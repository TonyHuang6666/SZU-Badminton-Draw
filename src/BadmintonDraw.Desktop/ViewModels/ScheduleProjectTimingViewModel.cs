using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class ScheduleProjectTimingViewModel : ScheduleEditorViewModel
{
    private string minutesText, boundaryText, beforeMinutesText;
    private bool useTimingSplit;
    public Guid ProjectId { get; }
    public string Name { get; }
    public bool SupportsTimingSplit { get; }
    public string MinutesText { get => minutesText; set => Edit(ref minutesText, value, nameof(MinutesText)); }
    public string BoundaryText { get => boundaryText; set => Edit(ref boundaryText, value, nameof(BoundaryText)); }
    public string BeforeMinutesText { get => beforeMinutesText; set => Edit(ref beforeMinutesText, value, nameof(BeforeMinutesText)); }
    public bool UseTimingSplit { get => useTimingSplit; set => Edit(ref useTimingSplit, value, nameof(UseTimingSplit)); }
    public ScheduleProjectTimingViewModel(TournamentProject project, TournamentSchedulingPolicy? policy, Action changed) : base(changed)
    {
        ProjectId = project.Id; Name = project.DisplayName; SupportsTimingSplit = project.CompetitionMode is CompetitionMode.SinglesKnockout or CompetitionMode.TeamKnockout;
        var timing = policy?.ProjectTimings.GetValueOrDefault(project.Id);
        minutesText = (timing?.MatchMinutes ?? project.MatchGraph?.Matches.FirstOrDefault()?.ExpectedDurationMinutes ?? 30).ToString();
        useTimingSplit = timing?.KnockoutTimingBoundaryEntrants is not null;
        boundaryText = timing?.KnockoutTimingBoundaryEntrants?.ToString() ?? "8"; beforeMinutesText = timing?.BeforeBoundaryMinutes?.ToString() ?? minutesText;
    }
    public ProjectMatchTiming BuildTiming() => new(ScheduleEditorInput.Integer(MinutesText, Name + "单场时长"),
        UseTimingSplit && SupportsTimingSplit ? ScheduleEditorInput.Integer(BoundaryText, "分段人数", 2) : null,
        UseTimingSplit && SupportsTimingSplit ? ScheduleEditorInput.Integer(BeforeMinutesText, "分段前时长") : null);
}
