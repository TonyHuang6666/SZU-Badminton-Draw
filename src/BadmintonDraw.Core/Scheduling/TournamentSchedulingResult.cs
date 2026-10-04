namespace BadmintonDraw.Core.Scheduling;

public abstract record TournamentSchedulingResult
{
    public sealed record Success(TournamentSchedule Schedule, IReadOnlyDictionary<Guid, string> GraphRevisions,
        TournamentScheduleQuality Quality) : TournamentSchedulingResult
    {
        public SchedulingRunDiagnostics? Diagnostics { get; init; }
    }
    public sealed record Failure(SchedulingFailure Detail) : TournamentSchedulingResult;
}

public sealed record TournamentScheduleQuality(int HardConstraintCount, long SoftScore,
    IReadOnlyList<SchedulingViolation> Violations, IReadOnlyList<SchedulingDayCapacity> DayLoads,
    IReadOnlyList<TournamentPlayerDailyLoadForecast> PlayerLoads, int MovedMatchCount, int CrossDayMoveCount)
{
    /// <summary>False means the violation count is incomplete and cannot establish absence of conflicts.</summary>
    public bool HardValidationComplete { get; init; } = true;
    /// <summary>True only when every returned player maximum and probability distribution is complete.</summary>
    public bool PlayerAnalysisComplete { get; init; } = true;
    /// <summary>False means SoftScore and movement counts are unavailable placeholders.</summary>
    public bool SoftAnalysisComplete { get; init; } = true;
}

public sealed record TournamentPlayerDailyLoadForecast(string PlayerKey, string PlayerName, string DayLabel,
    int ConfirmedCount, int? MaximumCount, double? ExpectedCount, double? ProbabilityAtOrAboveLimit,
    IReadOnlyDictionary<int, double> Distribution, bool IsExact, IReadOnlyList<Guid> MatchIds)
{
    /// <summary>A proved compatible appearance count; this remains available when MaximumCount is null.</summary>
    public int MaximumLowerBound { get; init; } = MaximumCount ?? ConfirmedCount;
    public int MaximumUpperBound { get; init; } = MaximumCount ?? MatchIds.Count;
}
