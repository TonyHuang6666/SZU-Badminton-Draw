namespace BadmintonDraw.Core;

public sealed record ScheduleDaySettings(
    DateOnly Date,
    TimeOnly DayStart,
    TimeOnly DayEnd,
    IReadOnlyList<string> Courts)
{
    public string DayLabel => Date.ToString("yyyy-MM-dd");
}
