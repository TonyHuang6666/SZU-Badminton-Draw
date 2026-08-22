using System.Text.RegularExpressions;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows;

public sealed partial class CrossEventConflictWorkflow
{
    private CrossEventConflictReport BuildBoardConflictReport(
        IReadOnlyList<CrossEventScheduleSource> sources,
        int minimumRestMinutes)
    {
        // All board badges, blocking counts and exported audit totals derive from this one report;
        // resource and probabilistic-load issues are merged before the board is materialized.
        var report = _detector.Analyze(sources, minimumRestMinutes);
        var courtIssues = BuildCourtOverlapIssues(sources);
        var loadForecastIssues = BuildCrossEventLoadForecastIssues(sources);
        var extraIssues = courtIssues.Concat(loadForecastIssues).ToList();
        return extraIssues.Count == 0
            ? report
            : report with
            {
                Issues = report.Issues.Concat(extraIssues).ToList()
            };
    }


    private static IReadOnlyList<CrossEventPlayerMultiEntry> BuildPlayerDetails(
        IReadOnlyList<CrossEventScheduleSource> sources,
        IReadOnlyList<CrossEventScheduleBoardItem> items,
        CrossEventConflictReport report)
    {
        var itemLookup = items.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var issueLookup = BuildPlayerIssueLookup(report);
        var issueGroups = report.Issues
            .GroupBy(issue => issue.NormalizedPlayerName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var appearances = BuildPlayerScheduleAppearances(sources, itemLookup, issueLookup)
            .GroupBy(appearance => appearance.NormalizedPlayerName, StringComparer.OrdinalIgnoreCase);

        return appearances
            .Select(group =>
            {
                var orderedAppearances = group
                    .Select(appearance => appearance.Appearance)
                    .OrderBy(appearance => appearance.DayLabel, StringComparer.Ordinal)
                    .ThenBy(appearance => appearance.StartTime)
                    .ThenBy(appearance => appearance.EventName, StringComparer.Ordinal)
                    .ThenBy(appearance => appearance.MatchName, StringComparer.Ordinal)
                    .ToList();
                var eventNames = orderedAppearances
                    .Select(appearance => appearance.EventName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
                if (eventNames.Count < 2)
                {
                    return null;
                }

                issueGroups.TryGetValue(group.Key, out var playerIssues);
                playerIssues ??= [];
                var nextMatch = orderedAppearances.FirstOrDefault(appearance => !appearance.IsCompleted);
                var restMinutes = playerIssues
                    .Where(issue => issue.RestMinutes.HasValue)
                    .Select(issue => issue.RestMinutes!.Value)
                    .ToList();
                return new CrossEventPlayerMultiEntry(
                    group.First().PlayerName,
                    group.Key,
                    eventNames,
                    orderedAppearances.Count,
                    orderedAppearances.Count(appearance => appearance.IsCompleted),
                    orderedAppearances.Count(appearance => !appearance.IsCompleted),
                    playerIssues.Count(issue => issue.Severity == CrossEventConflictSeverity.Severe),
                    playerIssues.Count(issue => issue.Severity == CrossEventConflictSeverity.Warning),
                    restMinutes.Count == 0 ? null : restMinutes.Min(),
                    nextMatch is null
                        ? "暂无未完成比赛"
                        : $"{nextMatch.DayLabel} {nextMatch.TimeRange} {nextMatch.Court} {nextMatch.EventName} {nextMatch.MatchName}",
                    orderedAppearances);
            })
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .OrderByDescending(entry => entry.HasBlockingIssues)
            .ThenByDescending(entry => entry.EventCount)
            .ThenBy(entry => entry.PlayerName, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<PlayerAppearanceBuilder> BuildPlayerScheduleAppearances(
        IReadOnlyList<CrossEventScheduleSource> sources,
        IReadOnlyDictionary<string, CrossEventScheduleBoardItem> itemLookup,
        IReadOnlyDictionary<string, BoardConflictAccumulator> issueLookup)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            foreach (var match in source.Matches)
            {
                foreach (var player in match.SideAPlayerIdentities)
                {
                    foreach (var appearance in BuildPlayerScheduleAppearance(
                                 source,
                                 match,
                                 player,
                                 "A",
                                 match.SideA,
                                 match.SideB,
                                 itemLookup,
                                 issueLookup,
                                 seen))
                    {
                        yield return appearance;
                    }
                }

                foreach (var player in match.SideBPlayerIdentities)
                {
                    foreach (var appearance in BuildPlayerScheduleAppearance(
                                 source,
                                 match,
                                 player,
                                 "B",
                                 match.SideB,
                                 match.SideA,
                                 itemLookup,
                                 issueLookup,
                                 seen))
                    {
                        yield return appearance;
                    }
                }
            }
        }
    }

    private static IEnumerable<PlayerAppearanceBuilder> BuildPlayerScheduleAppearance(
        CrossEventScheduleSource source,
        CrossEventScheduledMatch match,
        CrossEventPlayerIdentity player,
        string side,
        string sideText,
        string opponentText,
        IReadOnlyDictionary<string, CrossEventScheduleBoardItem> itemLookup,
        IReadOnlyDictionary<string, BoardConflictAccumulator> issueLookup,
        ISet<string> seen)
    {
        var normalized = player.IdentityKey;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        var itemKey = BuildItemKey(source.SourceId, match.MatchName);
        var uniqueKey = $"{normalized}{ItemKeySeparator}{itemKey}{ItemKeySeparator}{side}";
        if (!seen.Add(uniqueKey) || !itemLookup.TryGetValue(itemKey, out var item))
        {
            yield break;
        }

        issueLookup.TryGetValue(BuildPlayerIssueKey(normalized, itemKey), out var issue);
        yield return new PlayerAppearanceBuilder(
            player.DisplayName,
            normalized,
            new CrossEventPlayerScheduleAppearance(
                itemKey,
                source.EventName,
                match.DayLabel,
                match.StartTime,
                match.EndTime,
                match.Court,
                match.Phase,
                match.MatchName,
                side,
                sideText,
                opponentText,
                item.IsCompleted,
                issue?.Severity,
                issue is null ? "" : string.Join("；", issue.Messages.Distinct(StringComparer.Ordinal))));
    }

    private static Dictionary<string, BoardConflictAccumulator> BuildPlayerIssueLookup(CrossEventConflictReport report)
    {
        var lookup = new Dictionary<string, BoardConflictAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in report.Issues.Where(issue => issue.Severity != CrossEventConflictSeverity.Notice))
        {
            AddPlayerIssue(lookup, issue, issue.FirstMatch);
            AddPlayerIssue(lookup, issue, issue.SecondMatch);
        }

        return lookup;
    }

    private static void AddPlayerIssue(
        IDictionary<string, BoardConflictAccumulator> lookup,
        CrossEventConflictIssue issue,
        CrossEventPlayerAppearance appearance)
    {
        var itemKey = BuildItemKey(appearance.SourceId, appearance.MatchName);
        var key = BuildPlayerIssueKey(issue.NormalizedPlayerName, itemKey);
        AddConflict(lookup, key, issue.Severity, $"{issue.PlayerName}：{issue.Detail}");
    }

    private static Dictionary<string, BoardConflictAccumulator> BuildBoardConflicts(CrossEventConflictReport report)
    {
        var conflicts = new Dictionary<string, BoardConflictAccumulator>(StringComparer.Ordinal);
        foreach (var issue in report.Issues.Where(issue => issue.Severity != CrossEventConflictSeverity.Notice))
        {
            var firstKey = BuildItemKey(issue.FirstMatch.SourceId, issue.FirstMatch.MatchName);
            var secondKey = BuildItemKey(issue.SecondMatch.SourceId, issue.SecondMatch.MatchName);
            AddConflict(conflicts, firstKey, issue.Severity, $"{issue.PlayerName}：{issue.Detail}");
            AddConflict(conflicts, secondKey, issue.Severity, $"{issue.PlayerName}：{issue.Detail}");
        }

        return conflicts;
    }

    private static IReadOnlyList<CrossEventConflictIssue> BuildCourtOverlapIssues(
        IReadOnlyList<CrossEventScheduleSource> sources)
    {
        var issues = new List<CrossEventConflictIssue>();
        foreach (var courtGroup in sources
                     .SelectMany(source => source.Matches.Select(match => (Source: source, Match: match)))
                     .Where(item => !string.IsNullOrWhiteSpace(item.Match.DayLabel)
                                    && !string.IsNullOrWhiteSpace(item.Match.Court))
                     .GroupBy(item => (item.Match.DayLabel, item.Match.Court)))
        {
            var matches = courtGroup
                .OrderBy(item => item.Match.StartTime)
                .ThenBy(item => item.Source.EventName, StringComparer.Ordinal)
                .ThenBy(item => item.Match.MatchName, StringComparer.Ordinal)
                .ToList();
            for (var firstIndex = 0; firstIndex < matches.Count; firstIndex++)
            {
                for (var secondIndex = firstIndex + 1; secondIndex < matches.Count; secondIndex++)
                {
                    var first = matches[firstIndex];
                    var second = matches[secondIndex];
                    if (first.Match.EndTime <= second.Match.StartTime)
                    {
                        continue;
                    }

                    var dayLabel = courtGroup.Key.DayLabel;
                    var court = courtGroup.Key.Court;
                    var detail =
                        $"{dayLabel} {court} 同一场地时间重叠：{first.Source.EventName} {first.Match.MatchName} 与 {second.Source.EventName} {second.Match.MatchName}。";
                    issues.Add(new CrossEventConflictIssue(
                        CrossEventConflictSeverity.Severe,
                        $"场地 {court}",
                        $"court:{dayLabel}:{court}",
                        dayLabel,
                        null,
                        BuildCourtConflictAppearance(first.Source, first.Match, second.Match),
                        BuildCourtConflictAppearance(second.Source, second.Match, first.Match),
                        detail));
                }
            }
        }

        return issues;
    }

    private static IReadOnlyList<CrossEventConflictIssue> BuildCrossEventLoadForecastIssues(
        IReadOnlyList<CrossEventScheduleSource> sources)
    {
        var analyzer = new PlayerLoadForecastAnalyzer();
        var contributions = new List<CrossEventLoadForecastContribution>();
        foreach (var source in sources)
        {
            var schedule = BuildSchedulePlan(source);
            var forecasts = analyzer.Analyze(
                schedule,
                maxProjectedDepth: 4,
                dailyLimit: CrossEventScheduleRules.MaxPlayerMatchesPerDay);
            foreach (var forecast in forecasts.Where(forecast => forecast.MaximumCount > 0))
            {
                var anchor = BuildForecastAnchorAppearance(source, forecast);
                if (anchor is null)
                {
                    continue;
                }

                contributions.Add(new CrossEventLoadForecastContribution(source, forecast, anchor));
            }
        }

        var issues = new List<CrossEventConflictIssue>();
        foreach (var group in contributions.GroupBy(
                     contribution => $"{contribution.Forecast.NormalizedPlayerName}\u001F{contribution.Forecast.DayLabel}",
                     StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.ToList();
            var eventCount = entries
                .Select(entry => entry.Source.SourceId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (eventCount < 2 || !entries.Any(entry => entry.Forecast.HasProjectedAppearances))
            {
                continue;
            }

            var confirmedCount = entries.Sum(entry => entry.Forecast.ConfirmedCount);
            if (confirmedCount > CrossEventScheduleRules.MaxPlayerMatchesPerDay)
            {
                continue;
            }

            var distribution = ConvolveDistributions(entries.Select(entry => entry.Forecast.Distribution));
            var maximumCount = distribution.Keys.DefaultIfEmpty(0).Max();
            if (maximumCount < CrossEventScheduleRules.MaxPlayerMatchesPerDay)
            {
                continue;
            }

            var probabilityAtLimit = distribution
                .Where(pair => pair.Key >= CrossEventScheduleRules.MaxPlayerMatchesPerDay)
                .Sum(pair => pair.Value);
            if (probabilityAtLimit <= 0)
            {
                continue;
            }

            var expectedCount = distribution.Sum(pair => pair.Key * pair.Value);
            var orderedAnchors = entries
                .OrderBy(entry => entry.Anchor.DayLabel, StringComparer.Ordinal)
                .ThenBy(entry => entry.Anchor.StartTime)
                .ThenBy(entry => entry.Source.EventName, StringComparer.Ordinal)
                .ToList();
            var first = orderedAnchors[0].Anchor;
            var secondContribution = orderedAnchors
                .Skip(1)
                .FirstOrDefault(entry => !string.Equals(entry.Source.SourceId, first.SourceId, StringComparison.Ordinal))
                ?? orderedAnchors.Skip(1).FirstOrDefault();
            var second = secondContribution?.Anchor ?? first;
            var playerName = entries[0].Forecast.PlayerName;
            var detail =
                $"负荷推演：{playerName} 在 {entries[0].Forecast.DayLabel} 跨项目最高可能 {maximumCount}/{CrossEventScheduleRules.MaxPlayerMatchesPerDay} 场，"
                + $"达到或超过每日上限概率约 {FormatProbability(probabilityAtLimit)}，期望 {expectedCount:0.0} 场。"
                + $"分布：{FormatDistribution(distribution)}。来源：{FormatCrossEventForecastSources(entries)}。"
                + "此为未决淘汰路径概率提醒，不作为硬冲突。";
            issues.Add(new CrossEventConflictIssue(
                CrossEventConflictSeverity.Notice,
                playerName,
                entries[0].Forecast.NormalizedPlayerName,
                entries[0].Forecast.DayLabel,
                null,
                first,
                second,
                detail));
        }

        return issues;
    }

    private static CrossEventPlayerAppearance? BuildForecastAnchorAppearance(
        CrossEventScheduleSource source,
        PlayerDailyLoadForecast forecast)
    {
        var appearance = forecast.Appearances
            .OrderBy(item => item.StartTime)
            .ThenBy(item => item.Court, StringComparer.Ordinal)
            .FirstOrDefault();
        if (appearance is null)
        {
            return null;
        }

        var match = source.Matches.FirstOrDefault(match =>
            string.Equals(match.MatchId, appearance.MatchId, StringComparison.Ordinal)
            || string.Equals(match.MatchName, appearance.MatchName, StringComparison.Ordinal));
        if (match is null)
        {
            return null;
        }

        var side = "推演";
        var sideText = appearance.PlayerName;
        var opponentText = $"{match.SideA} vs {match.SideB}";
        if (match.SideAPlayerIdentities.Any(identity =>
                string.Equals(identity.IdentityKey, forecast.NormalizedPlayerName, StringComparison.OrdinalIgnoreCase)))
        {
            side = "A";
            sideText = match.SideA;
            opponentText = match.SideB;
        }
        else if (match.SideBPlayerIdentities.Any(identity =>
                     string.Equals(identity.IdentityKey, forecast.NormalizedPlayerName, StringComparison.OrdinalIgnoreCase)))
        {
            side = "B";
            sideText = match.SideB;
            opponentText = match.SideA;
        }

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

    private static IReadOnlyDictionary<int, double> ConvolveDistributions(
        IEnumerable<IReadOnlyDictionary<int, double>> distributions)
    {
        var result = new Dictionary<int, double> { [0] = 1.0 };
        foreach (var distribution in distributions)
        {
            var next = new Dictionary<int, double>();
            foreach (var left in result)
            {
                foreach (var right in distribution)
                {
                    var count = left.Key + right.Key;
                    var probability = left.Value * right.Value;
                    next[count] = next.TryGetValue(count, out var existing)
                        ? existing + probability
                        : probability;
                }
            }

            result = next;
        }

        return result
            .OrderBy(pair => pair.Key)
            .ToDictionary(pair => pair.Key, pair => Math.Round(pair.Value, 8));
    }

    private static string FormatCrossEventForecastSources(
        IReadOnlyList<CrossEventLoadForecastContribution> contributions)
    {
        return string.Join(
            "；",
            contributions
                .OrderBy(item => item.Source.EventName, StringComparer.Ordinal)
                .Select(item =>
                    $"{item.Source.EventName}最高{item.Forecast.MaximumCount}场({FormatDistribution(item.Forecast.Distribution)})"));
    }

    private static string FormatProbability(double probability)
    {
        return $"{Math.Clamp(probability, 0.0, 1.0) * 100:0.#}%";
    }

    private static string FormatDistribution(IReadOnlyDictionary<int, double> distribution)
    {
        return string.Join(
            "，",
            distribution
                .OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}场 {FormatProbability(pair.Value)}"));
    }

    private static CrossEventPlayerAppearance BuildCourtConflictAppearance(
        CrossEventScheduleSource source,
        CrossEventScheduledMatch match,
        CrossEventScheduledMatch opponentMatch)
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
            "场地",
            $"{match.SideA} vs {match.SideB}",
            $"{opponentMatch.SideA} vs {opponentMatch.SideB}");
    }

    private static void AddConflict(
        IDictionary<string, BoardConflictAccumulator> conflicts,
        string key,
        CrossEventConflictSeverity severity,
        string message)
    {
        if (!conflicts.TryGetValue(key, out var accumulator))
        {
            accumulator = new BoardConflictAccumulator(severity);
            conflicts[key] = accumulator;
        }

        if (SeverityOrder(severity) < SeverityOrder(accumulator.Severity))
        {
            accumulator.Severity = severity;
        }

        accumulator.Messages.Add(message);
    }
}
