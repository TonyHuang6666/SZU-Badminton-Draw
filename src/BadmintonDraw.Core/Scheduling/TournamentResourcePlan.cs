using BadmintonDraw.Core.Tournaments;
namespace BadmintonDraw.Core.Scheduling;
public sealed record TournamentResourcePlan(IReadOnlyList<ScheduleDaySettings> Days,
    int? RefereeCount, int MinimumRestMinutes, int MaxPlayerMatchesPerDay)
{
    private IReadOnlyList<ScheduleDaySettings> days = WorkspaceSnapshot.List(Days.Select(WorkspaceSnapshot.Day));
    public IReadOnlyList<ScheduleDaySettings> Days { get => days; init => days = WorkspaceSnapshot.List(value.Select(WorkspaceSnapshot.Day)); }
}
