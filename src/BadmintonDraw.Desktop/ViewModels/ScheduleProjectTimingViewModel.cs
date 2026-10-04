using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class ScheduleProjectTimingViewModel : ScheduleEditorViewModel
{
    private string minutesText, boundaryText, afterMinutesText;
    private bool useTimingSplit;
    public Guid ProjectId { get; }
    public string Name { get; }
    public bool SupportsTimingSplit { get; }
    public string MinutesText { get => minutesText; set => Edit(ref minutesText, value, nameof(MinutesText)); }
    public string BoundaryText { get => boundaryText; set => Edit(ref boundaryText, value, nameof(BoundaryText)); }
    public string AfterMinutesText { get => afterMinutesText; set => Edit(ref afterMinutesText, value, nameof(AfterMinutesText)); }
    public bool UseTimingSplit { get => useTimingSplit; set => Edit(ref useTimingSplit, value, nameof(UseTimingSplit)); }
    public ScheduleProjectTimingViewModel(TournamentProject project, TournamentSchedulingPolicy? policy, Action changed) : base(changed)
    {
        ProjectId = project.Id; Name = project.DisplayName; SupportsTimingSplit = project.CompetitionMode is CompetitionMode.SinglesKnockout or CompetitionMode.TeamKnockout;
        var timing = policy?.ProjectTimings.GetValueOrDefault(project.Id);
        useTimingSplit = SupportsTimingSplit && timing?.KnockoutTimingBoundaryEntrants is not null;
        // The persisted model stores the later duration in MatchMinutes. Present the
        // early duration as the main input without changing existing match lengths.
        minutesText = (useTimingSplit ? timing!.BeforeBoundaryMinutes!.Value :
            timing?.MatchMinutes ?? project.MatchGraph?.Matches.FirstOrDefault()?.ExpectedDurationMinutes ?? 30).ToString();
        boundaryText = timing?.KnockoutTimingBoundaryEntrants?.ToString() ?? "8";
        afterMinutesText = timing?.MatchMinutes.ToString() ?? minutesText;
    }
    public ProjectMatchTiming BuildTiming()
    {
        var basicMinutes = ScheduleEditorInput.Integer(MinutesText, Name + "基础单场时长");
        return UseTimingSplit && SupportsTimingSplit
            ? new(ScheduleEditorInput.Integer(AfterMinutesText, "进入前 N 名起单场时长"),
                ScheduleEditorInput.Integer(BoundaryText, "分段人数", 2), basicMinutes)
            : new(basicMinutes);
    }
}
