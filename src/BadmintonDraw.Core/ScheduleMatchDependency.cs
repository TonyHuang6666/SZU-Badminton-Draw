namespace BadmintonDraw.Core;

public sealed record ScheduleMatchDependency(
    string SourceMatchId,
    string SourceMatchName,
    ScheduleMatchDependencyOutcome Outcome,
    ScheduleMatchSide TargetSide);

public enum ScheduleMatchDependencyOutcome
{
    Winner = 0,
    Loser = 1
}

public enum ScheduleMatchSide
{
    SideA = 0,
    SideB = 1
}
