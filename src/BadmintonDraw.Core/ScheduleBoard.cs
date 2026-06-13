namespace BadmintonDraw.Core;

public sealed record ScheduleBoardDay(
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    IReadOnlyList<string> Courts,
    int SlotMinutes,
    IReadOnlyList<TimeOnly> TimeSlots)
{
    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";
}
