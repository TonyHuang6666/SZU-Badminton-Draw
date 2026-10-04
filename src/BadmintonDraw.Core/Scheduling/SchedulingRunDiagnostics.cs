using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Core.Scheduling;

public enum SchedulingRunPhase
{
    Context, Preflight, Search, Validation, Optimization, Quality
}

public enum SchedulingPreflightStatus
{
    NotRun, NoContradictionFound, ProvenInfeasible, Unknown
}

public enum SchedulingFailureKind
{
    InvalidInput, ProvenInfeasible, SearchIncomplete, ValidationIncomplete, Canceled
}

/// <summary>An immutable snapshot of one scheduling run; work units are not elapsed-time guarantees.</summary>
public sealed record SchedulingRunDiagnostics(SchedulingRunPhase Phase,
    SchedulingPreflightStatus PreflightStatus, SchedulingFailureKind? FailureKind,
    IReadOnlyDictionary<SchedulingRunPhase, long> UsedWorkUnits,
    TournamentResourcePlan Resources, TournamentSchedulingPolicy Policy)
{
    private IReadOnlyDictionary<SchedulingRunPhase, long> usedWorkUnits = WorkspaceSnapshot.Dictionary(UsedWorkUnits);

    public IReadOnlyDictionary<SchedulingRunPhase, long> UsedWorkUnits
    {
        get => usedWorkUnits;
        init => usedWorkUnits = WorkspaceSnapshot.Dictionary(value);
    }

    public SchedulingRunPhase? ExhaustedPhase { get; init; }
    private IReadOnlyList<SchedulingRunPhase> rejectedBudgetPhases = WorkspaceSnapshot.List(Array.Empty<SchedulingRunPhase>());
    /// <summary>All refused work spends, including an Unknown preflight followed by search.</summary>
    public IReadOnlyList<SchedulingRunPhase> RejectedBudgetPhases
    {
        get => rejectedBudgetPhases;
        init => rejectedBudgetPhases = WorkspaceSnapshot.List(value);
    }
    public bool HasBaselinePlacements { get; init; }
    private IReadOnlyDictionary<string, int> cachePeaks = WorkspaceSnapshot.Dictionary(new Dictionary<string, int>());
    /// <summary>Actual per-cache entry high-water marks, not a simultaneous total or memory bytes.
    /// AppearanceProofMemo is work-budget bounded; the other two obey MaxCacheEntries.</summary>
    public IReadOnlyDictionary<string, int> CachePeaks { get => cachePeaks; init => cachePeaks = WorkspaceSnapshot.Dictionary(value); }
}
