using BadmintonDraw.Core.Matches;

namespace BadmintonDraw.Core.Scheduling;

public static class ScheduleTimingResolver
{
    public static int Resolve(MatchNode node, TournamentSchedulingPolicy policy)
    {
        if (!policy.ProjectTimings.TryGetValue(node.ProjectId, out var timing)) return node.ExpectedDurationMinutes;
        var before = !node.IsPlacementPlayoff && timing.KnockoutTimingBoundaryEntrants.HasValue &&
            (node.ForceBeforeTimingBoundary || node.KnockoutEntrantCount > timing.KnockoutTimingBoundaryEntrants);
        return before ? timing.BeforeBoundaryMinutes!.Value : timing.MatchMinutes;
    }
}
