using System.Text.Json.Serialization;

namespace BadmintonDraw.Core;

public enum ScheduleAutoSchedulingStrategy
{
    Compact = 0,
    BalancedRelaxed = 1,
    FinalsDayFriendly = 2,
    Custom = 3
}

public sealed record ScheduleDayLoadTarget(
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

public sealed record ScheduleStageWaveTarget(
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

public sealed record ScheduleSchedulingOptions(
    ScheduleAutoSchedulingStrategy Strategy,
    IReadOnlyList<ScheduleDayLoadTarget> DayLoadTargets,
    bool SynchronizeStageWaves,
    IReadOnlyList<ScheduleStageWaveTarget> StageWaveTargets);

[method: JsonConstructor]
public sealed record ScheduleSettings(
    IReadOnlyList<ScheduleDaySettings> Days,
    int MatchMinutes,
    int MaxMatchesPerEntrantPerDay = 2,
    int? KnockoutTimingBoundaryEntrants = null,
    ScheduleTimingSettings? BeforeBoundaryTiming = null,
    int? RefereeCount = null)
{
    public ScheduleConstraintProfile ConstraintProfile { get; init; } = ScheduleConstraintProfile.Campus;

    public ScheduleAutoSchedulingStrategy AutoSchedulingStrategy { get; init; } = ScheduleAutoSchedulingStrategy.Compact;

    public IReadOnlyList<ScheduleDayLoadTarget> DayLoadTargets { get; init; } = Array.Empty<ScheduleDayLoadTarget>();

    public bool SynchronizeStageWaves { get; init; }

    public IReadOnlyList<ScheduleStageWaveTarget> StageWaveTargets { get; init; } = Array.Empty<ScheduleStageWaveTarget>();

    public bool HasKnockoutTimingSplit => KnockoutTimingBoundaryEntrants is > 0 && BeforeBoundaryTiming is not null;

    public int MinimumMatchMinutes => HasKnockoutTimingSplit
        ? Math.Min(MatchMinutes, BeforeBoundaryTiming!.MatchMinutes)
        : MatchMinutes;

    public int MaximumMatchMinutes => HasKnockoutTimingSplit
        ? Math.Max(MatchMinutes, BeforeBoundaryTiming!.MatchMinutes)
        : MatchMinutes;

    public ScheduleSettings(
        IReadOnlyList<string> Courts,
        TimeOnly DayStart,
        TimeOnly DayEnd,
        int MatchMinutes,
        string DayLabelPrefix = "比赛日",
        DateOnly? StartDate = null,
        int MaxMatchesPerEntrantPerDay = 2,
        int? KnockoutTimingBoundaryEntrants = null,
        ScheduleTimingSettings? BeforeBoundaryTiming = null,
        int? RefereeCount = null,
        ScheduleConstraintProfile constraintProfile = ScheduleConstraintProfile.Campus,
        ScheduleAutoSchedulingStrategy autoSchedulingStrategy = ScheduleAutoSchedulingStrategy.Compact)
        : this(
            [new ScheduleDaySettings(StartDate ?? DateOnly.FromDateTime(DateTime.Today), DayStart, DayEnd, Courts)],
            MatchMinutes,
            MaxMatchesPerEntrantPerDay,
            KnockoutTimingBoundaryEntrants,
            BeforeBoundaryTiming,
            RefereeCount)
    {
        ConstraintProfile = constraintProfile;
        AutoSchedulingStrategy = autoSchedulingStrategy;
    }

    public IReadOnlyList<string> Courts => Days.FirstOrDefault()?.Courts ?? [];

    public TimeOnly DayStart => Days.FirstOrDefault()?.DayStart ?? default;

    public TimeOnly DayEnd => Days.FirstOrDefault()?.DayEnd ?? default;

    public DateOnly? StartDate => Days.FirstOrDefault()?.Date;
}
