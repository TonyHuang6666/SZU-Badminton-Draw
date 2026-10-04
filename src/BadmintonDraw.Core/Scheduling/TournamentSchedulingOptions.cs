namespace BadmintonDraw.Core.Scheduling;

/// <summary>Finite per-request work and search limits. Zero disables the corresponding allowance.</summary>
public sealed record TournamentSchedulingOptions
{
    private long contextWorkUnits = 8_000_000;
    private long preflightWorkUnits = 1_000_000;
    // Calibrated on the retained 276/292-match acceptance cases after score/path
    // reuse. These conservative work units are not iterations or milliseconds.
    private long searchWorkUnits = 5_000_000_000;
    private long validationWorkUnits = 30_000_000;
    private long optimizationWorkUnits = 5_000_000;
    private long qualityWorkUnits = 2_000_000;
    private int maxDecisionAlternatives = 64;
    private int maxRollbackDepth = 64;
    private int maxBacktracks = 4_096;
    private int maxCacheEntries = 32_768;

    public static TournamentSchedulingOptions Default { get; } = new();

    public long ContextWorkUnits { get => contextWorkUnits; init => contextWorkUnits = NonNegative(value, nameof(ContextWorkUnits)); }
    public long PreflightWorkUnits { get => preflightWorkUnits; init => preflightWorkUnits = NonNegative(value, nameof(PreflightWorkUnits)); }
    public long SearchWorkUnits { get => searchWorkUnits; init => searchWorkUnits = NonNegative(value, nameof(SearchWorkUnits)); }
    public long ValidationWorkUnits { get => validationWorkUnits; init => validationWorkUnits = NonNegative(value, nameof(ValidationWorkUnits)); }
    public long OptimizationWorkUnits { get => optimizationWorkUnits; init => optimizationWorkUnits = NonNegative(value, nameof(OptimizationWorkUnits)); }
    public long QualityWorkUnits { get => qualityWorkUnits; init => qualityWorkUnits = NonNegative(value, nameof(QualityWorkUnits)); }
    public int MaxDecisionAlternatives { get => maxDecisionAlternatives; init => maxDecisionAlternatives = NonNegative(value, nameof(MaxDecisionAlternatives)); }
    public int MaxRollbackDepth { get => maxRollbackDepth; init => maxRollbackDepth = NonNegative(value, nameof(MaxRollbackDepth)); }
    public int MaxBacktracks { get => maxBacktracks; init => maxBacktracks = NonNegative(value, nameof(MaxBacktracks)); }
    public int MaxCacheEntries { get => maxCacheEntries; init => maxCacheEntries = NonNegative(value, nameof(MaxCacheEntries)); }

    private static long NonNegative(long value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
        return value;
    }

    private static int NonNegative(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
        return value;
    }
}
