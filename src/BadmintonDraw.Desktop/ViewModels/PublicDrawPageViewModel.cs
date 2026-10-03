using System.Collections.ObjectModel;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Workflows;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class PublicDrawPageViewModel : WorkspacePageViewModel, IDisposable
{
    internal AppShellViewModel Shell { get; }
    private readonly Func<Task<string?>> outputPicker;
    private readonly Func<DrawExportOptionsViewModel, Task<bool>> configureExport;
    private ProjectDrawViewModel? selectedProject;
    private int exportFormatIndex;
    private string pdfRowsText = "1", pdfColumnsText = "1", exportDetails = "";
    private long exportGeneration;
    private bool exporting, configuringExport, disposed;
    internal bool CanStartExport => !disposed && !exporting && !configuringExport && Shell.CanMutate;
    internal bool CanReviewPendingDraws => !disposed && !Shell.IsBusy;
    public ObservableCollection<ProjectDrawViewModel> Projects { get; } = [];
    public ProjectDrawViewModel? SelectedProject
    {
        get => selectedProject;
        set { if (SetProperty(ref selectedProject, value)) { exportGeneration++; RefreshAvailability(); } }
    }
    public IReadOnlyList<string> ExportFormats { get; } = ["Excel 工作簿", "A4 PDF", "PNG 图片", "JPEG 图片", "全部格式"];
    public int ExportFormatIndex { get => exportFormatIndex; set { if (SetProperty(ref exportFormatIndex, value)) { exportGeneration++; OnPropertyChanged(nameof(UsesPdf)); } } }
    public bool UsesPdf => ExportFormatIndex is 1 or 4;
    public string PdfRowsText { get => pdfRowsText; set { if (SetProperty(ref pdfRowsText, value)) exportGeneration++; } }
    public string PdfColumnsText { get => pdfColumnsText; set { if (SetProperty(ref pdfColumnsText, value)) exportGeneration++; } }
    public string ExportDetails { get => exportDetails; private set { if (SetProperty(ref exportDetails, value)) OnPropertyChanged(nameof(HasExportDetails)); } }
    public bool HasExportDetails => !string.IsNullOrWhiteSpace(ExportDetails);
    public bool HasExportableDraw => SelectedProject?.HasPreview == true;
    public bool IsSchedulePrimary => AllDrawsConfirmed && !CanUpgrade;
    public bool IsExportPrimary => SelectedProject?.IsConfirmed == true && !IsSchedulePrimary;
    public string Readiness => $"{Session.Workspace.Projects.Count(p => p.Draw?.ConfirmedAt is not null)} / {Session.Workspace.Projects.Count} 个项目抽签已确认";
    public bool AllDrawsConfirmed => Session.Workspace.Projects.Count > 0 && Session.Workspace.Projects.All(p => p.Draw?.ConfirmedAt is not null);
    public IReadOnlyList<ProjectDrawViewModel> PendingProjects => Projects.Where(project => !project.IsConfirmed).ToArray();
    public bool HasPendingDraws => PendingProjects.Count > 0;
    public string PendingDrawSummary => $"还有 {PendingProjects.Count} 项未确认 · 查看";
    public string SchedulingHint => HasPendingDraws
        ? $"还有 {PendingProjects.Count} 个项目未确认抽签。点击下方项目继续处理，全部确认后即可安排赛程。"
        : "全部项目的抽签已确认，可以继续安排比赛时间；无需先导出文件。";
    public bool HasMultipleProjects => Projects.Count > 1;
    public string CompletionHint => !AllDrawsConfirmed ? "请逐个项目完成抽签并确认，全部完成后即可安排比赛时间。"
        : CanUpgrade ? "公开抽签已完成。导出后可以结束本次工作；以后打开赛事文件，仍可继续筹备。"
        : "全部项目的抽签已确认，可以继续安排比赛时间，也可以导出结果；无需先导出文件。";
    public bool CanUpgrade => Session.Workspace.Purpose == TournamentPurpose.PublicDrawOnly;
    public AsyncCommand ExportAllPreviewCommand { get; }
    public AsyncCommand ExportAllConfirmedCommand { get; }
    public AsyncCommand OpenExportCommand { get; }
    public AsyncCommand UpgradeCommand { get; }
    public AsyncCommand ContinueToScheduleCommand { get; }
    public DelegateCommand BackCommand { get; }

    public PublicDrawPageViewModel(AppShellViewModel shell, WorkspaceSession session, Func<Task<string?>> outputPicker,
        Func<DrawExportOptionsViewModel, Task<bool>>? configureExport = null) : base(session)
    {
        Shell = shell; this.outputPicker = outputPicker;
        this.configureExport = configureExport ?? (_ => Task.FromResult(false));
        OpenExportCommand = new(ConfigureExportAsync, () => CanStartExport && HasExportableDraw, shell.ReportError);
        ContinueToScheduleCommand = new(async () =>
        {
            if (CanUpgrade && !await shell.RunWorkspaceCommandAsync(Session, (workflow, revision) => workflow.UpgradeToFullTournament(revision),
                "已继续筹备整场比赛；抽签结果保持不变，接下来安排时间和场地。")) return;
            shell.Navigate(WorkspaceRoute.ScheduleSetup);
        }, () => shell.CanMutate && AllDrawsConfirmed && (CanUpgrade || shell.CanNavigate(WorkspaceRoute.ScheduleSetup)), shell.ReportError);
        BackCommand = new(() => shell.Navigate(WorkspaceRoute.Rosters), () => shell.CanNavigate(WorkspaceRoute.Rosters));
        ExportAllPreviewCommand = new(() => ExportAsync(null, DrawExportState.Preview), () => CanExportAll(false), shell.ReportError);
        ExportAllConfirmedCommand = new(() => ExportAsync(null, DrawExportState.Confirmed), () => CanExportAll(true), shell.ReportError);
        UpgradeCommand = new(async () => await shell.RunWorkspaceCommandAsync(Session, (workflow, revision) => workflow.UpgradeToFullTournament(revision),
            "已升级为完整赛事筹备；已确认抽签保持不变，尚未生成赛程。"), () => shell.CanMutate && CanUpgrade, shell.ReportError);
        RefreshProjects();
    }
    private bool CanExportAll(bool confirmed) => CanStartExport && Projects.Count > 0 &&
        Session.Workspace.Projects.All(p => p.Draw is not null && (p.Draw.ConfirmedAt is not null) == confirmed);
    private async Task ConfigureExportAsync()
    {
        if (!CanStartExport || SelectedProject is not { HasPreview: true } selected) return;
        var expected = Session;
        var generation = exportGeneration;
        bool Current() => !disposed && generation == exportGeneration && ReferenceEquals(Session, expected)
            && ReferenceEquals(Shell.CurrentSession, expected);
        var scopes = new List<DrawExportScope>
        {
            new(selected.ProjectId, $"当前项目：{selected.DisplayLabel}", selected.IsConfirmed ? DrawExportState.Confirmed : DrawExportState.Preview)
        };
        if (HasMultipleProjects)
        {
            DrawExportState? allState = AllDrawsConfirmed ? DrawExportState.Confirmed
                : expected.Workspace.Projects.All(p => p.Draw is { ConfirmedAt: null }) ? DrawExportState.Preview : null;
            scopes.Add(new(null, $"全部项目（{Projects.Count} 项）", allState));
        }
        // The choices belong to this snapshot; later changes must cancel this request, not silently change its scope.
        var options = new DrawExportOptionsViewModel(scopes.AsReadOnly(), HasMultipleProjects && AllDrawsConfirmed ? 1 : 0,
            ExportFormatIndex, PdfRowsText, PdfColumnsText);
        configuringExport = true; RefreshAvailability();
        DrawExportSelection? selection = null;
        try
        {
            if (!await configureExport(options) || !Current()) return;
            if (!options.TryCreateSelection(out selection) || selection is null)
            {
                Shell.ReportError(new WorkspaceCommandException(new("draw.export-options", "请检查导出范围、格式和 PDF 分页设置。")));
                return;
            }
        }
        catch (Exception) when (!Current()) { return; }
        finally { configuringExport = false; if (!disposed) RefreshAvailability(); }
        if (!Current() || selection is null) return;
        ExportFormatIndex = selection.FormatIndex;
        PdfRowsText = selection.PdfRows.ToString(); PdfColumnsText = selection.PdfColumns.ToString();
        await ExportAsync(selection.ProjectId, selection.State);
    }
    internal async Task ExportAsync(Guid? projectId, DrawExportState state)
    {
        if (!CanStartExport) return;
        var expected = Session;
        var generation = exportGeneration;
        bool Current() => !disposed && generation == exportGeneration && ReferenceEquals(Session, expected)
            && ReferenceEquals(Shell.CurrentSession, expected);
        var format = ExportFormatIndex switch { 0 => WorkflowExportFormat.Excel, 1 => WorkflowExportFormat.A4Pdf, 2 => WorkflowExportFormat.Png,
            3 => WorkflowExportFormat.Jpeg, 4 => WorkflowExportFormat.All, _ => throw new WorkspaceCommandException(new("draw.export-format", "请选择导出格式。")) };
        var rows = 1; var columns = 1;
        if (UsesPdf && (!int.TryParse(PdfRowsText, out rows) || rows < 1 || !int.TryParse(PdfColumnsText, out columns) || columns < 1))
            throw new WorkspaceCommandException(new("draw.export-layout", "PDF 拼页行数和列数应为正整数。"));
        exporting = true; RefreshAvailability();
        ExportDetails = "";
        try
        {
            var output = await outputPicker();
            if (output is null || !Current()) return;
            var request = new DrawExportRequest(output, format, state, new(rows, columns));
            var preview = await Shell.RunWorkspaceQueryAsync(expected,
                (workflow, revision) => workflow.PreviewDrawExportConflicts(projectId, request, revision), background: true, showStatus: false);
            if (!Current()) return;
            if (!preview.Succeeded || preview.Value is null)
            {
                if (preview.Error is { } error) Shell.ReportError(new WorkspaceCommandException(error));
                ExportDetails = "导出前检查未通过，没有生成或替换文件。请查看下方错误详情。";
                return;
            }
            var conflicts = preview.Value.ToArray();
            if (conflicts.Length > 0)
            {
                var accepted = await Shell.ConfirmExportOverwriteAsync(Array.AsReadOnly(conflicts));
                if (!Current()) return;
                if (!accepted) { ExportDetails = "已取消导出，原有文件未改变。可以选择其他文件夹后再导出。"; return; }
            }
            // An empty list also limits consent: a new collision after the check must be rejected.
            request = request with { ConfirmedOverwritePaths = conflicts };
            DrawPackageExportResult? result = null; DrawPackageExportException? failure = null;
            var success = await Shell.RunWorkspaceCommandAsync(expected, (workflow, revision) =>
            {
                try { result = workflow.ExportDrawPackage(projectId, request, revision); return result.Command; }
                catch (DrawPackageExportException exception) { failure = exception; throw new WorkspaceCommandException(exception.Error, exception); }
            }, state == DrawExportState.Preview ? "待确认的抽签结果已导出，抽签仍需单独确认。" : "正式抽签材料已导出，导出记录已保存。");
            if (disposed) return;
            ExportDetails = success && result is not null ? "已导出，导出记录已保存：" + Environment.NewLine +
                string.Join(Environment.NewLine, result.Outputs.Select(item => item.Path)) : failure is not null
                ? DrawExportFeedback.Describe(failure) : "导出未完成，请查看下方错误详情。";
        }
        catch (Exception) when (!Current()) { /* A closed page or changed request cannot report stale picker/dialog errors. */ }
        finally { exporting = false; if (!disposed) RefreshAvailability(); }
    }
    public override void RefreshSession(WorkspaceSession next)
    {
        if (!ReferenceEquals(Session, next)) exportGeneration++;
        if (Session.Workspace.Revision != next.Workspace.Revision)
        {
            foreach (var project in Projects) project.AcknowledgeInvalidation = false;
        }
        base.RefreshSession(next); RefreshProjects();
        foreach (var property in new[] { nameof(Readiness), nameof(CanUpgrade), nameof(AllDrawsConfirmed), nameof(CompletionHint), nameof(HasMultipleProjects),
            nameof(PendingProjects), nameof(HasPendingDraws), nameof(PendingDrawSummary), nameof(SchedulingHint) }) OnPropertyChanged(property);
    }
    private void RefreshProjects()
    {
        var selectedId = SelectedProject?.ProjectId;
        var retained = Projects.ToDictionary(p => p.ProjectId);
        var next = Session.Workspace.Projects.OrderBy(p => p.SortOrder).Select(project =>
        {
            if (retained.TryGetValue(project.Id, out var existing)) { existing.Refresh(project); return existing; }
            return new ProjectDrawViewModel(this, project);
        }).ToArray();
        Projects.Clear(); foreach (var item in next) Projects.Add(item);
        SelectedProject = Projects.FirstOrDefault(p => p.ProjectId == selectedId) ?? Projects.FirstOrDefault();
        RefreshAvailability();
    }
    public override void RefreshAvailability()
    {
        foreach (var property in new[] { nameof(HasExportableDraw), nameof(IsExportPrimary), nameof(IsSchedulePrimary) }) OnPropertyChanged(property);
        foreach (var project in Projects) project.RefreshAvailability();
        OpenExportCommand?.NotifyCanExecuteChanged();
        ExportAllPreviewCommand?.NotifyCanExecuteChanged(); ExportAllConfirmedCommand?.NotifyCanExecuteChanged(); UpgradeCommand?.NotifyCanExecuteChanged();
        ContinueToScheduleCommand?.NotifyCanExecuteChanged(); BackCommand?.NotifyCanExecuteChanged();
    }
    public void Dispose() { disposed = true; exportGeneration++; RefreshAvailability(); }
}

internal static class DrawExportFeedback
{
    internal static string Describe(DrawPackageExportException failure) => "导出未完整完成。" + Environment.NewLine +
        (failure.Outputs.Count == 0 ? "没有文件成功发布。" : "以下文件已发布，可使用（不代表整个材料包成功）：" + Environment.NewLine +
            string.Join(Environment.NewLine, failure.Outputs.Select(item => item.Path))) + Environment.NewLine +
        (failure.AuditRecorded ? "导出审计已写入，但重新读取失败；请重新载入工作区。" : "本次完整导出审计未写入工作区。") +
        (failure.RetainedStagingDirectory is { } staging ? Environment.NewLine + "保留的临时目录：" + staging : "");
}
