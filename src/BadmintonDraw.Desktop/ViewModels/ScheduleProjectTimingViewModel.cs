using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class ScheduleProjectTimingViewModel : ScheduleEditorViewModel
{
    private string minutesText, boundaryText, beforeMinutesText;
    private bool useTimingSplit;
    private int finalPreferenceIndex, semifinalPreferenceIndex, bronzePreferenceIndex, placementPreferenceIndex;
    public Guid ProjectId { get; }
    public string Name { get; }
    public bool SupportsTimingSplit { get; }
    public string MinutesText { get => minutesText; set => Edit(ref minutesText, value, nameof(MinutesText)); }
    public string BoundaryText { get => boundaryText; set => Edit(ref boundaryText, value, nameof(BoundaryText)); }
    public string BeforeMinutesText { get => beforeMinutesText; set => Edit(ref beforeMinutesText, value, nameof(BeforeMinutesText)); }
    public bool UseTimingSplit { get => useTimingSplit; set => Edit(ref useTimingSplit, value, nameof(UseTimingSplit)); }
    public IReadOnlyList<string> Preferences { get; } = ["使用默认安排", "任意比赛日", "尽量避开最后一天", "尽量安排在最后一天", "优先留在最后一天（尽量满足）"];
    public int FinalPreferenceIndex { get => finalPreferenceIndex; set => Edit(ref finalPreferenceIndex, value, nameof(FinalPreferenceIndex)); }
    public int SemifinalPreferenceIndex { get => semifinalPreferenceIndex; set => Edit(ref semifinalPreferenceIndex, value, nameof(SemifinalPreferenceIndex)); }
    public int BronzePreferenceIndex { get => bronzePreferenceIndex; set => Edit(ref bronzePreferenceIndex, value, nameof(BronzePreferenceIndex)); }
    public int PlacementPreferenceIndex { get => placementPreferenceIndex; set => Edit(ref placementPreferenceIndex, value, nameof(PlacementPreferenceIndex)); }
    public ScheduleProjectTimingViewModel(TournamentProject project, TournamentSchedulingPolicy? policy, Action changed) : base(changed)
    {
        ProjectId = project.Id; Name = project.DisplayName; SupportsTimingSplit = project.CompetitionMode is CompetitionMode.SinglesKnockout or CompetitionMode.TeamKnockout;
        var timing = policy?.ProjectTimings.GetValueOrDefault(project.Id);
        minutesText = (timing?.MatchMinutes ?? project.MatchGraph?.Matches.FirstOrDefault()?.ExpectedDurationMinutes ?? 30).ToString();
        useTimingSplit = timing?.KnockoutTimingBoundaryEntrants is not null;
        boundaryText = timing?.KnockoutTimingBoundaryEntrants?.ToString() ?? "8"; beforeMinutesText = timing?.BeforeBoundaryMinutes?.ToString() ?? minutesText;
        int Preference(TournamentFinalDayMatchCategory category) => policy?.FinalDayRules.FirstOrDefault(r => r.ProjectId == ProjectId && r.Category == category) is { } rule ? (int)rule.Preference + 1 : 0;
        finalPreferenceIndex = Preference(TournamentFinalDayMatchCategory.Final); semifinalPreferenceIndex = Preference(TournamentFinalDayMatchCategory.Semifinal);
        bronzePreferenceIndex = Preference(TournamentFinalDayMatchCategory.Bronze); placementPreferenceIndex = Preference(TournamentFinalDayMatchCategory.Placement5To8);
    }
    public ProjectMatchTiming BuildTiming() => new(ScheduleEditorInput.Integer(MinutesText, Name + "单场时长"),
        UseTimingSplit && SupportsTimingSplit ? ScheduleEditorInput.Integer(BoundaryText, "分段人数", 2) : null,
        UseTimingSplit && SupportsTimingSplit ? ScheduleEditorInput.Integer(BeforeMinutesText, "分段前时长") : null);
    public IEnumerable<TournamentFinalDayRule> BuildFinalRules()
    {
        foreach (var (category, index) in new[] { (TournamentFinalDayMatchCategory.Final, FinalPreferenceIndex), (TournamentFinalDayMatchCategory.Semifinal, SemifinalPreferenceIndex),
            (TournamentFinalDayMatchCategory.Bronze, BronzePreferenceIndex), (TournamentFinalDayMatchCategory.Placement5To8, PlacementPreferenceIndex) })
        {
            if (index is < 0 or > 4) throw ScheduleEditorInput.Error("请选择有效的决赛日目标。");
            if (index > 0) yield return new(ProjectId, category, (TournamentFinalDayPreference)(index - 1));
        }
    }
}
