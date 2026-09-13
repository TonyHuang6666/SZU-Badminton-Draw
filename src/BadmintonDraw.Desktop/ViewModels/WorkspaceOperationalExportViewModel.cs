using System.ComponentModel;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class WorkspaceOperationalExportViewModel : ViewModelBase, IDisposable
{
    private readonly AppShellViewModel shell;
    private readonly Func<Task<string?>> pickOutput;
    private WorkspaceSession session;
    private long generation, sessionGeneration;
    private bool disposed, working, applying, includePending, overwrite, confirmed, refreshingChoices;
    private string output = "", state = "请核对项目、日期与输出目录，再明确导出；不会移动比赛。", details = "";
    private int pdfRows = 1, pdfColumns = 1;
    private OperationalProjectChoice? selectedProject;
    private OperationalDayChoice? selectedCarryover;
    public WorkspaceOperationalExportViewModel(AppShellViewModel shell, WorkspaceSession session, Func<Task<string?>> pickOutputDirectory)
    {
        this.shell = shell; this.session = session; pickOutput = pickOutputDirectory;
        PickOutputCommand = new(PickAsync, () => Usable && !working && !shell.IsBusy);
        ExportCommand = new(ExportAsync, CanExport); RebuildChoices(initial: true);
    }
    public IReadOnlyList<OperationalProjectChoice> ProjectChoices { get; private set; } = Array.Empty<OperationalProjectChoice>();
    public IReadOnlyList<OperationalDayChoice> Days { get; private set; } = Array.Empty<OperationalDayChoice>();
    public IReadOnlyList<OperationalDayChoice> PendingDays => Array.AsReadOnly(Days.Where(d => d.IsSelected).ToArray());
    public OperationalProjectChoice? SelectedProject { get => selectedProject; set { if (!refreshingChoices && SetProperty(ref selectedProject, value)) Edited(); } }
    public OperationalDayChoice? SelectedCarryoverDay { get => selectedCarryover; set { if (!refreshingChoices && SetProperty(ref selectedCarryover, value)) Edited(); } }
    public string OutputDirectory { get => output; set { if (SetProperty(ref output, value ?? "")) Edited(); } }
    public bool IncludePendingCarryover { get => includePending; set { if (SetProperty(ref includePending, value)) { if (!value) { selectedCarryover = null; OnPropertyChanged(nameof(SelectedCarryoverDay)); } Edited(); } } }
    public bool OverwriteExisting { get => overwrite; set { if (SetProperty(ref overwrite, value)) Edited(resetOverwrite: false); } }
    public bool ScopeConfirmed { get => confirmed; set { if (SetProperty(ref confirmed, value)) { generation++; ChangedDuringWork(); RefreshAvailability(); } } }
    public int PdfRows { get => pdfRows; set { if (SetProperty(ref pdfRows, value)) Edited(); } }
    public int PdfColumns { get => pdfColumns; set { if (SetProperty(ref pdfColumns, value)) Edited(); } }
    public bool IsWorking => working;
    public string StateMessage { get => state; private set => SetProperty(ref state, value); }
    public string OutcomeDetails { get => details; private set => SetProperty(ref details, value); }
    public string SourceDetails => $"工作区：{session.Workspace.Id:D}\n{session.WorkspacePath}\n源修订：{session.Workspace.Revision}" +
        (session.RequiresReload ? "\n最后已知快照，必须重新载入后操作。" : "");
    public string ScopeSummary => $"范围：{SelectedProject?.Label ?? "未选择有效项目"}\n日期：{string.Join("、", Days.Where(d => d.IsSelected).Select(d => d.Label))}\n" +
        $"待填记录目标日：{(IncludePendingCarryover ? SelectedCarryoverDay?.Label ?? "尚未选择" : "不纳入")}；淘汰赛 PDF：{PdfRows} 行 × {PdfColumns} 列\n" +
        "日期仅限制记录材料；带时间抽签图覆盖所选项目全图，质量报告检查整个工作区。";
    public OperationalPackageOutcome? Outcome { get; private set; }
    public OperationalPackageExportException? ExportFailure { get; private set; }
    public WorkspaceError? Error { get; private set; }
    public IReadOnlyList<OperationalPackageOutput> Outputs => Outcome?.Outputs ?? ExportFailure?.Outputs ?? Array.Empty<OperationalPackageOutput>();
    public AsyncCommand PickOutputCommand { get; }
    public AsyncCommand ExportCommand { get; }
    private bool Usable => !disposed && ReferenceEquals(shell.CurrentSession, session) && !session.RequiresReload &&
        session.Workspace.Purpose == TournamentPurpose.FullTournament && session.Workspace.Stage >= TournamentStage.ScheduleReady && session.Workspace.Schedule is not null;
    private bool CanExport() => Usable && !working && !shell.IsBusy && ScopeConfirmed && !string.IsNullOrWhiteSpace(OutputDirectory) &&
        SelectedProject is not null && ProjectChoices.Contains(SelectedProject) && Days.Any(d => d.IsSelected) && PdfRows > 0 && PdfColumns > 0 &&
        (!IncludePendingCarryover || SelectedCarryoverDay is not null && Days.Contains(SelectedCarryoverDay) && SelectedCarryoverDay.IsSelected);
    private void Edited(bool resetOverwrite = true)
    {
        generation++; ResetConsent(resetOverwrite); ChangedDuringWork(); OnPropertyChanged(nameof(ScopeSummary)); RefreshAvailability();
    }
    private void ChangedDuringWork()
    {
        if (applying) StateMessage = "输入已改变；已发起的导出不会因此回滚，请以正式工作区和实际文件为准，重新核对后再操作。";
        else if (working) StateMessage = "选择期间输入或来源已改变，保留当前草稿；请重新选择。";
    }
    private void ResetConsent(bool resetOverwrite = true)
    {
        confirmed = false; OnPropertyChanged(nameof(ScopeConfirmed));
        if (resetOverwrite) { overwrite = false; OnPropertyChanged(nameof(OverwriteExisting)); }
    }
    private void DayChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(OperationalDayChoice.IsSelected)) return;
        if (selectedCarryover is not null && !selectedCarryover.IsSelected) { selectedCarryover = null; OnPropertyChanged(nameof(SelectedCarryoverDay)); }
        OnPropertyChanged(nameof(PendingDays)); Edited();
    }
    private void RebuildChoices(bool initial)
    {
        // Replacing ComboBox items synchronously writes transient null selections back.
        // Those binding updates are not a user's edit and must not invalidate an own-save completion.
        refreshingChoices = true;
        try
        {
            var previousDates = Days.ToDictionary(d => d.Day, d => d.IsSelected);
            foreach (var old in Days) old.PropertyChanged -= DayChanged;
            var projectId = selectedProject?.ProjectId; var hadProject = selectedProject is not null;
            ProjectChoices = Array.AsReadOnly(new[] { new OperationalProjectChoice(null, "全项目") }.Concat(session.Workspace.Projects.Select(p =>
                new OperationalProjectChoice(p.Id, $"{p.DisplayName} [{p.Id:D}]"))).ToArray());
            selectedProject = initial ? ProjectChoices[0] : hadProject ? ProjectChoices.FirstOrDefault(p => p.ProjectId == projectId) : null;
            var targetDate = selectedCarryover?.Day;
            Days = Array.AsReadOnly((session.Workspace.Schedule?.Resources.Days ?? []).Select(d =>
                new OperationalDayChoice(d.Date, initial || previousDates.GetValueOrDefault(d.Date))).ToArray());
            foreach (var day in Days) day.PropertyChanged += DayChanged;
            selectedCarryover = Days.FirstOrDefault(d => d.Day == targetDate && d.IsSelected);
            foreach (var name in new[] { nameof(ProjectChoices), nameof(SelectedProject), nameof(Days), nameof(PendingDays), nameof(SelectedCarryoverDay), nameof(ScopeSummary) }) OnPropertyChanged(name);
        }
        finally { refreshingChoices = false; }
    }
    public void RefreshSession(WorkspaceSession next)
    {
        if (disposed || ReferenceEquals(session, next)) return;
        session = next; sessionGeneration++; if (!applying) generation++;
        RebuildChoices(initial: false); ResetConsent(); ClearEvidence();
        StateMessage = "工作区已更新或重新打开，保留有效草稿；请重新核对范围并确认。";
        OnPropertyChanged(nameof(SourceDetails)); RefreshAvailability();
    }
    private bool Current(long token, WorkspaceSession captured) => !disposed && token == generation && ReferenceEquals(session, captured) && ReferenceEquals(shell.CurrentSession, captured);
    private async Task PickAsync()
    {
        var token = generation; var captured = session; SetWorking(true);
        try { var picked = await pickOutput(); if (Current(token, captured) && picked is not null) OutputDirectory = picked; }
        catch (Exception error) { if (Current(token, captured)) ShowError(ToError(error), null, exportAttempted: false); }
        finally { SetWorking(false); }
    }
    private async Task ExportAsync()
    {
        var captured = session; var token = generation; var refreshes = sessionGeneration;
        var request = new OperationalExportRequest(OutputDirectory, SelectedProject!.ProjectId, Days.Where(d => d.IsSelected).Select(d => d.Day).ToArray(),
            IncludePendingCarryover ? SelectedCarryoverDay!.Day : null, OverwriteExisting, new(PdfRows, PdfColumns));
        ResetConsent(); ClearEvidence(); applying = true; SetWorking(true);
        StateMessage = "正在生成并发布材料；输出与工作区审计分别核对，修改输入不会撤销已发起操作。";
        try
        {
            var execution = await shell.ExportOperationalPackageAsync(captured, request);
            if (disposed || token != generation) return;
            var completion = ReferenceEquals(session, execution.CompletionSession) && ReferenceEquals(shell.CurrentSession, session);
            if (execution.Outcome is { } outcome)
            {
                if (!completion || sessionGeneration - refreshes > 1 || !ReferenceEquals(session.Workspace, outcome.Command.Workspace) || session.WorkspacePath != outcome.Command.WorkspacePath) return;
                Outcome = outcome; StateMessage = "材料已发布，成功导出审计已保存；赛果与赛程未因导出改变。";
                OutcomeDetails = Describe(outcome.SourceRevision, outcome.AuditId, outcome.ExportedAt, outcome.Scope, outcome.Counts, outcome.Outputs, outcome.Skips) +
                    $"\n正式文件：{outcome.Command.WorkspacePath}；保存后修订：{outcome.Command.Workspace.Revision}" +
                    (outcome.Command.BackupPath is { } backup ? "\n审计保存备份：" + backup : "");
                EvidenceChanged();
            }
            else if (execution.Error is { } error)
            {
                var ownCommit = error.Committed && completion && sessionGeneration - refreshes == 1 && session.WorkspacePath == captured.WorkspacePath && session.Workspace.Id == captured.Workspace.Id;
                if (error.Committed ? ownCommit : Current(token, captured)) ShowError(error, execution.ExportFailure);
            }
        }
        catch (Exception error) { if (Current(token, captured)) ShowError(ToError(error), null); }
        finally { applying = false; SetWorking(false); }
    }
    private void ShowError(WorkspaceError error, OperationalPackageExportException? failure, bool exportAttempted = true)
    {
        Error = error; ExportFailure = failure; Outcome = null;
        StateMessage = $"[{error.Code}] {error.Message}\n" + (!exportAttempted ? "目录选择失败；未发起材料导出或保存审计。" :
            error.Committed ? "导出审计已保存；重读失败不代表回滚，请检查当前快照或重新载入。" : "未保存成功导出审计；文件可能已经部分发布，请核对以下实际证据。");
        OutcomeDetails = StateMessage + (failure is null ? "" : "\n" + Describe(failure.SourceRevision, failure.AuditId, failure.ExportedAt,
            failure.Scope, failure.Counts, failure.Outputs, failure.Skips) +
            (failure.AttemptedOutputPath is { } attempted ? "\n尝试发布，最终状态未确认（可能已存在或被替换）：" + attempted : "") +
            (failure.RetainedStagingDirectory is { } staging ? "\n保留的暂存目录：" + staging : "")) +
            (error.CandidatePath is { } candidate ? "\n候选文件：" + candidate : "") +
            (error.BackupPath is { } backup ? "\n保留的备份路径（不单凭路径证明完整性）：" + backup : "") +
            (error.SchedulingFailure is { } scheduling ? "\n" + string.Join("\n", scheduling.Violations.Select(v =>
                $"[{v.Code}] {v.Message}；项目：{v.ProjectId}；比赛：{v.MatchId}；关联比赛：{v.RelatedMatchId}")) : "");
        EvidenceChanged();
    }
    private static string Describe(long? revision, Guid auditId, DateTimeOffset? time, OperationalPackageScope? scope,
        OperationalPackageCounts? counts, IReadOnlyList<OperationalPackageOutput> outputs, IReadOnlyList<OperationalPackageSkip> skips) =>
        $"源修订：{revision?.ToString() ?? "尚未确定"}；审计 ID（仅以实际保存状态为准）：{auditId:D}；导出时间：{time?.ToString("O") ?? "尚未确定"}\n" +
        (scope is null ? "范围尚未确定" : $"实际项目：{string.Join("、", scope.ProjectIds)}\n记录日期：{string.Join("、", scope.Days.Select(d => d.ToString("yyyy-MM-dd")))}；待填目标：{scope.PendingCarryoverDay?.ToString("yyyy-MM-dd") ?? "无"}") + "\n" +
        (counts is null ? "计数尚未确定" : $"不同比赛：{counts.DistinctMatchCount}；逻辑记录行：{counts.RecordRowCount}；待填补录：{counts.PendingCarryoverCount}；全项目带时间图场次：{counts.TimedDrawMatchCount}；必需产物：{counts.RequiredOutputCount}") +
        $"\n已验证发布：{outputs.Count}\n" + string.Join("\n\n", outputs.Select(o =>
            $"{o.Kind}；项目：{o.ProjectId?.ToString("D") ?? "全工作区/合并"}；记录日期：{o.RecordDay?.ToString("yyyy-MM-dd") ?? "全图/不适用"}\n{o.Path}\n字节：{o.ByteLength}；SHA-256：{o.Sha256}")) +
        "\n跳过项：\n" + string.Join("\n", skips.Select(s => $"[{s.Code}] {s.Message}；项目：{s.ProjectId}；日期：{s.RecordDay:yyyy-MM-dd}"));
    private static WorkspaceError ToError(Exception error) => error is WorkspaceCommandException command ? command.Error : new("desktop.operation-failed", error.Message);
    private void ClearEvidence() { Outcome = null; ExportFailure = null; Error = null; OutcomeDetails = ""; EvidenceChanged(); }
    private void EvidenceChanged() { foreach (var name in new[] { nameof(Outcome), nameof(ExportFailure), nameof(Error), nameof(Outputs) }) OnPropertyChanged(name); }
    private void SetWorking(bool value) { working = value; if (!disposed) { OnPropertyChanged(nameof(IsWorking)); RefreshAvailability(); } }
    public void RefreshAvailability() { PickOutputCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged(); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; generation++; foreach (var day in Days) day.PropertyChanged -= DayChanged; ResetConsent(); RefreshAvailability();
    }
}

public sealed record OperationalProjectChoice(Guid? ProjectId, string Label);
public sealed class OperationalDayChoice(DateOnly day, bool selected) : ViewModelBase
{
    private bool selected = selected;
    public DateOnly Day { get; } = day;
    public string Label => Day.ToString("yyyy-MM-dd");
    public bool IsSelected { get => selected; set => SetProperty(ref selected, value); }
}
