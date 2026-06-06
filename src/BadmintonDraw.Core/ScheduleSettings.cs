namespace BadmintonDraw.Core;

public sealed record ScheduleSettings(
    IReadOnlyList<ScheduleDaySettings> Days,
    int MatchMinutes,
    int BreakMinutes = 0,
    int MaxMatchesPerEntrantPerDay = 2)
{
    public int SlotMinutes => MatchMinutes + BreakMinutes;

    public ScheduleSettings(
        IReadOnlyList<string> Courts,
        TimeOnly DayStart,
        TimeOnly DayEnd,
        int MatchMinutes,
        int BreakMinutes = 0,
        string DayLabelPrefix = "比赛日",
        DateOnly? StartDate = null,
        int MaxMatchesPerEntrantPerDay = 2)
        : this(
            [new ScheduleDaySettings(StartDate ?? DateOnly.FromDateTime(DateTime.Today), DayStart, DayEnd, Courts)],
            MatchMinutes,
            BreakMinutes,
            MaxMatchesPerEntrantPerDay)
    {
    }

    public IReadOnlyList<string> Courts => Days.FirstOrDefault()?.Courts ?? [];

    public TimeOnly DayStart => Days.FirstOrDefault()?.DayStart ?? default;

    public TimeOnly DayEnd => Days.FirstOrDefault()?.DayEnd ?? default;

    public DateOnly? StartDate => Days.FirstOrDefault()?.Date;
}
