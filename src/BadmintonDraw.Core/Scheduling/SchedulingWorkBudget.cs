using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Core.Scheduling;

/// <summary>Shared by all work in one request. Failed spending never changes a phase's balance.</summary>
internal sealed class SchedulingWorkBudget
{
    private readonly CancellationToken cancellationToken;
    private readonly Dictionary<SchedulingRunPhase, long> limits;
    private readonly Dictionary<SchedulingRunPhase, long> usedWorkUnits;
    private readonly HashSet<SchedulingRunPhase> failedSpends = [];
    private readonly Dictionary<string, int> cachePeaks = new(StringComparer.Ordinal)
    { ["AppearanceChecks"] = 0, ["StateTimeChecks"] = 0, ["AppearanceProofMemo"] = 0 };
    internal IReadOnlyDictionary<string, int> CachePeaks => WorkspaceSnapshot.Dictionary(cachePeaks);
    internal void ObserveCache(string name, int count) => cachePeaks[name] = Math.Max(cachePeaks[name], count);

    internal SchedulingWorkBudget(TournamentSchedulingOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        MaxCacheEntries = options.MaxCacheEntries;
        this.cancellationToken = cancellationToken;
        limits = new()
        {
            [SchedulingRunPhase.Context] = options.ContextWorkUnits,
            [SchedulingRunPhase.Preflight] = options.PreflightWorkUnits,
            [SchedulingRunPhase.Search] = options.SearchWorkUnits,
            [SchedulingRunPhase.Validation] = options.ValidationWorkUnits,
            [SchedulingRunPhase.Optimization] = options.OptimizationWorkUnits,
            [SchedulingRunPhase.Quality] = options.QualityWorkUnits
        };
        usedWorkUnits = limits.Keys.ToDictionary(phase => phase, _ => 0L);
    }

    internal bool IsCanceled => cancellationToken.IsCancellationRequested;
    internal int MaxCacheEntries { get; }
    internal bool HasFailedSpend(SchedulingRunPhase phase) => failedSpends.Contains(phase);

    internal IReadOnlyDictionary<SchedulingRunPhase, long> UsedWorkUnits => WorkspaceSnapshot.Dictionary(usedWorkUnits);

    internal bool TrySpend(SchedulingRunPhase phase, long units = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(units);
        var remaining = Remaining(phase);
        if (IsCanceled) return false;
        if (units > remaining)
        {
            failedSpends.Add(phase);
            return false;
        }

        // The subtraction comparison proves this addition stays within the nonnegative long limit.
        usedWorkUnits[phase] += units;
        return true;
    }

    internal long Remaining(SchedulingRunPhase phase)
    {
        if (!limits.TryGetValue(phase, out var limit))
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown scheduling phase.");

        return limit - usedWorkUnits[phase];
    }
}
