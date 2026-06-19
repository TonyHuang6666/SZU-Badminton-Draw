namespace BadmintonDraw.Core;

public enum CrossEventSchedulingStrategy
{
    Compact = 1,
    BalancedRelaxed = 2,
    FinalsDayFriendly = 3,
    Custom = 4
}

public enum CrossEventFinalDayMatchCategory
{
    Final = 1,
    Semifinal = 2,
    Bronze = 3,
    Placement5To8 = 4
}

public enum CrossEventFinalDayPolicy
{
    Flexible = 0,
    AvoidFinalDay = 1,
    PreferFinalDay = 2,
    MustFinalDay = 3
}

public sealed record CrossEventDayLoadTarget(
    string DayLabel,
    double TargetUtilization,
    double WarningUtilization)
{
    public double TargetUtilization { get; init; } = ClampRatio(TargetUtilization);

    public double WarningUtilization { get; init; } = ClampRatio(Math.Max(WarningUtilization, TargetUtilization));

    private static double ClampRatio(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0.6;
        }

        return Math.Clamp(value, 0.05, 1.0);
    }
}

public sealed record CrossEventStageWaveTarget(
    string DayLabel,
    double CumulativeProgress)
{
    public double CumulativeProgress { get; init; } = ClampProgress(CumulativeProgress);

    private static double ClampProgress(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 1.0;
        }

        return Math.Clamp(value, 0.05, 1.0);
    }
}

public sealed record CrossEventFinalDayRule(
    string EventName,
    CrossEventFinalDayMatchCategory Category,
    CrossEventFinalDayPolicy Policy);

public sealed record CrossEventSchedulingOptions(
    CrossEventSchedulingStrategy Strategy,
    IReadOnlyList<CrossEventDayLoadTarget> DayLoadTargets,
    bool SynchronizeStageWaves,
    IReadOnlyList<CrossEventStageWaveTarget> StageWaveTargets,
    IReadOnlyList<CrossEventFinalDayRule> FinalDayRules,
    int? RefereeCount = null)
{
    public static CrossEventSchedulingOptions Empty(CrossEventSchedulingStrategy strategy)
    {
        return new CrossEventSchedulingOptions(
            strategy,
            Array.Empty<CrossEventDayLoadTarget>(),
            SynchronizeStageWaves: false,
            Array.Empty<CrossEventStageWaveTarget>(),
            Array.Empty<CrossEventFinalDayRule>());
    }
}
