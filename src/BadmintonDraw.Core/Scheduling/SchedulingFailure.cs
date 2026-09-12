using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Core.Scheduling;

public enum SchedulingConstraintCode
{
    InvalidGraph, MissingDependency, CyclicDependency, CrossProjectDependency, InvalidResources, InvalidPolicy,
    InvalidResult, UnknownMatch, MissingPlacement, PlacementIdentity, Duration, DayBounds, CourtUnavailable,
    CourtOverlap, RefereeCapacity, DependencyOrder, PlayerOverlap, MinimumRest, DailyMatchLimit,
    LockedPlacement, ChampionshipOrder, SearchExhausted
}

public sealed record SchedulingViolation(SchedulingConstraintCode Code, Guid? ProjectId, Guid? MatchId,
    Guid? RelatedMatchId, string Message);

public sealed record TournamentPlacementValidation(IReadOnlyList<SchedulingViolation> Violations)
{
    public IReadOnlyList<SchedulingViolation> Violations { get; init; } = WorkspaceSnapshot.List(Violations);
    public bool IsValid => Violations.Count == 0;
}

public sealed record SchedulingBlockedMatch(Guid ProjectId, string ProjectName, Guid MatchId,
    string MatchName, IReadOnlyList<SchedulingConstraintCode> ConstraintCodes);
public sealed record SchedulingDayCapacity(string DayLabel, int AvailableMatchMinutes, int RequiredPlacedMinutes);
public sealed record SchedulingFailure(string Message, IReadOnlyList<SchedulingBlockedMatch> UnplacedMatches,
    IReadOnlyList<SchedulingViolation> Violations, IReadOnlyList<SchedulingDayCapacity> Capacity,
    IReadOnlyList<string> Suggestions);
