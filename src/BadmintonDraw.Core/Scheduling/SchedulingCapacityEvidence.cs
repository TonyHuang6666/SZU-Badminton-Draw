using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Core.Scheduling;

/// <summary>Compatible appearances prove a lower bound; time quantities use ticks.</summary>
public sealed record SchedulingCapacityEvidence(string Kind, string? PlayerKey, string? PlayerName,
    long RequiredLowerBound, long CapacityUpperBound, IReadOnlyList<Guid> WitnessMatchIds,
    IReadOnlyDictionary<Guid, bool> OutcomeConditions)
{
    private IReadOnlyList<Guid> witnessMatchIds = WorkspaceSnapshot.List(WitnessMatchIds);
    private IReadOnlyDictionary<Guid, bool> outcomeConditions = WorkspaceSnapshot.Dictionary(OutcomeConditions);
    public IReadOnlyList<Guid> WitnessMatchIds { get => witnessMatchIds; init => witnessMatchIds = WorkspaceSnapshot.List(value); }
    public IReadOnlyDictionary<Guid, bool> OutcomeConditions { get => outcomeConditions; init => outcomeConditions = WorkspaceSnapshot.Dictionary(value); }
}

internal sealed record SchedulingPreflightResult(SchedulingPreflightStatus Status,
    SchedulingCapacityEvidence? Evidence, IReadOnlyList<SchedulingViolation> Violations);

internal static class SchedulingCapacityArithmetic
{
    internal static long Add(long a, long b) => b > long.MaxValue - a ? long.MaxValue : checked(a + b);
    internal static long Multiply(long a, long b) => a != 0 && b > long.MaxValue / a ? long.MaxValue : checked(a * b);
    internal static long DayIntegrationWork(ScheduleDaySettings day)
    {
        var windows = (long)(day.RefereeCapacityWindows?.Count ?? 0) + (day.UnavailableCourtWindows?.Count ?? 0) + 1;
        var boundaries = Multiply(2, windows);
        var comparisons = Multiply(day.Courts.Count + 1L, windows);
        foreach (var block in day.UnavailableCourtWindows ?? []) comparisons = Add(comparisons, Multiply(day.Courts.Count, block.Courts.Count + 1L));
        return Add(Multiply(boundaries, boundaries), Multiply(boundaries, comparisons));
    }
}
