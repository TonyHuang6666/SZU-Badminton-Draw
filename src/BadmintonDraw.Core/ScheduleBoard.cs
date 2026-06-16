namespace BadmintonDraw.Core;

public enum ScheduleBoardKind
{
    SingleEvent,
    CrossEvent
}

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

public sealed record ScheduleBoardView(
    ScheduleBoardKind Kind,
    IReadOnlyList<ScheduleBoardDay> Days,
    IReadOnlyList<ScheduleBoardItem> Items,
    string EmptyDayText = "当前比赛日没有赛程。")
{
    public IReadOnlyList<string> DayLabels => Days.Select(day => day.DayLabel).ToList();

    public ScheduleBoardDay? FindDay(string? dayLabel)
    {
        if (string.IsNullOrWhiteSpace(dayLabel))
        {
            return null;
        }

        return Days.FirstOrDefault(day => string.Equals(day.DayLabel, dayLabel, StringComparison.Ordinal));
    }

    public IReadOnlyList<ScheduleBoardItem> GetItems(string dayLabel, string court, TimeOnly startTime)
    {
        return Items
            .Where(item => string.Equals(item.DayLabel, dayLabel, StringComparison.Ordinal)
                           && string.Equals(item.Court, court, StringComparison.Ordinal)
                           && item.StartTime == startTime)
            .OrderBy(item => item.SortText, StringComparer.Ordinal)
            .ThenBy(item => item.Order)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToList();
    }
}

public sealed record ScheduleBoardItem(
    string Key,
    string DragPayload,
    string FocusKey,
    string DayLabel,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Court,
    int Order,
    string Title,
    string Subtitle,
    string SideText,
    string DetailText = "",
    string Tooltip = "",
    bool IsLocked = false,
    bool IsBlocking = false,
    string SortText = "")
{
    public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";
}

public sealed record ScheduleBoardDropTarget(
    ScheduleBoardKind Kind,
    string DayLabel,
    TimeOnly StartTime,
    string Court);

public static class ScheduleBoardDrag
{
    public const string SingleEventPrefix = "schedule:";

    public static string BuildSingleEventPayload(string matchName)
    {
        return $"{SingleEventPrefix}{matchName}";
    }

    public static bool TryParseSingleEventPayload(string? payload, out string matchName)
    {
        if (!string.IsNullOrWhiteSpace(payload)
            && payload.StartsWith(SingleEventPrefix, StringComparison.Ordinal))
        {
            matchName = payload[SingleEventPrefix.Length..];
            return !string.IsNullOrWhiteSpace(matchName);
        }

        matchName = "";
        return false;
    }
}

public static class ScheduleBoardLayout
{
    public const double MainMinZoom = 0.65;
    public const double WindowMinZoom = 0.25;
    public const double MaxZoom = 1.6;
    public const double ZoomStep = 0.15;

    public static double ClampMainZoom(double zoom)
    {
        return Math.Clamp(zoom, MainMinZoom, MaxZoom);
    }

    public static double ClampWindowZoom(double zoom)
    {
        return Math.Clamp(zoom, WindowMinZoom, MaxZoom);
    }

    public static double Scale(double value, double zoom)
    {
        return Math.Max(1, value * zoom);
    }

    public static double ScaleFont(double value, double zoom)
    {
        return Math.Max(8, value * zoom);
    }
}
