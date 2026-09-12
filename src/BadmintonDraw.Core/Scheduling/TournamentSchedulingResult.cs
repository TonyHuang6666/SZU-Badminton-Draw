namespace BadmintonDraw.Core.Scheduling;

public abstract record TournamentSchedulingResult
{
    public sealed record Success(TournamentSchedule Schedule, IReadOnlyDictionary<Guid, string> GraphRevisions,
        TournamentScheduleQuality Quality) : TournamentSchedulingResult;
    public sealed record Failure(SchedulingFailure Detail) : TournamentSchedulingResult;
}

public sealed record TournamentScheduleQuality(int HardConstraintCount, long SoftScore,
    IReadOnlyList<SchedulingViolation> Violations, IReadOnlyList<SchedulingDayCapacity> DayLoads,
    IReadOnlyList<TournamentPlayerDailyLoadForecast> PlayerLoads, int MovedMatchCount, int CrossDayMoveCount);

public sealed record TournamentPlayerDailyLoadForecast(string PlayerKey, string PlayerName, string DayLabel,
    int ConfirmedCount, int MaximumCount, double? ExpectedCount, double? ProbabilityAtOrAboveLimit,
    IReadOnlyDictionary<int, double> Distribution, bool IsExact, IReadOnlyList<Guid> MatchIds);
