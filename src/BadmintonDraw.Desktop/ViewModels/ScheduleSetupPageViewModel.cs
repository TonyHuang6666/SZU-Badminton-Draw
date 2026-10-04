using System.Collections.ObjectModel;
using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed record ScheduleSetupRequest(TournamentResourcePlan Resources, TournamentSchedulingPolicy Policy);
public sealed record ScheduleCapacityEstimate(int MatchCount, long AvailableMinutes, long RequiredMinutes)
{
    public bool IsInsufficient => AvailableMinutes < RequiredMinutes;
    public long EquivalentMatches => RequiredMinutes > 0 ? AvailableMinutes * MatchCount / RequiredMinutes : 0;
}

public sealed partial class ScheduleSetupPageViewModel : WorkspacePageViewModel, IDisposable
{
    private readonly AppShellViewModel shell;
    private string baseline = "", refereeCountText = "", minimumRestText = "30", dailyMaximumText = "4";
    private bool edited, conflict, synchronizeStageWaves;
    private int strategyIndex;
    private SchedulingFailure? failure;
    private string successSummary = "";
    public ObservableCollection<ScheduleDayEditorViewModel> Days { get; } = [];
    public event Action<ScheduleDayEditorViewModel>? DayAdded;
    public ObservableCollection<ScheduleProjectTimingViewModel> ProjectTimings { get; } = [];
    public IReadOnlyList<string> Strategies { get; } = ["尽快完成", "时间安排均衡", "重要比赛集中在最后一天", "自定义安排"];
    public bool IsSingleProject => Session.Workspace.Projects.Count == 1;
    public bool IsMultiProject => !IsSingleProject;
    public string ScheduleSummary => $"{Session.Workspace.Projects.Count} 个项目 · 共 {Session.Workspace.Projects.Sum(p => p.MatchGraph?.Matches.Count ?? 0)} 场比赛";
    public ScheduleCapacityEstimate? CapacityEstimate
    {
        get
        {
            try
            {
                var setup = BuildSetup(); var days = setup.Resources.Days;
                if (days.Select(d => d.Date).Distinct().Count() != days.Count || days.Any(d =>
                    d.DayStart >= d.DayEnd || d.Courts.Count == 0 || d.Courts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != d.Courts.Count ||
                    (d.UnavailableCourtWindows ?? []).Any(w => w.StartTime >= w.EndTime || w.StartTime < d.DayStart || w.EndTime > d.DayEnd || w.Courts.Any(c => !d.Courts.Contains(c, StringComparer.OrdinalIgnoreCase))) ||
                    (d.RefereeCapacityWindows ?? []).Any(w => w.StartTime >= w.EndTime || w.StartTime < d.DayStart || w.EndTime > d.DayEnd))) return null;
                var matches = Session.Workspace.Projects.SelectMany(p => p.MatchGraph?.Matches ?? []).ToArray();
                return new(matches.Length, days.Sum(d => (long)ScheduleResourceCalculator.CalculateDayCapacityMinutes(setup.Resources, d)),
                    matches.Sum(m => (long)ScheduleTimingResolver.Resolve(m, setup.Policy)));
            }
            catch (WorkspaceCommandException) { return null; }
        }
    }
    public string CapacitySummary => CapacityEstimate is not { } estimate
        ? "请填写有效的日期、时段、场地和时长后查看容量估算。"
        : $"按场地与裁判计算，可用时间共 {estimate.AvailableMinutes} 分钟；{estimate.MatchCount} 场比赛预计需要 {estimate.RequiredMinutes} 分钟。折合约 {estimate.EquivalentMatches} 场（按平均时长估算）。" +
            (estimate.IsInsufficient ? " 当前总容量不足，请增加日期、时间或资源。" : " 总量可容纳；仍需检查选手冲突、休息和比赛先后顺序。") +
            " 容量估算不保证能排下全部比赛。";
    public string StrategyDescription => StrategyIndex switch
    {
        0 => "尽可能使用较早的可用时段，让比赛更早结束；仍会为选手保留休息时间。",
        1 => "尽量把比赛分散到各个比赛日，减轻单日比赛压力。",
        2 => "尽量把决赛等重要比赛留到最后一天，具体安排仍取决于场地和选手的可用时间。",
        _ => "使用高级设置中的每日负载、阶段进度和决赛日偏好。所有项目仍共用同一份赛程。"
    };
    public string PolicyLabel => Session.Workspace.Projects.Count == 1 ? "项目完成节奏" : "全赛事编排策略";
    public string RefereeCountText { get => refereeCountText; set { if (SetProperty(ref refereeCountText, value)) Edited(); } }
    public string MinimumRestText { get => minimumRestText; set { if (SetProperty(ref minimumRestText, value)) Edited(); } }
    public string DailyMaximumText { get => dailyMaximumText; set { if (SetProperty(ref dailyMaximumText, value)) Edited(); } }
    public int StrategyIndex { get => strategyIndex; set { if (SetProperty(ref strategyIndex, value)) { OnPropertyChanged(nameof(StrategyDescription)); Edited(); } } }
    public bool SynchronizeStageWaves { get => synchronizeStageWaves; set { if (SetProperty(ref synchronizeStageWaves, value)) Edited(); } }
    public bool HasEditorConflict => conflict;
    public bool CanEdit => !disposed && shell.CanMutate && !conflict && Session.Workspace.Purpose == TournamentPurpose.FullTournament && Session.Workspace.Results.Count == 0 &&
        Session.Workspace.Stage is TournamentStage.DrawsConfirmed or TournamentStage.ScheduleReady;
    public string EditHint => conflict ? "赛程、抽签或资源已被其他操作更改。输入仍保留；请载入最新设置后再编排。" : Session.Workspace.Results.Count > 0
        ? "已有赛果，不能重新生成整场赛程；请到赛程板调整未完成场次。" : "修改设置不会自动重排。点击生成后才保存；失败时，已保存的数据保持不变。";
    public SchedulingFailure? Failure { get => failure; private set { if (SetProperty(ref failure, value)) { OnPropertyChanged(nameof(FailureDetails)); OnPropertyChanged(nameof(FailureSummary)); OnPropertyChanged(nameof(FailureAdvice)); OnPropertyChanged(nameof(FailureTechnicalDetails)); OnPropertyChanged(nameof(HasFailure)); } } }
    public bool HasFailure => Failure is not null;
    public string SuccessSummary { get => successSummary; private set { if (SetProperty(ref successSummary, value)) OnPropertyChanged(nameof(HasSuccessSummary)); } }
    public bool HasSuccessSummary => SuccessSummary.Length > 0;
    public string FailureSummary
    {
        get
        {
            if (Failure is not { } detail) return "";
            var summary = detail.Diagnostics?.FailureKind switch
            {
                SchedulingFailureKind.InvalidInput => "排程设置有误，请检查输入。",
                SchedulingFailureKind.Canceled => "本次赛程生成已取消。",
                SchedulingFailureKind.ProvenInfeasible => CapacityFailureSummary(detail),
                SchedulingFailureKind.ValidationIncomplete => "本次约束验证未完成，不能确认完整赛程；这不表示赛事无法编排。",
                _ => "本次搜索未完成，尚未找到满足全部条件的完整赛程；这不表示赛事无法编排。"
            };
            return summary + " 原赛程没有改变。";
        }
    }
    public string FailureAdvice => Failure?.Diagnostics?.FailureKind switch
    {
        SchedulingFailureKind.InvalidInput => "请检查日期、时段、场地、裁判、预计时长及高级设置中的输入提示。",
        SchedulingFailureKind.Canceled => "输入仍保留，可以检查设置后重新生成。",
        SchedulingFailureKind.ProvenInfeasible when Failure.CapacityEvidence?.Kind == "PlayerDailyCap" => "请增加比赛日，或由赛事组织者复核每位选手的每日场次上限。场地和裁判数量不会改变该上限。",
        SchedulingFailureKind.ProvenInfeasible when Failure.CapacityEvidence?.Kind == "PlayerTime" => "请增加比赛日或延长每日可用时段，并复核比赛时长、最短休息和每日上限；场地和裁判数量不会增加同一选手的可用时间。",
        SchedulingFailureKind.ProvenInfeasible => "请增加比赛日、延长可用时段或增加可用场地与裁判，并检查不可用时段。",
        _ when Failure is not null => "可以检查设置后重试，或请赛事组织者复核比赛时长、休息和每日上限。本次没有自动修改任何参数。",
        _ => ""
    };

    private static string CapacityFailureSummary(SchedulingFailure detail)
    {
        if (detail.CapacityEvidence is not { } evidence || detail.Diagnostics is not { } diagnostics)
            return "已证明当前设置的容量不足。";
        var resources = diagnostics.Resources; var player = string.IsNullOrWhiteSpace(evidence.PlayerName) ? "该选手" : evidence.PlayerName;
        if (evidence.Kind == "PlayerDailyCap")
            return $"已证明每日场次上限不足：{player}在一种可能的晋级路径中至少需 {evidence.RequiredLowerBound} 场，" +
                $"当前容量上界为 {resources.Days.Count} 天 × 每天 {resources.MaxPlayerMatchesPerDay} 场 = {evidence.CapacityUpperBound} 场。";
        if (evidence.Kind == "PlayerTime" && evidence.WitnessMatchIds.Count > 0)
        {
            var count = evidence.WitnessMatchIds.Count;
            var minimumTicks = evidence.RequiredLowerBound / count;
            var capacityCount = minimumTicks > 0 ? evidence.CapacityUpperBound / minimumTicks : 0;
            return $"已证明选手时间容量不足：{player}在一种可能的晋级路径中至少需 {count} 场，" +
                $"按最短单场 {minimumTicks / (double)TimeSpan.TicksPerMinute:0.##} 分钟折算：" +
                $"{count} 场 × 单场时长 > 容量上界 {capacityCount} 场 × 单场时长。" +
                $"该上界已计入 {resources.MinimumRestMinutes} 分钟最短休息和每天 {resources.MaxPlayerMatchesPerDay} 场上限。";
        }
        return $"已证明场地与裁判时间容量不足：需要至少 {evidence.RequiredLowerBound / (double)TimeSpan.TicksPerMinute:0.##} 分钟，" +
            $"可用容量上界为 {evidence.CapacityUpperBound / (double)TimeSpan.TicksPerMinute:0.##} 分钟。";
    }
    public string FailureDetails => Failure is null ? "" : string.Join(Environment.NewLine,
        Failure.UnplacedMatches.Select(m => $"未安排：{m.ProjectName} · {m.MatchName}")
        .Concat(Failure.Violations.Select(v => v.Message))
        .Concat(Failure.Suggestions.Select(s => "建议：" + s)));
    public string FailureTechnicalDetails => Failure is null ? "" : string.Join(Environment.NewLine,
        CapturedFailureDetails(Failure).Concat(Failure.UnplacedMatches.Select(m => $"未安排：{m.ProjectName} · {m.MatchName} ({string.Join(", ", m.ConstraintCodes)})"))
        .Concat(Failure.Violations.Select(v => $"{v.Code}：{v.Message}"))
        .Concat(Failure.Suggestions.Select(s => "建议：" + s)));
    public AsyncCommand GenerateCommand { get; }
    public DelegateCommand AddDayCommand { get; }
    public DelegateCommand ResetCommand { get; }
    public DelegateCommand BoardCommand { get; }
    public DelegateCommand UseStrategyDefaultsCommand { get; }
    public ScheduleSetupPageViewModel(AppShellViewModel shell, WorkspaceSession session,
        Func<VenueCourtSelectionViewModel, Task<bool>>? chooseCourts = null,
        Func<UnavailableCourtSelectionViewModel, Task<bool>>? chooseUnavailable = null) : base(session)
    {
        this.shell = shell;
        this.chooseCourts = chooseCourts ?? (_ => Task.FromResult(false));
        this.chooseUnavailable = chooseUnavailable ?? (_ => Task.FromResult(false));
        GenerateCommand = new(async () =>
        {
            Failure = null; SuccessSummary = "";
            var request = BuildSetup(); var expected = Session;
            if (await shell.RunWorkspaceCommandAsync(expected, (workflow, revision) => workflow.GenerateSchedule(request.Resources, request.Policy, revision), "赛程已生成并自动保存，可以查看赛程板。"))
            {
                var result = shell.LastCommandResult;
                if (disposed || !ReferenceEquals(shell.CurrentPage, this) || result is null ||
                    !ReferenceEquals(Session.Workspace, result.Workspace) || Session.WorkspacePath != result.WorkspacePath) return;
                Load();
                SuccessSummary = BuildSuccessSummary(result.Workspace.Schedule, result.SchedulingQuality, result.SchedulingDiagnostics);
            }
            else if (!disposed && ReferenceEquals(shell.CurrentPage, this) && ReferenceEquals(Session, expected))
                Failure = shell.LastError?.SchedulingFailure;
        }, () => CanSelectCourts, shell.ReportError);
        AddDayCommand = new(AddNewDay, () => CanSelectCourts);
        ResetCommand = new(Load, () => !shell.IsBusy);
        BoardCommand = new(() => shell.Navigate(WorkspaceRoute.ScheduleBoard), () => shell.CanNavigate(WorkspaceRoute.ScheduleBoard));
        UseStrategyDefaultsCommand = new(() =>
        {
            foreach (var day in Days) { day.TargetLoadText = ""; day.WarningLoadText = ""; day.StageProgressText = ""; }
            foreach (var project in ProjectTimings) { project.FinalPreferenceIndex = 0; project.SemifinalPreferenceIndex = 0; project.BronzePreferenceIndex = 0; project.PlacementPreferenceIndex = 0; }
            SynchronizeStageWaves = false; Edited();
        }, () => CanEdit);
        Load();
    }
    public ScheduleSetupRequest BuildSetup()
    {
        if (conflict) throw ScheduleEditorInput.Error("请先载入最新设置，不能提交旧设置。");
        if (Days.Count == 0) throw ScheduleEditorInput.Error("请至少添加一个比赛日。");
        var days = Days.Select(d => d.Build()).OrderBy(d => d.Date).ToArray();
        var targets = new List<TournamentDayLoadTarget>(); var stages = new List<TournamentStageWaveTarget>();
        foreach (var day in Days)
        {
            var label = ScheduleEditorInput.Date(day.DateText).ToString("yyyy-MM-dd");
            var target = ScheduleEditorInput.Percent(day.TargetLoadText, "每日目标负载"); var warning = ScheduleEditorInput.Percent(day.WarningLoadText, "警戒负载");
            if (target.HasValue || warning.HasValue)
            {
                if (!target.HasValue) throw ScheduleEditorInput.Error("填写警戒负载时，也请填写对应的目标负载。");
                targets.Add(new(label, target.Value, warning ?? Math.Min(1, target.Value + .15)));
            }
            if (ScheduleEditorInput.Percent(day.StageProgressText, "累计阶段进度") is { } progress) stages.Add(new(label, progress));
        }
        var resources = new TournamentResourcePlan(days, string.IsNullOrWhiteSpace(RefereeCountText) ? null : ScheduleEditorInput.Integer(RefereeCountText, "裁判人数"),
            ScheduleEditorInput.Integer(MinimumRestText, "最小休息", 0), ScheduleEditorInput.Integer(DailyMaximumText, "每日场次上限"));
        if (StrategyIndex is < 0 or > 3) throw ScheduleEditorInput.Error("请选择全局编排策略。");
        var policy = new TournamentSchedulingPolicy((ScheduleAutoSchedulingStrategy)StrategyIndex, targets, SynchronizeStageWaves, stages, ProjectTimings.SelectMany(p => p.BuildFinalRules()).ToArray())
        { ProjectTimings = ProjectTimings.ToDictionary(p => p.ProjectId, p => p.BuildTiming()) };
        return new(resources, policy);
    }
    private void AddNewDay()
    {
        var latest = Days.Select(d => d.SelectedDate).Max();
        if (latest == DateTime.MaxValue.Date)
        {
            shell.ReportError(ScheduleEditorInput.Error("已有比赛日达到支持的最晚日期，无法再添加下一天。请先调整日期。"));
            return;
        }
        var date = DateOnly.FromDateTime(latest?.AddDays(1) ?? DateTime.Today);
        var day = AddDay(new(date, new(9, 0), new(18, 0), []), null);
        Edited();
        DayAdded?.Invoke(day);
    }
    private ScheduleDayEditorViewModel AddDay(ScheduleDaySettings day, TournamentSchedulingPolicy? policy)
    {
        var target = policy?.DayLoadTargets.FirstOrDefault(t => t.DayLabel == day.DayLabel);
        var editor = new ScheduleDayEditorViewModel(day, target?.TargetUtilization, target?.WarningUtilization, policy?.StageWaveTargets.FirstOrDefault(t => t.DayLabel == day.DayLabel)?.CumulativeProgress,
            Edited, item => { Days.Remove(item); Edited(); }, day => SelectCourtsAsync(day, ScheduleEditorInput.Courts(day.CourtsText)),
            SelectUnavailableCourtsAsync, day => SelectCourtsAsync(day, PreviousCourts(day)), () => CanSelectCourts,
            day => PreviousCourts(day).Count > 0, shell.ReportError);
        // Keep new drafts within reach; BuildSetup still submits dates in chronological order.
        Days.Insert(0, editor);
        return editor;
    }
    private void Load()
    {
        selectionGeneration++;
        var workspace = Session.Workspace; var resources = workspace.Schedule?.Resources ?? workspace.Resources; var policy = workspace.Schedule?.Policy;
        baseline = Source(workspace); conflict = false; edited = false;
        Failure = null; SuccessSummary = BuildSuccessSummary(workspace.Schedule);
        refereeCountText = resources?.RefereeCount?.ToString() ?? ""; minimumRestText = (resources?.MinimumRestMinutes ?? 30).ToString(); dailyMaximumText = (resources?.MaxPlayerMatchesPerDay ?? 4).ToString();
        strategyIndex = (int)(policy?.Strategy ?? ScheduleAutoSchedulingStrategy.Compact); synchronizeStageWaves = policy?.SynchronizeStageWaves ?? false;
        Days.Clear(); foreach (var day in resources?.Days.OrderBy(d => d.Date).ToArray() ?? [new ScheduleDaySettings(DateOnly.FromDateTime(DateTime.Today), new(9, 0), new(18, 0), [])]) AddDay(day, policy);
        ProjectTimings.Clear(); foreach (var project in workspace.Projects.OrderBy(p => p.SortOrder)) ProjectTimings.Add(new(project, policy, Edited));
        foreach (var property in new[] { nameof(RefereeCountText), nameof(MinimumRestText), nameof(DailyMaximumText), nameof(StrategyIndex), nameof(SynchronizeStageWaves), nameof(PolicyLabel), nameof(IsSingleProject), nameof(IsMultiProject), nameof(ScheduleSummary), nameof(StrategyDescription) }) OnPropertyChanged(property);
        RefreshAvailability();
    }
    private static string BuildSuccessSummary(TournamentSchedule? schedule, TournamentScheduleQuality? quality = null,
        SchedulingRunDiagnostics? diagnostics = null)
    {
        if (schedule is null) return "";
        var loads = quality?.DayLoads ?? schedule.Resources.Days.OrderBy(d => d.Date).Select(d => new SchedulingDayCapacity(d.DayLabel,
            ScheduleResourceCalculator.CalculateDayCapacityMinutes(schedule.Resources, d),
            schedule.Placements.Values.Where(p => p.DayLabel == d.DayLabel).Sum(p => (int)(p.EndTime - p.StartTime).TotalMinutes))).ToArray();
        var lines = new List<string> { diagnostics is null ? "已保存赛程的每日负荷：" : "本次赛程已生成并保存。每日负荷：",
            $"策略：{StrategyName(schedule.Policy.Strategy)}" };
        foreach (var day in schedule.Resources.Days.OrderBy(d => d.Date))
        {
            var load = loads.FirstOrDefault(d => d.DayLabel == day.DayLabel);
            var count = schedule.Placements.Values.Count(p => p.DayLabel == day.DayLabel);
            lines.Add(load is null ? $"{day.DayLabel}：{count} 场，本次负荷分析未完成。" :
                $"{load.DayLabel}：{count} 场，{load.RequiredPlacedMinutes} / {load.AvailableMatchMinutes} 分钟（" +
                (load.AvailableMatchMinutes > 0 ? $"{100d * load.RequiredPlacedMinutes / load.AvailableMatchMinutes:0.#}%" : "无可用容量") + "）。");
        }
        if (diagnostics is { HasBaselinePlacements: true })
            lines.Add(quality is { SoftAnalysisComplete: true }
                ? $"重新生成变动：{quality.MovedMatchCount} 场调整位置，其中跨日 {quality.CrossDayMoveCount} 场。"
                : "重新生成变动：分析未完成，本次无法提供变动场数或跨日场数。");
        if (diagnostics?.ExhaustedPhase is { } phase)
            lines.Add($"本次{(phase == SchedulingRunPhase.Quality ? "质量分析" : "改进搜索")}预算已用尽；已保存满足硬约束的完整赛程，仍可能有更好的安排。");
        if (quality is { PlayerAnalysisComplete: false }) lines.Add("部分选手负荷分析未完成，不能将负荷范围视为精确最大值。");
        if (quality is { SoftAnalysisComplete: false }) lines.Add("比赛节奏评分未完成，本次不提供完整评分。");
        return string.Join(Environment.NewLine, lines);
    }
    private static string Source(TournamentWorkspace workspace) => JsonSerializer.Serialize(new { workspace.Projects, workspace.Resources, workspace.Schedule, Results = workspace.Results.Values.OrderBy(r => r.Key.ProjectId).ThenBy(r => r.Key.MatchId).ToArray() });
    private void Edited() { selectionGeneration++; edited = true; RefreshAvailability(); }
    public override void RefreshSession(WorkspaceSession next)
    {
        if (!ReferenceEquals(Session, next)) selectionGeneration++;
        var changed = baseline != Source(next.Workspace);
        if (changed && edited) conflict = true;
        base.RefreshSession(next);
        if (changed && !edited && !conflict) Load();
        RefreshAvailability();
    }
    public override void RefreshAvailability()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(HasEditorConflict)); OnPropertyChanged(nameof(EditHint));
        OnPropertyChanged(nameof(CapacityEstimate)); OnPropertyChanged(nameof(CapacitySummary));
        GenerateCommand?.NotifyCanExecuteChanged(); AddDayCommand?.NotifyCanExecuteChanged(); ResetCommand?.NotifyCanExecuteChanged(); BoardCommand?.NotifyCanExecuteChanged(); UseStrategyDefaultsCommand?.NotifyCanExecuteChanged();
        foreach (var day in Days) day.RefreshCommands();
    }
}
