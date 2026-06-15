namespace BadmintonDraw.Core;

public static class ScheduleDependencyBackfill
{
    public static SchedulePlan EnrichFromDrawResult(DrawResult result, SchedulePlan schedule)
    {
        if (!schedule.IsComplete || schedule.Matches.Count == 0 || result.Settings.IsRoundRobin)
        {
            return schedule;
        }

        SchedulePlan template;
        try
        {
            template = new ScheduleService().Generate(result, schedule.Settings);
        }
        catch (DrawValidationException)
        {
            return schedule;
        }
        catch (InvalidOperationException)
        {
            return schedule;
        }

        var byIdentity = BuildUniqueIdentityLookup(template.Matches);
        var byName = BuildUniqueNameLookup(template.Matches);
        var changed = false;
        var matches = schedule.Matches
            .Select(match =>
            {
                var templateMatch = TryFindTemplateMatch(match, byIdentity, byName);
                if (templateMatch is null)
                {
                    return match;
                }

                if (string.Equals(match.MatchId, templateMatch.MatchId, StringComparison.Ordinal)
                    && match.Dependencies.SequenceEqual(templateMatch.Dependencies))
                {
                    return match;
                }

                changed = true;
                return match with
                {
                    MatchId = templateMatch.MatchId,
                    Dependencies = templateMatch.Dependencies
                };
            })
            .ToList();

        return changed
            ? schedule with { Matches = matches }
            : schedule;
    }

    private static ScheduledMatch? TryFindTemplateMatch(
        ScheduledMatch match,
        IReadOnlyDictionary<ScheduleMatchIdentity, ScheduledMatch> byIdentity,
        IReadOnlyDictionary<string, ScheduledMatch> byName)
    {
        if (byIdentity.TryGetValue(ScheduleMatchIdentity.From(match), out var templateMatch))
        {
            return templateMatch;
        }

        return byName.TryGetValue(match.MatchName, out templateMatch)
            ? templateMatch
            : null;
    }

    private static IReadOnlyDictionary<ScheduleMatchIdentity, ScheduledMatch> BuildUniqueIdentityLookup(
        IReadOnlyList<ScheduledMatch> matches)
    {
        return matches
            .GroupBy(ScheduleMatchIdentity.From)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First());
    }

    private static IReadOnlyDictionary<string, ScheduledMatch> BuildUniqueNameLookup(
        IReadOnlyList<ScheduledMatch> matches)
    {
        return matches
            .GroupBy(match => match.MatchName, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private sealed record ScheduleMatchIdentity(
        int GroupNumber,
        string GroupName,
        string Phase,
        string MatchName)
    {
        public static ScheduleMatchIdentity From(ScheduledMatch match)
        {
            return new ScheduleMatchIdentity(
                match.GroupNumber,
                match.GroupName,
                match.Phase,
                match.MatchName);
        }
    }
}
