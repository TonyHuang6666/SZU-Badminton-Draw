namespace BadmintonDraw.Core;

public sealed record SchedulePlan(
    IReadOnlyList<ScheduledMatch> Matches,
    ScheduleSettings Settings)
{
    public int DayCount => Matches
        .Select(match => match.DayLabel)
        .Distinct(StringComparer.Ordinal)
        .Count();
}
