namespace BadmintonDraw.Core.Scheduling;

internal static class TournamentScheduleSpreader
{
    internal static Dictionary<Guid, MatchPlacement> Spread(TournamentPlacementValidator validator, Dictionary<Guid, MatchPlacement> original)
    {
        var context = validator.Context;
        if (context.Request.Policy.Strategy == ScheduleAutoSchedulingStrategy.Compact || context.Request.BaselinePlacements is not null)
            return original;
        var proposal = new Dictionary<Guid, MatchPlacement>(original);
        foreach (var day in context.Days)
        {
            var matches = original.Values.Where(p => p.DayLabel == day.DayLabel).ToArray();
            if (matches.Length < 2) continue;
            var first = matches.Min(p => p.StartTime);
            var last = matches.Max(p => p.StartTime);
            var remaining = (int)(day.DayEnd - matches.Max(p => p.EndTime)).TotalMinutes;
            var span = (last - first).TotalMinutes;
            if (span <= 0 || remaining <= 0) continue;
            foreach (var match in matches)
            {
                if (context.LockedIds.Contains(match.MatchId)) continue;
                var extra = (int)Math.Floor((match.StartTime - first).TotalMinutes / span * remaining);
                proposal[match.MatchId] = match with { StartTime = match.StartTime.AddMinutes(extra), EndTime = match.EndTime.AddMinutes(extra) };
            }
        }
        // A referee window, locked match, dependency or compatible player path can invalidate
        // a spread. Retain the complete previous valid schedule in that case.
        return validator.ValidateSchedule(proposal).IsValid ? proposal : original;
    }
}
