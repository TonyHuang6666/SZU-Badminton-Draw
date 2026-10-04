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
    private string baseline = "", editorBaseline = "", refereeCountText = "", minimumRestText = "30", dailyMaximumText = "4";
    private bool edited, conflict, requireChampionshipFinalsOnLastDay;
    private SchedulingFailure? failure;
    private string successSummary = "";
    public ObservableCollection<ScheduleDayEditorViewModel> Days { get; } = [];
    public event Action<ScheduleDayEditorViewModel>? DayAdded;
    public ObservableCollection<ScheduleProjectTimingViewModel> ProjectTimings { get; } = [];
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
    public string FinalDayHint => Days.Select(d => d.SelectedDate).Max() is { } last
        ? $"最后一个比赛日：{last:yyyy-MM-dd}。仅限定冠亚军决赛；半决赛、季军赛和排位赛仍尽早安排。没有冠亚军决赛的项目不受此项影响。"
        : "请先选择比赛日期；最后一天按日期确定，与添加顺序无关。";
    public string RefereeCountText { get => refereeCountText; set { if (SetProperty(ref refereeCountText, value)) Edited(); } }
    public string MinimumRestText { get => minimumRestText; set { if (SetProperty(ref minimumRestText, value)) Edited(); } }
    public string DailyMaximumText { get => dailyMaximumText; set { if (SetProperty(ref dailyMaximumText, value)) Edited(); } }
    public bool RequireChampionshipFinalsOnLastDay { get => requireChampionshipFinalsOnLastDay; set { if (SetProperty(ref requireChampionshipFinalsOnLastDay, value)) Edited(); } }
    public bool HasEditorConflict => conflict;
    public bool CanEdit => !disposed && shell.CanMutate && !conflict && Session.Workspace.Purpose == TournamentPurpose.FullTournament && Session.Workspace.Results.Count == 0 &&
        Session.Workspace.Stage is TournamentStage.DrawsConfirmed or TournamentStage.ScheduleReady;
    public string EditHint => conflict ? "赛程、抽签或资源已被其他操作更改。输入仍保留；请点击“放弃未保存的修改”，确认恢复已保存的设置后再编排。" : Session.Workspace.Results.Count > 0
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
        _ when Failure?.Diagnostics is { Policy.RequireChampionshipFinalsOnLastDay: true } details && details.Resources.Days.Count > 0 =>
            $"本次要求冠亚军决赛必须在 {details.Resources.Days.Max(d => d.Date):yyyy-MM-dd} 进行。请检查最后一天的时段、场地和裁判，以及前序比赛与休息时间。程序不会自动提前决赛或取消勾选；如不需要固定日期，可自行取消勾选后重新生成。",
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
    public AsyncCommand ResetCommand { get; }
    public DelegateCommand BoardCommand { get; }
    public ScheduleSetupPageViewModel(AppShellViewModel shell, WorkspaceSession session,
        Func<VenueCourtSelectionViewModel, Task<bool>>? chooseCourts = null,
        Func<UnavailableCourtSelectionViewModel, Task<bool>>? chooseUnavailable = null,
        Func<Task<bool>>? confirmDiscard = null) : base(session)
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
        ResetCommand = new(async () =>
        {
            var expected = Session; var generation = selectionGeneration;
            bool IsCurrent() => !disposed && !shell.IsBusy && ReferenceEquals(Session, expected) && selectionGeneration == generation;
            try
            {
                if (await (confirmDiscard?.Invoke() ?? Task.FromResult(false)) && IsCurrent()) Load();
            }
            catch (Exception) when (!IsCurrent())
            {
                // A stale/closed confirmation must not discard newer input or replace its feedback.
            }
        }, () => !disposed && !shell.IsBusy && (edited || conflict), shell.ReportError);
        BoardCommand = new(() => shell.Navigate(WorkspaceRoute.ScheduleBoard), () => shell.CanNavigate(WorkspaceRoute.ScheduleBoard));
        Load();
    }
    public ScheduleSetupRequest BuildSetup()
    {
        if (conflict) throw ScheduleEditorInput.Error("请先点击“放弃未保存的修改”并确认恢复已保存的设置，不能提交旧设置。");
        if (Days.Count == 0) throw ScheduleEditorInput.Error("请至少添加一个比赛日。");
        var days = Days.Select(d => d.Build()).OrderBy(d => d.Date).ToArray();
        var resources = new TournamentResourcePlan(days, string.IsNullOrWhiteSpace(RefereeCountText) ? null : ScheduleEditorInput.Integer(RefereeCountText, "裁判人数"),
            ScheduleEditorInput.Integer(MinimumRestText, "最小休息", 0), ScheduleEditorInput.Integer(DailyMaximumText, "每日场次上限"));
        // Rebuild the policy explicitly: retired soft targets from older schedules must not
        // silently affect the simple editor. The saved schedule changes only after success.
        var policy = new TournamentSchedulingPolicy(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])
        {
            RequireChampionshipFinalsOnLastDay = RequireChampionshipFinalsOnLastDay,
            ProjectTimings = ProjectTimings.ToDictionary(p => p.ProjectId, p => p.BuildTiming())
        };
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
        var day = AddDay(new(date, new(9, 0), new(18, 0), []));
        Edited();
        DayAdded?.Invoke(day);
    }
    private ScheduleDayEditorViewModel AddDay(ScheduleDaySettings day)
    {
        var editor = new ScheduleDayEditorViewModel(day,
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
        requireChampionshipFinalsOnLastDay = policy?.RequireChampionshipFinalsOnLastDay ?? false;
        Days.Clear(); foreach (var day in resources?.Days.OrderBy(d => d.Date).ToArray() ?? [new ScheduleDaySettings(DateOnly.FromDateTime(DateTime.Today), new(9, 0), new(18, 0), [])]) AddDay(day);
        ProjectTimings.Clear(); foreach (var project in workspace.Projects.OrderBy(p => p.SortOrder)) ProjectTimings.Add(new(project, policy, Edited));
        editorBaseline = EditorSnapshot();
        foreach (var property in new[] { nameof(RefereeCountText), nameof(MinimumRestText), nameof(DailyMaximumText), nameof(RequireChampionshipFinalsOnLastDay), nameof(IsSingleProject), nameof(IsMultiProject), nameof(ScheduleSummary) }) OnPropertyChanged(property);
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
        if (schedule.Policy.RequireChampionshipFinalsOnLastDay)
            lines.Add($"冠亚军决赛固定在最后比赛日：{schedule.Resources.Days.Max(d => d.Date):yyyy-MM-dd}。");
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
    // Compare raw fields, not BuildSetup(): even unfinished or invalid input must be discardable.
    private string EditorSnapshot() => JsonSerializer.Serialize(new
    {
        RefereeCountText, MinimumRestText, DailyMaximumText, RequireChampionshipFinalsOnLastDay,
        Days = Days.Select(d => new
        {
            d.DateText, d.StartText, d.EndText, d.CourtsText,
            Unavailable = d.Unavailable.Select(w => new { w.StartText, w.EndText, w.CourtsText }),
            Referees = d.RefereeWindows.Select(w => new { w.StartText, w.EndText, w.CountText })
        }),
        Timings = ProjectTimings.Select(p => new { p.ProjectId, p.MinutesText, p.UseTimingSplit, p.BoundaryText, p.BeforeMinutesText })
    });
    private void Edited() { selectionGeneration++; edited = EditorSnapshot() != editorBaseline; RefreshAvailability(); }
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
        OnPropertyChanged(nameof(CapacityEstimate)); OnPropertyChanged(nameof(CapacitySummary)); OnPropertyChanged(nameof(FinalDayHint));
        GenerateCommand?.NotifyCanExecuteChanged(); AddDayCommand?.NotifyCanExecuteChanged(); ResetCommand?.NotifyCanExecuteChanged(); BoardCommand?.NotifyCanExecuteChanged();
        foreach (var day in Days) day.RefreshCommands();
    }
}
