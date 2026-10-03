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
    public ObservableCollection<ScheduleDayEditorViewModel> Days { get; } = [];
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
    public SchedulingFailure? Failure { get => failure; private set { if (SetProperty(ref failure, value)) { OnPropertyChanged(nameof(FailureDetails)); OnPropertyChanged(nameof(FailureTechnicalDetails)); OnPropertyChanged(nameof(HasFailure)); } } }
    public bool HasFailure => Failure is not null;
    public string FailureDetails => Failure is null ? "" : string.Join(Environment.NewLine,
        Failure.UnplacedMatches.Select(m => $"未安排：{m.ProjectName} · {m.MatchName}")
        .Concat(Failure.Violations.Select(v => v.Message))
        .Concat(Failure.Suggestions.Select(s => "建议：" + s)));
    public string FailureTechnicalDetails => Failure is null ? "" : string.Join(Environment.NewLine, Failure.Violations.Select(v => $"{v.Code}：{v.Message}"));
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
            var request = BuildSetup(); var expected = Session; Failure = null;
            if (await shell.RunWorkspaceCommandAsync(expected, (workflow, revision) => workflow.GenerateSchedule(request.Resources, request.Policy, revision), "赛程已生成并自动保存，可以查看赛程板。")) Load();
            else Failure = shell.LastError?.SchedulingFailure;
        }, () => CanSelectCourts, shell.ReportError);
        AddDayCommand = new(() => { var date = Days.Count > 0 && DateOnly.TryParse(Days[^1].DateText, out var last) ? last.AddDays(1) : DateOnly.FromDateTime(DateTime.Today); AddDay(new(date, new(9, 0), new(18, 0), []), null); Edited(); }, () => CanSelectCourts);
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
    private void AddDay(ScheduleDaySettings day, TournamentSchedulingPolicy? policy)
    {
        var target = policy?.DayLoadTargets.FirstOrDefault(t => t.DayLabel == day.DayLabel);
        Days.Add(new(day, target?.TargetUtilization, target?.WarningUtilization, policy?.StageWaveTargets.FirstOrDefault(t => t.DayLabel == day.DayLabel)?.CumulativeProgress,
            Edited, item => { Days.Remove(item); Edited(); }, day => SelectCourtsAsync(day, ScheduleEditorInput.Courts(day.CourtsText)),
            SelectUnavailableCourtsAsync, day => SelectCourtsAsync(day, PreviousCourts(day)), () => CanSelectCourts,
            day => PreviousCourts(day).Count > 0, shell.ReportError));
    }
    private void Load()
    {
        selectionGeneration++;
        var workspace = Session.Workspace; var resources = workspace.Schedule?.Resources ?? workspace.Resources; var policy = workspace.Schedule?.Policy;
        baseline = Source(workspace); conflict = false; edited = false;
        refereeCountText = resources?.RefereeCount?.ToString() ?? ""; minimumRestText = (resources?.MinimumRestMinutes ?? 30).ToString(); dailyMaximumText = (resources?.MaxPlayerMatchesPerDay ?? 4).ToString();
        strategyIndex = (int)(policy?.Strategy ?? ScheduleAutoSchedulingStrategy.Compact); synchronizeStageWaves = policy?.SynchronizeStageWaves ?? false;
        Days.Clear(); foreach (var day in resources?.Days.OrderBy(d => d.Date).ToArray() ?? [new ScheduleDaySettings(DateOnly.FromDateTime(DateTime.Today), new(9, 0), new(18, 0), [])]) AddDay(day, policy);
        ProjectTimings.Clear(); foreach (var project in workspace.Projects.OrderBy(p => p.SortOrder)) ProjectTimings.Add(new(project, policy, Edited));
        foreach (var property in new[] { nameof(RefereeCountText), nameof(MinimumRestText), nameof(DailyMaximumText), nameof(StrategyIndex), nameof(SynchronizeStageWaves), nameof(PolicyLabel), nameof(IsSingleProject), nameof(IsMultiProject), nameof(ScheduleSummary), nameof(StrategyDescription) }) OnPropertyChanged(property);
        RefreshAvailability();
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
