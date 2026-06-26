namespace BadmintonDraw.Core;

public sealed record ScheduleRefereeCapacityWindow(
    TimeOnly StartTime,
    TimeOnly EndTime,
    int RefereeCount)
{
    public bool Overlaps(TimeOnly start, TimeOnly end)
    {
        return StartTime < end && start < EndTime;
    }
}

public sealed record ScheduleCourtAvailabilityBlock(
    TimeOnly StartTime,
    TimeOnly EndTime,
    IReadOnlyList<string> Courts)
{
    public bool AppliesTo(string court)
    {
        return Courts.Count == 0 || Courts.Contains(court, StringComparer.OrdinalIgnoreCase);
    }

    public bool Overlaps(TimeOnly start, TimeOnly end)
    {
        return StartTime < end && start < EndTime;
    }
}

public static class ScheduleResourceCalculator
{
    public static bool IsCourtAvailable(
        ScheduleDaySettings day,
        string court,
        TimeOnly start,
        TimeOnly end)
    {
        return !GetUnavailableCourtWindows(day).Any(block =>
            block.AppliesTo(court) && block.Overlaps(start, end));
    }

    public static bool IsCourtAvailable(
        CrossEventScheduleBoardDay day,
        string court,
        TimeOnly start,
        TimeOnly end)
    {
        return !GetUnavailableCourtWindows(day).Any(block =>
            block.AppliesTo(court) && block.Overlaps(start, end));
    }

    public static int GetConcurrentMatchLimit(
        ScheduleDaySettings day,
        int? defaultRefereeCount,
        TimeOnly start,
        TimeOnly end)
    {
        return GetConcurrentMatchLimit(
            day.Courts,
            GetRefereeCapacityWindows(day),
            GetUnavailableCourtWindows(day),
            defaultRefereeCount,
            start,
            end);
    }

    public static int GetConcurrentMatchLimit(
        CrossEventScheduleBoardDay day,
        int? defaultRefereeCount,
        TimeOnly start,
        TimeOnly end)
    {
        return GetConcurrentMatchLimit(
            day.Courts,
            GetRefereeCapacityWindows(day),
            GetUnavailableCourtWindows(day),
            defaultRefereeCount,
            start,
            end);
    }

    public static int CalculateDayCapacityMinutes(
        ScheduleDaySettings day,
        int? defaultRefereeCount,
        int slotMinutes)
    {
        return CalculateDayCapacityMinutes(
            day.DayStart,
            day.DayEnd,
            day.Courts,
            GetRefereeCapacityWindows(day),
            GetUnavailableCourtWindows(day),
            defaultRefereeCount,
            slotMinutes);
    }

    public static int CalculateDayCapacityMinutes(
        CrossEventScheduleBoardDay day,
        int? defaultRefereeCount,
        int slotMinutes)
    {
        return CalculateDayCapacityMinutes(
            day.StartTime,
            day.EndTime,
            day.Courts,
            GetRefereeCapacityWindows(day),
            GetUnavailableCourtWindows(day),
            defaultRefereeCount,
            slotMinutes);
    }

    private static int CalculateDayCapacityMinutes(
        TimeOnly dayStart,
        TimeOnly dayEnd,
        IReadOnlyList<string> courts,
        IReadOnlyList<ScheduleRefereeCapacityWindow> refereeWindows,
        IReadOnlyList<ScheduleCourtAvailabilityBlock> unavailableWindows,
        int? defaultRefereeCount,
        int slotMinutes)
    {
        var step = Math.Max(1, slotMinutes);
        var capacity = 0;
        for (var cursor = dayStart; cursor < dayEnd;)
        {
            var next = cursor.AddMinutes(step);
            if (next > dayEnd)
            {
                next = dayEnd;
            }

            var limit = GetConcurrentMatchLimit(
                courts,
                refereeWindows,
                unavailableWindows,
                defaultRefereeCount,
                cursor,
                next);
            capacity += Math.Max(1, (int)(next - cursor).TotalMinutes) * limit;
            cursor = next;
        }

        return Math.Max(1, capacity);
    }

    private static int GetConcurrentMatchLimit(
        IReadOnlyList<string> courts,
        IReadOnlyList<ScheduleRefereeCapacityWindow> refereeWindows,
        IReadOnlyList<ScheduleCourtAvailabilityBlock> unavailableWindows,
        int? defaultRefereeCount,
        TimeOnly start,
        TimeOnly end)
    {
        var availableCourtCount = courts.Count(court =>
            !unavailableWindows.Any(block => block.AppliesTo(court) && block.Overlaps(start, end)));
        var limit = defaultRefereeCount is > 0
            ? Math.Min(availableCourtCount, defaultRefereeCount.Value)
            : availableCourtCount;

        foreach (var window in refereeWindows.Where(window => window.Overlaps(start, end)))
        {
            limit = Math.Min(limit, window.RefereeCount);
        }

        return Math.Max(0, limit);
    }

    private static IReadOnlyList<ScheduleRefereeCapacityWindow> GetRefereeCapacityWindows(ScheduleDaySettings day)
    {
        return day.RefereeCapacityWindows ?? Array.Empty<ScheduleRefereeCapacityWindow>();
    }

    private static IReadOnlyList<ScheduleRefereeCapacityWindow> GetRefereeCapacityWindows(CrossEventScheduleBoardDay day)
    {
        return day.RefereeCapacityWindows ?? Array.Empty<ScheduleRefereeCapacityWindow>();
    }

    private static IReadOnlyList<ScheduleCourtAvailabilityBlock> GetUnavailableCourtWindows(ScheduleDaySettings day)
    {
        return day.UnavailableCourtWindows ?? Array.Empty<ScheduleCourtAvailabilityBlock>();
    }

    private static IReadOnlyList<ScheduleCourtAvailabilityBlock> GetUnavailableCourtWindows(CrossEventScheduleBoardDay day)
    {
        return day.UnavailableCourtWindows ?? Array.Empty<ScheduleCourtAvailabilityBlock>();
    }
}
