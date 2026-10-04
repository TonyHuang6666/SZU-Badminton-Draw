namespace BadmintonDraw.Core.Scheduling;

internal static class TournamentCandidateEnumeration
{
    // Search tries event boundaries first; the anchored minute grid remains a fallback.
    // Optimizers still request the complete grid by default.
    internal static IEnumerable<TimeOnly> Starts(ScheduleDaySettings day, Guid matchId,
        GraphSchedulingCandidates context, IReadOnlyDictionary<Guid, MatchPlacement> placements, bool includeMinuteGrid = true)
    {
        var duration = context.Durations[matchId];
        var startMinute = day.DayStart.ToTimeSpan().TotalMinutes;
        var latest = day.DayEnd.ToTimeSpan().TotalMinutes - duration;
        var starts = new HashSet<TimeOnly>();
        if (includeMinuteGrid)
            for (var minute = startMinute; minute <= latest; minute++) starts.Add(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute)));
        void Add(TimeOnly time, long offset = 0)
        {
            var minute = time.ToTimeSpan().TotalMinutes + offset;
            if (minute >= startMinute && minute <= latest) starts.Add(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute)));
        }
        Add(day.DayStart);
        if (context.Request.BaselinePlacements?.TryGetValue(matchId, out var baseline) == true && baseline.DayLabel == day.DayLabel) Add(baseline.StartTime);
        foreach (var p in placements.Values)
        {
            // Rest can extend across midnight or several dates. Translate absolute
            // boundaries into this day's coordinates, never wrap a TimeOnly offset.
            var sourceDay = context.Days[context.DayIndexes[p.DayLabel]];
            var offset = (long)(sourceDay.Date.DayNumber - day.Date.DayNumber) * 1440;
            Add(p.EndTime, offset); Add(p.EndTime, offset + context.Request.Resources.MinimumRestMinutes);
            Add(p.StartTime, offset - duration); Add(p.StartTime, offset - duration - context.Request.Resources.MinimumRestMinutes);
        }
        foreach (var window in day.RefereeCapacityWindows ?? []) { Add(window.EndTime); Add(window.StartTime, -duration); }
        foreach (var block in day.UnavailableCourtWindows ?? []) { Add(block.EndTime); Add(block.StartTime, -duration); }
        Add(day.DayEnd, -duration);
        return starts.Order();
    }
}
