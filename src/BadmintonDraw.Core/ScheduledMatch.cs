namespace BadmintonDraw.Core;

public sealed record ScheduledMatch(
    int Order,
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Court,
    int GroupNumber,
    string GroupName,
    string Phase,
    string MatchName,
    string SideA,
    string SideB,
    string Note = "",
    bool SameUnit = false)
{
    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";

    public int DurationMinutes => Math.Max(1, (int)(EndTime - StartTime).TotalMinutes);
}
