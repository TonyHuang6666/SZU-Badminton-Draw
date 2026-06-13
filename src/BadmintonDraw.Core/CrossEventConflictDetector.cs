namespace BadmintonDraw.Core;

public sealed class CrossEventConflictDetector
{
    public CrossEventConflictReport Analyze(
        IReadOnlyList<CrossEventScheduleSource> sources,
        int minimumRestMinutes)
    {
        if (minimumRestMinutes < 0)
        {
            throw new DrawValidationException("最小休息间隔不能小于 0 分钟。");
        }

        var sourceSummaries = sources
            .Select(source => new CrossEventConflictSourceSummary(
                source.SourceId,
                source.EventName,
                source.SourcePath,
                source.EventKind,
                source.Matches.Count,
                source.Matches.Sum(match => match.SideAPlayers.Count + match.SideBPlayers.Count),
                source.UnresolvedSideCount))
            .ToList();

        var appearances = sources
            .SelectMany(BuildAppearances)
            .Where(appearance => !string.IsNullOrWhiteSpace(appearance.PlayerName))
            .ToList();
        var issues = new List<CrossEventConflictIssue>();

        foreach (var playerGroup in appearances.GroupBy(
            appearance => appearance.NormalizedPlayerName,
            StringComparer.OrdinalIgnoreCase))
        {
            var playerAppearances = playerGroup
                .Select(appearance => appearance.Appearance)
                .OrderBy(appearance => appearance.DayLabel, StringComparer.Ordinal)
                .ThenBy(appearance => appearance.StartTime)
                .ThenBy(appearance => appearance.EventName, StringComparer.Ordinal)
                .ThenBy(appearance => appearance.MatchOrder)
                .ToList();

            for (var firstIndex = 0; firstIndex < playerAppearances.Count; firstIndex++)
            {
                for (var secondIndex = firstIndex + 1; secondIndex < playerAppearances.Count; secondIndex++)
                {
                    var first = playerAppearances[firstIndex];
                    var second = playerAppearances[secondIndex];
                    if (string.Equals(first.SourceId, second.SourceId, StringComparison.Ordinal)
                        || !string.Equals(first.DayLabel, second.DayLabel, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var issue = BuildIssue(
                        playerGroup.First().PlayerName,
                        playerGroup.Key,
                        first,
                        second,
                        minimumRestMinutes);
                    if (issue is not null)
                    {
                        issues.Add(issue);
                    }
                }
            }
        }

        var orderedIssues = issues
            .OrderBy(issue => SeverityOrder(issue.Severity))
            .ThenBy(issue => issue.DayLabel, StringComparer.Ordinal)
            .ThenBy(issue => issue.FirstMatch.StartTime)
            .ThenBy(issue => issue.PlayerName, StringComparer.Ordinal)
            .ThenBy(issue => issue.FirstMatch.EventName, StringComparer.Ordinal)
            .ThenBy(issue => issue.SecondMatch.EventName, StringComparer.Ordinal)
            .ToList();

        return new CrossEventConflictReport(sourceSummaries, orderedIssues, minimumRestMinutes);
    }

    private static IEnumerable<(string PlayerName, string NormalizedPlayerName, CrossEventPlayerAppearance Appearance)> BuildAppearances(
        CrossEventScheduleSource source)
    {
        foreach (var match in source.Matches)
        {
            foreach (var player in match.SideAPlayers)
            {
                var normalized = NormalizePlayerName(player);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                yield return (
                    player.Trim(),
                    normalized,
                    BuildAppearance(source, match, "A", match.SideA, match.SideB));
            }

            foreach (var player in match.SideBPlayers)
            {
                var normalized = NormalizePlayerName(player);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                yield return (
                    player.Trim(),
                    normalized,
                    BuildAppearance(source, match, "B", match.SideB, match.SideA));
            }
        }
    }

    private static CrossEventPlayerAppearance BuildAppearance(
        CrossEventScheduleSource source,
        CrossEventScheduledMatch match,
        string side,
        string sideText,
        string opponentText)
    {
        return new CrossEventPlayerAppearance(
            source.SourceId,
            source.EventName,
            source.SourcePath,
            source.EventKind,
            match.Order,
            match.DayLabel,
            match.StartTime,
            match.EndTime,
            match.Court,
            match.GroupName,
            match.Phase,
            match.MatchName,
            side,
            sideText,
            opponentText);
    }

    private static CrossEventConflictIssue? BuildIssue(
        string playerName,
        string normalizedPlayerName,
        CrossEventPlayerAppearance first,
        CrossEventPlayerAppearance second,
        int minimumRestMinutes)
    {
        if (second.StartTime < first.StartTime)
        {
            (first, second) = (second, first);
        }

        if (first.StartTime < second.EndTime && second.StartTime < first.EndTime)
        {
            return new CrossEventConflictIssue(
                CrossEventConflictSeverity.Severe,
                playerName,
                normalizedPlayerName,
                first.DayLabel,
                null,
                first,
                second,
                "同一选手在同一时间段有两场跨项目比赛。");
        }

        var restMinutes = first.EndTime <= second.StartTime
            ? (int)(second.StartTime - first.EndTime).TotalMinutes
            : (int)(first.StartTime - second.EndTime).TotalMinutes;
        if (restMinutes < minimumRestMinutes)
        {
            return new CrossEventConflictIssue(
                CrossEventConflictSeverity.Warning,
                playerName,
                normalizedPlayerName,
                first.DayLabel,
                restMinutes,
                first,
                second,
                $"两场跨项目比赛间隔 {restMinutes} 分钟，低于最小休息间隔 {minimumRestMinutes} 分钟。");
        }

        return new CrossEventConflictIssue(
            CrossEventConflictSeverity.Notice,
            playerName,
            normalizedPlayerName,
            first.DayLabel,
            restMinutes,
            first,
            second,
            $"同一选手同日参加多个项目，间隔 {restMinutes} 分钟，建议人工确认体能安排。");
    }

    private static string NormalizePlayerName(string name)
    {
        return string.Concat(name.Trim().Where(character => !char.IsWhiteSpace(character)));
    }

    private static int SeverityOrder(CrossEventConflictSeverity severity)
    {
        return severity switch
        {
            CrossEventConflictSeverity.Severe => 0,
            CrossEventConflictSeverity.Warning => 1,
            _ => 2
        };
    }
}
