using System.Text.Json.Serialization;

namespace BadmintonDraw.Core;

[method: JsonConstructor]
public sealed record ScheduleSettings(
    IReadOnlyList<ScheduleDaySettings> Days,
    int MatchMinutes,
    int MaxMatchesPerEntrantPerDay = 2,
    int? KnockoutTimingBoundaryEntrants = null,
    ScheduleTimingSettings? BeforeBoundaryTiming = null)
{
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
        ScheduleTimingSettings? BeforeBoundaryTiming = null)
        : this(
            [new ScheduleDaySettings(StartDate ?? DateOnly.FromDateTime(DateTime.Today), DayStart, DayEnd, Courts)],
            MatchMinutes,
            MaxMatchesPerEntrantPerDay,
            KnockoutTimingBoundaryEntrants,
            BeforeBoundaryTiming)
    {
    }

    public IReadOnlyList<string> Courts => Days.FirstOrDefault()?.Courts ?? [];

    public TimeOnly DayStart => Days.FirstOrDefault()?.DayStart ?? default;

    public TimeOnly DayEnd => Days.FirstOrDefault()?.DayEnd ?? default;

    public DateOnly? StartDate => Days.FirstOrDefault()?.Date;
}
