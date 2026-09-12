using System.Text.RegularExpressions;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows;

public sealed partial class CrossEventConflictWorkflow
{
    public CrossEventScheduleAutoAdjustResult AutoAdjustScheduleBoard(
        CrossEventScheduleBoard board,
        CrossEventSchedulingOptions? options = null)
    {
        // The input board is the stable baseline. Completed matches are locked, and unresolved
        // entries retain their original placement so the returned candidate can be audited.
        // Callers accept it only when the rebuilt board has no blocking conflicts.
        options ??= board.SchedulingOptions ?? CreateDefaultSchedulingOptions(board, CrossEventSchedulingStrategy.BalancedRelaxed);
        var originalPlacements = board.Items.ToDictionary(item => item.Key, item => item, StringComparer.Ordinal);
        var entries = BuildGlobalScheduleEntries(board);
        var entryLookup = entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var dayNumbers = BuildBoardDayNumberLookup(board.Days);
        var schedulingContext = BuildSchedulingContext(board, entries, options);
        var placements = entries
            .Where(entry => entry.Match.IsCompleted)
            .ToDictionary(
                entry => entry.Key,
                entry => new GlobalSchedulePlacement(
                    entry.Match.DayLabel,
                    entry.Match.StartTime,
                    entry.Match.EndTime,
                    entry.Match.Court),
                StringComparer.Ordinal);
        var placedMinutesByDay = placements
            .Values
            .GroupBy(placement => placement.DayLabel, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(placement => Math.Max(1, (int)(placement.EndTime - placement.StartTime).TotalMinutes)),
                StringComparer.Ordinal);
        var messages = new List<string>();

        foreach (var entry in BuildGlobalScheduleOrder(entries))
        {
            if (entry.Match.IsCompleted)
            {
                continue;
            }

            var placement = FindBestGlobalPlacement(
                board,
                entry,
                placements,
                entryLookup,
                dayNumbers,
                schedulingContext,
                placedMinutesByDay);
            if (placement is null)
            {
                placements[entry.Key] = new GlobalSchedulePlacement(
                    entry.Match.DayLabel,
                    entry.Match.StartTime,
                    entry.Match.EndTime,
                    entry.Match.Court);
                messages.Add($"{entry.Source.EventName} {entry.Match.MatchName} 未找到满足依赖、场地、裁判人数、每日上限和休息约束的全局位置，已保留原位置。");
                continue;
            }

            placements[entry.Key] = placement;
            placedMinutesByDay.TryGetValue(placement.DayLabel, out var dayMinutes);
            placedMinutesByDay[placement.DayLabel] = dayMinutes + entry.Match.DurationMinutes;
        }

        var sources = ApplyGlobalPlacements(board.Sources, placements);
        var working = BuildScheduleBoard(sources, board.MinimumRestMinutes, hasUnsavedChanges: true, options);
        var movedCount = working.Items.Count(item =>
            originalPlacements.TryGetValue(item.Key, out var original)
            && !item.IsCompleted
            && (!string.Equals(item.DayLabel, original.DayLabel, StringComparison.Ordinal)
                || item.StartTime != original.StartTime
                || !string.Equals(item.Court, original.Court, StringComparison.Ordinal)));
        working = working with
        {
            QualityReport = BuildCrossEventQualityReport(working, movedCount, messages)
        };

        return new CrossEventScheduleAutoAdjustResult(
            working,
            movedCount,
            working.BlockingConflictItemCount,
            messages);
    }


    private static CrossEventSchedulingOptions CreateDefaultSchedulingOptions(
        CrossEventScheduleBoard board,
        CrossEventSchedulingStrategy strategy)
    {
        var orderedDays = board.Days.OrderBy(day => day.DayLabel, StringComparer.Ordinal).ToList();
        if (orderedDays.Count == 0)
        {
            return CrossEventSchedulingOptions.Empty(strategy);
        }

        var capacityByDay = orderedDays.ToDictionary(
            day => day.DayLabel,
            day => CalculateDayCapacityMinutes(day, null),
            StringComparer.Ordinal);
        var totalMinutes = Math.Max(1, board.Items.Sum(item => item.DurationMinutes));
        var targets = BuildDefaultDayLoadTargets(orderedDays, capacityByDay, totalMinutes, strategy);
        var stageTargets = BuildDefaultStageWaveTargets(orderedDays, strategy);
        var finalDayRules = BuildDefaultFinalDayRules(board, strategy);

        return new CrossEventSchedulingOptions(
            strategy,
            targets,
            SynchronizeStageWaves: strategy is CrossEventSchedulingStrategy.BalancedRelaxed
                or CrossEventSchedulingStrategy.FinalsDayFriendly
                or CrossEventSchedulingStrategy.Custom,
            stageTargets,
            finalDayRules);
    }

    private static IReadOnlyList<CrossEventDayLoadTarget> BuildDefaultDayLoadTargets(
        IReadOnlyList<CrossEventScheduleBoardDay> days,
        IReadOnlyDictionary<string, int> capacityByDay,
        int totalMinutes,
        CrossEventSchedulingStrategy strategy)
    {
        if (strategy == CrossEventSchedulingStrategy.Compact)
        {
            return days
                .Select(day => new CrossEventDayLoadTarget(day.DayLabel, 0.95, 1.0))
                .ToList();
        }

        if (days.Count == 1)
        {
            var singleTarget = strategy == CrossEventSchedulingStrategy.FinalsDayFriendly ? 0.75 : 0.8;
            return [new CrossEventDayLoadTarget(days[0].DayLabel, singleTarget, Math.Min(1.0, singleTarget + 0.15))];
        }

        var result = new List<CrossEventDayLoadTarget>();
        var remainingMinutes = totalMinutes;
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var capacity = Math.Max(1, capacityByDay[day.DayLabel]);
            var remainingDays = days.Count - index;
            var isLast = index == days.Count - 1;
            double target;

            if (strategy == CrossEventSchedulingStrategy.FinalsDayFriendly)
            {
                target = index switch
                {
                    0 => 0.64,
                    _ when isLast => Math.Clamp((totalMinutes * 0.18) / capacity, 0.28, 0.32),
                    _ => 0.56
                };
            }
            else
            {
                target = index switch
                {
                    0 => 0.68,
                    _ when isLast => Math.Clamp((totalMinutes * 0.14) / capacity, 0.25, 0.45),
                    _ => 0.56
                };
            }

            var targetMinutes = (int)Math.Round(capacity * target);
            remainingMinutes = Math.Max(0, remainingMinutes - targetMinutes);
            result.Add(new CrossEventDayLoadTarget(day.DayLabel, target, Math.Min(1.0, target + 0.15)));
        }

        return result;
    }

    private static IReadOnlyList<CrossEventStageWaveTarget> BuildDefaultStageWaveTargets(
        IReadOnlyList<CrossEventScheduleBoardDay> days,
        CrossEventSchedulingStrategy strategy)
    {
        if (strategy == CrossEventSchedulingStrategy.Compact || days.Count == 0)
        {
            return days.Select((day, index) => new CrossEventStageWaveTarget(day.DayLabel, (index + 1d) / days.Count)).ToList();
        }

        var result = new List<CrossEventStageWaveTarget>();
        for (var index = 0; index < days.Count; index++)
        {
            var isLast = index == days.Count - 1;
            double progress;
            if (isLast)
            {
                progress = 1.0;
            }
            else if (strategy == CrossEventSchedulingStrategy.FinalsDayFriendly)
            {
                progress = Math.Clamp(0.45 + (index * 0.25), 0.3, 0.82);
            }
            else
            {
                progress = Math.Clamp(0.55 + (index * 0.28), 0.35, 0.9);
            }

            result.Add(new CrossEventStageWaveTarget(days[index].DayLabel, progress));
        }

        return result;
    }

    private static IReadOnlyList<CrossEventFinalDayRule> BuildDefaultFinalDayRules(
        CrossEventScheduleBoard board,
        CrossEventSchedulingStrategy strategy)
    {
        var eventNames = board.Sources
            .Select(source => source.EventName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var result = new List<CrossEventFinalDayRule>();
        foreach (var eventName in eventNames)
        {
            result.Add(new CrossEventFinalDayRule(
                eventName,
                CrossEventFinalDayMatchCategory.Final,
                strategy == CrossEventSchedulingStrategy.Compact
                    ? CrossEventFinalDayPolicy.PreferFinalDay
                    : CrossEventFinalDayPolicy.MustFinalDay));
            result.Add(new CrossEventFinalDayRule(
                eventName,
                CrossEventFinalDayMatchCategory.Semifinal,
                strategy == CrossEventSchedulingStrategy.FinalsDayFriendly
                    ? CrossEventFinalDayPolicy.PreferFinalDay
                    : CrossEventFinalDayPolicy.Flexible));
            result.Add(new CrossEventFinalDayRule(
                eventName,
                CrossEventFinalDayMatchCategory.Bronze,
                strategy == CrossEventSchedulingStrategy.Compact
                    ? CrossEventFinalDayPolicy.Flexible
                    : CrossEventFinalDayPolicy.MustFinalDay));
            result.Add(new CrossEventFinalDayRule(
                eventName,
                CrossEventFinalDayMatchCategory.Placement5To8,
                strategy == CrossEventSchedulingStrategy.Compact
                    ? CrossEventFinalDayPolicy.Flexible
                    : CrossEventFinalDayPolicy.PreferFinalDay));
        }

        return result;
    }


    private static IReadOnlyList<GlobalScheduleEntry> BuildGlobalScheduleEntries(CrossEventScheduleBoard board)
    {
        var entries = board.Sources
            .SelectMany(source => source.Matches.Select(match => new GlobalScheduleEntry(
                source,
                match,
                BuildItemKey(source.SourceId, match.MatchName),
                NormalizePlayerKeys(match.SideAPlayerIdentities.Concat(match.SideBPlayerIdentities)))))
            .ToList();
        var matchIdLookup = entries
            .GroupBy(entry => BuildSourceMatchIdKey(entry.Source.SourceId, entry.Match.MatchId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            foreach (var dependency in entry.Match.Dependencies)
            {
                var dependencyKey = BuildSourceMatchIdKey(entry.Source.SourceId, dependency.SourceMatchId);
                if (!matchIdLookup.TryGetValue(dependencyKey, out var sourceEntry))
                {
                    continue;
                }

                entry.DependencyKeys.Add(sourceEntry.Key);
                sourceEntry.DependentKeys.Add(entry.Key);
            }
        }

        foreach (var entry in entries)
        {
            entry.DependencyKeys.Sort(StringComparer.Ordinal);
            entry.DependentKeys.Sort(StringComparer.Ordinal);
            entry.PlayerPaths = ResolveGlobalPlayerPaths(entry, matchIdLookup, []);
            entry.PlayerPathsByKey = entry.PlayerPaths
                .GroupBy(path => path.PlayerKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<GlobalPlayerPath>)group.ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }

        CalculatePlayerConflictDegrees(entries);

        return entries;
    }

    private static void CalculatePlayerConflictDegrees(IReadOnlyList<GlobalScheduleEntry> entries)
    {
        var conflictingEntryKeys = entries.ToDictionary(
            entry => entry.Key,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var playerGroup in entries
                     .SelectMany(entry => entry.PlayerPaths.Select(path => (Entry: entry, Path: path)))
                     .GroupBy(item => item.Path.PlayerKey, StringComparer.OrdinalIgnoreCase))
        {
            var appearances = playerGroup.ToList();
            for (var firstIndex = 0; firstIndex < appearances.Count; firstIndex++)
            {
                var first = appearances[firstIndex];
                for (var secondIndex = firstIndex + 1; secondIndex < appearances.Count; secondIndex++)
                {
                    var second = appearances[secondIndex];
                    if (string.Equals(first.Entry.Key, second.Entry.Key, StringComparison.Ordinal)
                        || !AreGlobalOutcomeConditionsCompatible(first.Path.Conditions, second.Path.Conditions))
                    {
                        continue;
                    }

                    conflictingEntryKeys[first.Entry.Key].Add(second.Entry.Key);
                    conflictingEntryKeys[second.Entry.Key].Add(first.Entry.Key);
                }
            }
        }

        foreach (var entry in entries)
        {
            entry.PlayerConflictDegree = conflictingEntryKeys[entry.Key].Count;
        }
    }

    private static IReadOnlyList<GlobalPlayerPath> ResolveGlobalPlayerPaths(
        GlobalScheduleEntry entry,
        IReadOnlyDictionary<string, GlobalScheduleEntry> matchIdLookup,
        HashSet<string> visiting)
    {
        if (!visiting.Add(entry.Key))
        {
            return [];
        }

        var paths = ResolveGlobalSidePlayerPaths(entry, ScheduleMatchSide.SideA, matchIdLookup, visiting)
            .Concat(ResolveGlobalSidePlayerPaths(entry, ScheduleMatchSide.SideB, matchIdLookup, visiting))
            .GroupBy(BuildGlobalPlayerPathKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        visiting.Remove(entry.Key);
        return paths;
    }

    private static IReadOnlyList<GlobalPlayerPath> ResolveGlobalSidePlayerPaths(
        GlobalScheduleEntry entry,
        ScheduleMatchSide side,
        IReadOnlyDictionary<string, GlobalScheduleEntry> matchIdLookup,
        HashSet<string> visiting)
    {
        var confirmedIdentities = side == ScheduleMatchSide.SideA
            ? entry.Match.SideAPlayerIdentities
            : entry.Match.SideBPlayerIdentities;
        if (confirmedIdentities.Count > 0)
        {
            return CreateGlobalPlayerPaths(confirmedIdentities);
        }

        var dependencies = entry.Match.Dependencies
            .Where(dependency => dependency.TargetSide == side)
            .ToList();
        if (dependencies.Count > 0)
        {
            return dependencies
                .SelectMany(dependency =>
                {
                    var dependencyKey = BuildSourceMatchIdKey(entry.Source.SourceId, dependency.SourceMatchId);
                    if (!matchIdLookup.TryGetValue(dependencyKey, out var sourceEntry))
                    {
                        return [];
                    }

                    return ResolveGlobalPlayerPaths(sourceEntry, matchIdLookup, visiting)
                        .Select(path => TryAddGlobalOutcomeCondition(path, dependencyKey, dependency.Outcome))
                        .Where(path => path is not null)
                        .Select(path => path!);
                })
                .GroupBy(BuildGlobalPlayerPathKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }

        var possibleIdentities = side == ScheduleMatchSide.SideA
            ? entry.Match.SideAPossiblePlayerIdentities
            : entry.Match.SideBPossiblePlayerIdentities;
        return CreateGlobalPlayerPaths(possibleIdentities);
    }

    private static IReadOnlyList<GlobalPlayerPath> CreateGlobalPlayerPaths(
        IEnumerable<CrossEventPlayerIdentity> identities)
    {
        return identities
            .Select(identity => identity.IdentityKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key => new GlobalPlayerPath(key, []))
            .ToList();
    }

    private static GlobalPlayerPath? TryAddGlobalOutcomeCondition(
        GlobalPlayerPath path,
        string matchKey,
        ScheduleMatchDependencyOutcome outcome)
    {
        var existing = path.Conditions.FirstOrDefault(condition =>
            string.Equals(condition.MatchKey, matchKey, StringComparison.Ordinal));
        if (existing is not null && existing.Outcome != outcome)
        {
            return null;
        }

        if (existing is not null)
        {
            return path;
        }

        return path with
        {
            Conditions = path.Conditions
                .Append(new GlobalOutcomeCondition(matchKey, outcome))
                .OrderBy(condition => condition.MatchKey, StringComparer.Ordinal)
                .ThenBy(condition => condition.Outcome)
                .ToList()
        };
    }

    private static string BuildGlobalPlayerPathKey(GlobalPlayerPath path)
    {
        var conditions = string.Join(",", path.Conditions.Select(condition => $"{condition.MatchKey}:{(int)condition.Outcome}"));
        return $"{path.PlayerKey}|{conditions}";
    }

    private static IReadOnlyList<GlobalScheduleEntry> BuildGlobalScheduleOrder(IReadOnlyList<GlobalScheduleEntry> entries)
    {
        var pending = entries
            .Where(entry => !entry.Match.IsCompleted)
            .ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var indegrees = pending.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.DependencyKeys.Count(pending.ContainsKey),
            StringComparer.Ordinal);
        var result = new List<GlobalScheduleEntry>();

        while (indegrees.Count > 0)
        {
            var next = indegrees
                .Where(pair => pair.Value == 0)
                .Select(pair => pending[pair.Key])
                .OrderByDescending(entry => entry.PlayerConflictDegree)
                .ThenBy(entry => entry.Match.DayLabel, StringComparer.Ordinal)
                .ThenBy(entry => entry.Match.StartTime)
                .ThenBy(entry => IsImportantMatch(entry) ? 0 : 1)
                .ThenBy(entry => entry.Source.EventName, StringComparer.Ordinal)
                .ThenBy(entry => entry.Match.Order)
                .FirstOrDefault();
            if (next is null)
            {
                break;
            }

            result.Add(next);
            pending.Remove(next.Key);
            indegrees.Remove(next.Key);
            foreach (var dependentKey in next.DependentKeys)
            {
                if (indegrees.TryGetValue(dependentKey, out var indegree))
                {
                    indegrees[dependentKey] = Math.Max(0, indegree - 1);
                }
            }
        }

        if (pending.Count > 0)
        {
            result.AddRange(pending.Values
                .OrderBy(entry => entry.Match.DayLabel, StringComparer.Ordinal)
                .ThenBy(entry => entry.Match.StartTime)
                .ThenBy(entry => IsImportantMatch(entry) ? 0 : 1)
                .ThenBy(entry => entry.Source.EventName, StringComparer.Ordinal)
                .ThenBy(entry => entry.Match.Order));
        }

        return result;
    }

    private static GlobalSchedulingContext BuildSchedulingContext(
        CrossEventScheduleBoard board,
        IReadOnlyList<GlobalScheduleEntry> entries,
        CrossEventSchedulingOptions options)
    {
        var orderedDays = board.Days.OrderBy(day => day.DayLabel, StringComparer.Ordinal).ToList();
        var dayIndex = orderedDays
            .Select((day, index) => (day.DayLabel, Index: index))
            .ToDictionary(pair => pair.DayLabel, pair => pair.Index, StringComparer.Ordinal);
        var dayCapacity = orderedDays.ToDictionary(
            day => day.DayLabel,
            day => CalculateDayCapacityMinutes(day, options.RefereeCount),
            StringComparer.Ordinal);
        var loadTargets = BuildResolvedDayLoadTargets(orderedDays, dayCapacity, entries, options);
        var waveTargets = BuildResolvedStageWaveTargets(orderedDays, options);
        var finalRules = options.FinalDayRules
            .GroupBy(rule => new FinalDayRuleKey(rule.EventName, rule.Category))
            .ToDictionary(group => group.Key, group => group.Last().Policy);
        var compact = options.Strategy == CrossEventSchedulingStrategy.Compact;

        return new GlobalSchedulingContext(
            options,
            orderedDays.LastOrDefault()?.DayLabel ?? "",
            dayIndex,
            dayCapacity,
            loadTargets,
            waveTargets,
            finalRules,
            OriginalPositionWeight: compact ? 10 : 4,
            CrossDayMoveWeight: compact ? 10_000 : 2_500,
            TargetLoadWeight: compact ? 0.15 : 1.8,
            WarningLoadWeight: compact ? 1.0 : 8.0,
            EarlyStageWavePenalty: compact ? 0 : 45_000,
            LateStageWavePenalty: compact ? 0 : 9_000);
    }

    private static IReadOnlyDictionary<string, CrossEventDayLoadTarget> BuildResolvedDayLoadTargets(
        IReadOnlyList<CrossEventScheduleBoardDay> days,
        IReadOnlyDictionary<string, int> capacityByDay,
        IReadOnlyList<GlobalScheduleEntry> entries,
        CrossEventSchedulingOptions options)
    {
        var existing = options.DayLoadTargets
            .GroupBy(target => target.DayLabel, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        if (existing.Count == days.Count)
        {
            return existing;
        }

        var generated = BuildDefaultDayLoadTargets(
            days,
            capacityByDay,
            Math.Max(1, entries.Sum(entry => entry.Match.DurationMinutes)),
            options.Strategy);
        foreach (var target in generated)
        {
            existing.TryAdd(target.DayLabel, target);
        }

        return existing;
    }

    private static IReadOnlyList<CrossEventStageWaveTarget> BuildResolvedStageWaveTargets(
        IReadOnlyList<CrossEventScheduleBoardDay> days,
        CrossEventSchedulingOptions options)
    {
        if (options.StageWaveTargets.Count == days.Count)
        {
            var order = days
                .Select((day, index) => (day.DayLabel, Index: index))
                .ToDictionary(pair => pair.DayLabel, pair => pair.Index, StringComparer.Ordinal);
            return options.StageWaveTargets
                .OrderBy(target => order.TryGetValue(target.DayLabel, out var index) ? index : int.MaxValue)
                .ToList();
        }

        var generated = BuildDefaultStageWaveTargets(days, options.Strategy);
        var existing = options.StageWaveTargets
            .GroupBy(target => target.DayLabel, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        return generated
            .Select(target => existing.TryGetValue(target.DayLabel, out var overrideTarget) ? overrideTarget : target)
            .ToList();
    }

    private static int CalculateDayCapacityMinutes(CrossEventScheduleBoardDay day, int? refereeCount)
    {
        return ScheduleResourceCalculator.CalculateDayCapacityMinutes(day, refereeCount, day.SlotMinutes);
    }

    private static ScheduleQualityReport BuildCrossEventQualityReport(
        CrossEventScheduleBoard board,
        int movedCount,
        IReadOnlyList<string> messages)
    {
        var options = board.SchedulingOptions;
        var strategyName = GetCrossEventStrategyName(options?.Strategy ?? CrossEventSchedulingStrategy.BalancedRelaxed);
        var hardConstraintCount = board.Report.SevereCount + board.Report.WarningCount;
        var softScore = hardConstraintCount * 100_000;
        var insights = new List<ScheduleQualityInsight>
        {
            new(
                "硬约束",
                hardConstraintCount == 0
                    ? "跨项目场地占用、同选手冲突、淘汰树接续和裁判并发已作为硬约束检查。"
                    : $"仍有 {hardConstraintCount} 条阻塞级问题，导出前需要继续调整。",
                hardConstraintCount * 100_000),
            new(
                "策略",
                $"{strategyName}；自动调整移动 {movedCount} 场。")
        };

        foreach (var day in board.Days.OrderBy(day => day.DayLabel, StringComparer.Ordinal))
        {
            var dayItems = board.Items
                .Where(item => string.Equals(item.DayLabel, day.DayLabel, StringComparison.Ordinal))
                .ToList();
            var capacity = CalculateDayCapacityMinutes(day, options?.RefereeCount);
            var minutes = dayItems.Sum(item => item.DurationMinutes);
            var utilization = capacity <= 0 ? 0 : minutes * 100d / capacity;
            var target = options?.DayLoadTargets.FirstOrDefault(item => string.Equals(item.DayLabel, day.DayLabel, StringComparison.Ordinal));
            var targetText = target is null ? "" : $"，目标 {target.TargetUtilization:P0}";
            var overloadPenalty = target is null
                ? 0
                : Math.Max(0, (int)Math.Round((utilization / 100d - target.WarningUtilization) * 1000));
            softScore += overloadPenalty;
            insights.Add(new ScheduleQualityInsight(
                "每日负载",
                $"{day.DayLabel}：{dayItems.Count} 场，约 {utilization:0.#}% 负载{targetText}。",
                overloadPenalty));
        }

        foreach (var message in messages.Take(5))
        {
            insights.Add(new ScheduleQualityInsight("未放置原因", message, 10_000));
            softScore += 10_000;
        }

        return new ScheduleQualityReport(strategyName, hardConstraintCount, softScore, insights);
    }

    private static string GetCrossEventStrategyName(CrossEventSchedulingStrategy strategy)
    {
        return strategy switch
        {
            CrossEventSchedulingStrategy.Compact => "紧凑完成",
            CrossEventSchedulingStrategy.FinalsDayFriendly => "决赛日友好",
            CrossEventSchedulingStrategy.Custom => "自定义",
            _ => "均衡宽松"
        };
    }

    private static bool WouldExceedRefereeCapacity(
        CrossEventScheduleBoard board,
        string itemKey,
        string dayLabel,
        TimeOnly startTime,
        TimeOnly endTime,
        int? refereeCount,
        IReadOnlyDictionary<string, GlobalSchedulePlacement>? placements = null)
    {
        var day = board.Days.FirstOrDefault(candidate => string.Equals(candidate.DayLabel, dayLabel, StringComparison.Ordinal));
        if (day is null)
        {
            return false;
        }

        var concurrentLimit = ScheduleResourceCalculator.GetConcurrentMatchLimit(day, refereeCount, startTime, endTime);
        var overlappingMatches = placements is null
            ? board.Items.Count(item =>
                !string.Equals(item.Key, itemKey, StringComparison.Ordinal)
                && string.Equals(item.DayLabel, dayLabel, StringComparison.Ordinal)
                && TimeRangesOverlap(startTime, endTime, item.StartTime, item.EndTime))
            : placements.Count(pair =>
                !string.Equals(pair.Key, itemKey, StringComparison.Ordinal)
                && string.Equals(pair.Value.DayLabel, dayLabel, StringComparison.Ordinal)
                && TimeRangesOverlap(startTime, endTime, pair.Value.StartTime, pair.Value.EndTime));

        return overlappingMatches >= concurrentLimit;
    }

    private static IReadOnlyList<string> FindPlayerDailyLimitOverages(
        CrossEventScheduleBoard board,
        CrossEventScheduleBoardItem item,
        string dayLabel)
    {
        var players = GetScheduleBoardItemPlayers(item)
            .GroupBy(player => player.IdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (players.Count == 0)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var player in players)
        {
            var count = 1 + board.Items.Count(other =>
                !string.Equals(other.Key, item.Key, StringComparison.Ordinal)
                && string.Equals(other.DayLabel, dayLabel, StringComparison.Ordinal)
                && GetScheduleBoardItemPlayers(other).Any(otherPlayer =>
                    string.Equals(otherPlayer.IdentityKey, player.IdentityKey, StringComparison.OrdinalIgnoreCase)));
            if (count > CrossEventScheduleRules.MaxPlayerMatchesPerDay)
            {
                result.Add($"{player.DisplayName} 当天 {count} 场");
            }
        }

        return result;
    }

    private static bool WouldExceedPlayerDailyMatchLimit(
        GlobalScheduleEntry entry,
        string dayLabel,
        IReadOnlyDictionary<string, GlobalSchedulePlacement> placements,
        IReadOnlyDictionary<string, GlobalScheduleEntry> entryLookup)
    {
        if (entry.PlayerKeys.Count == 0)
        {
            return false;
        }

        foreach (var playerKey in entry.PlayerKeys)
        {
            var count = 1 + placements.Count(pair =>
                string.Equals(pair.Value.DayLabel, dayLabel, StringComparison.Ordinal)
                && entryLookup.TryGetValue(pair.Key, out var existingEntry)
                && existingEntry.PlayerKeys.Contains(playerKey, StringComparer.OrdinalIgnoreCase));
            if (count > CrossEventScheduleRules.MaxPlayerMatchesPerDay)
            {
                return true;
            }
        }

        return false;
    }

    private static int FindDesiredStageDayIndex(GlobalScheduleEntry entry, GlobalSchedulingContext context)
    {
        var progress = EstimateStageProgress(entry);
        for (var index = 0; index < context.StageWaveTargets.Count; index++)
        {
            if (progress <= context.StageWaveTargets[index].CumulativeProgress)
            {
                return index;
            }
        }

        return Math.Max(0, context.StageWaveTargets.Count - 1);
    }

    private static double EstimateStageProgress(GlobalScheduleEntry entry)
    {
        var text = $"{entry.Match.Phase} {entry.Match.MatchName}";
        if (IsFinalMatchText(text) || IsBronzeMatchText(text))
        {
            return 1.0;
        }

        if (IsPlacement5To8MatchText(text))
        {
            return 0.88;
        }

        if (IsSemifinalMatchText(text) || text.Contains("4进2", StringComparison.Ordinal))
        {
            return 0.82;
        }

        if (text.Contains("8进4", StringComparison.Ordinal))
        {
            return 0.72;
        }

        if (text.Contains("16进8", StringComparison.Ordinal))
        {
            return 0.65;
        }

        if (text.Contains("32进16", StringComparison.Ordinal))
        {
            return 0.48;
        }

        if (text.Contains("64进32", StringComparison.Ordinal))
        {
            return 0.32;
        }

        if (text.Contains("128进64", StringComparison.Ordinal) || text.Contains("首轮", StringComparison.Ordinal))
        {
            return 0.16;
        }

        return 0.5;
    }

    private static CrossEventFinalDayMatchCategory? ClassifyFinalDayCategory(GlobalScheduleEntry entry)
    {
        var text = $"{entry.Match.Phase} {entry.Match.MatchName}";
        if (IsBronzeMatchText(text))
        {
            return CrossEventFinalDayMatchCategory.Bronze;
        }

        if (IsPlacement5To8MatchText(text))
        {
            return CrossEventFinalDayMatchCategory.Placement5To8;
        }

        if (IsSemifinalMatchText(text))
        {
            return CrossEventFinalDayMatchCategory.Semifinal;
        }

        if (IsFinalMatchText(text))
        {
            return CrossEventFinalDayMatchCategory.Final;
        }

        return null;
    }

    private static bool IsFinalMatchText(string text)
    {
        return text.Contains("决赛", StringComparison.Ordinal)
            && !IsSemifinalMatchText(text)
            && !text.Contains("5-8", StringComparison.Ordinal)
            && !text.Contains("5–8", StringComparison.Ordinal)
            && !text.Contains("3/4", StringComparison.Ordinal)
            && !text.Contains("3-4", StringComparison.Ordinal)
            && !text.Contains("三四", StringComparison.Ordinal)
            && !text.Contains("铜牌", StringComparison.Ordinal);
    }

    private static bool IsSemifinalMatchText(string text)
    {
        return text.Contains("半决赛", StringComparison.Ordinal)
            || text.Contains("4进2", StringComparison.Ordinal);
    }

    private static bool IsBronzeMatchText(string text)
    {
        return text.Contains("3/4", StringComparison.Ordinal)
            || text.Contains("3-4", StringComparison.Ordinal)
            || text.Contains("三四", StringComparison.Ordinal)
            || text.Contains("铜牌", StringComparison.Ordinal);
    }

    private static bool IsPlacement5To8MatchText(string text)
    {
        return text.Contains("5-8", StringComparison.Ordinal)
            || text.Contains("5–8", StringComparison.Ordinal)
            || text.Contains("5/8", StringComparison.Ordinal)
            || text.Contains("五至八", StringComparison.Ordinal)
            || text.Contains("名次", StringComparison.Ordinal);
    }

    private static GlobalSchedulePlacement? FindBestGlobalPlacement(
        CrossEventScheduleBoard board,
        GlobalScheduleEntry entry,
        IReadOnlyDictionary<string, GlobalSchedulePlacement> placements,
        IReadOnlyDictionary<string, GlobalScheduleEntry> entryLookup,
        IReadOnlyDictionary<string, int> dayNumbers,
        GlobalSchedulingContext schedulingContext,
        IReadOnlyDictionary<string, int> placedMinutesByDay)
    {
        GlobalSchedulePlacement? bestPlacement = null;
        long? bestScore = null;
        foreach (var day in board.Days.OrderBy(day => day.DayLabel, StringComparer.Ordinal))
        {
            foreach (var slot in day.TimeSlots)
            {
                var endTime = slot.AddMinutes(entry.Match.DurationMinutes);
                if (endTime > day.EndTime)
                {
                    continue;
                }

                foreach (var court in day.Courts)
                {
                    var placement = new GlobalSchedulePlacement(day.DayLabel, slot, endTime, court);
                    // Validity is the hard gate. The score below only chooses among placements that
                    // already satisfy resources, dependency order, player overlap and rest limits.
                    if (!IsGlobalPlacementValid(board, entry, placement, placements, entryLookup, dayNumbers, schedulingContext.Options.RefereeCount))
                    {
                        continue;
                    }

                    var score = ScoreGlobalPlacement(entry, placement, dayNumbers, schedulingContext, placedMinutesByDay);
                    if (!bestScore.HasValue || score < bestScore.Value)
                    {
                        bestScore = score;
                        bestPlacement = placement;
                    }
                }
            }
        }

        return bestPlacement;
    }

    private static bool IsGlobalPlacementValid(
        CrossEventScheduleBoard board,
        GlobalScheduleEntry entry,
        GlobalSchedulePlacement placement,
        IReadOnlyDictionary<string, GlobalSchedulePlacement> placements,
        IReadOnlyDictionary<string, GlobalScheduleEntry> entryLookup,
        IReadOnlyDictionary<string, int> dayNumbers,
        int? refereeCount)
    {
        var day = board.Days.FirstOrDefault(candidate => string.Equals(candidate.DayLabel, placement.DayLabel, StringComparison.Ordinal));
        if (day is null || !ScheduleResourceCalculator.IsCourtAvailable(day, placement.Court, placement.StartTime, placement.EndTime))
        {
            return false;
        }

        if (WouldExceedRefereeCapacity(board, entry.Key, placement.DayLabel, placement.StartTime, placement.EndTime, refereeCount, placements))
        {
            return false;
        }

        if (WouldExceedPlayerDailyMatchLimit(entry, placement.DayLabel, placements, entryLookup))
        {
            return false;
        }

        var startMinute = BuildComparableMinute(placement.DayLabel, placement.StartTime, dayNumbers);
        var endMinute = BuildComparableMinute(placement.DayLabel, placement.EndTime, dayNumbers);
        foreach (var dependencyKey in entry.DependencyKeys)
        {
            if (placements.TryGetValue(dependencyKey, out var dependency)
                && startMinute < BuildComparableMinute(dependency.DayLabel, dependency.EndTime, dayNumbers) + board.MinimumRestMinutes)
            {
                return false;
            }
        }

        foreach (var dependentKey in entry.DependentKeys)
        {
            if (placements.TryGetValue(dependentKey, out var dependent)
                && BuildComparableMinute(dependent.DayLabel, dependent.StartTime, dayNumbers) < endMinute + board.MinimumRestMinutes)
            {
                return false;
            }
        }

        foreach (var existingPair in placements)
        {
            var existing = existingPair.Value;
            if (string.Equals(existing.DayLabel, placement.DayLabel, StringComparison.Ordinal)
                && string.Equals(existing.Court, placement.Court, StringComparison.OrdinalIgnoreCase)
                && TimeRangesOverlap(placement.StartTime, placement.EndTime, existing.StartTime, existing.EndTime))
            {
                return false;
            }

            if (!entryLookup.TryGetValue(existingPair.Key, out var existingEntry)
                || !SharesPlayer(entry, existingEntry))
            {
                continue;
            }

            var existingStart = BuildComparableMinute(existing.DayLabel, existing.StartTime, dayNumbers);
            var existingEnd = BuildComparableMinute(existing.DayLabel, existing.EndTime, dayNumbers);
            if (!HasMinimumRest(startMinute, endMinute, existingStart, existingEnd, board.MinimumRestMinutes))
            {
                return false;
            }
        }

        return true;
    }

    private static long ScoreGlobalPlacement(
        GlobalScheduleEntry entry,
        GlobalSchedulePlacement placement,
        IReadOnlyDictionary<string, int> dayNumbers,
        GlobalSchedulingContext schedulingContext,
        IReadOnlyDictionary<string, int> placedMinutesByDay)
    {
        var originalStart = BuildComparableMinute(entry.Match.DayLabel, entry.Match.StartTime, dayNumbers);
        var candidateStart = BuildComparableMinute(placement.DayLabel, placement.StartTime, dayNumbers);
        var originalDay = dayNumbers.TryGetValue(entry.Match.DayLabel, out var sourceDay) ? sourceDay : 0;
        var candidateDay = dayNumbers.TryGetValue(placement.DayLabel, out var targetDay) ? targetDay : originalDay;
        var score = (long)Math.Abs(candidateStart - originalStart) * schedulingContext.OriginalPositionWeight;
        score += Math.Abs(candidateDay - originalDay) * schedulingContext.CrossDayMoveWeight;
        if (!string.Equals(entry.Match.Court, placement.Court, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        score += ScoreDayLoad(entry, placement, schedulingContext, placedMinutesByDay);
        score += ScoreStageWave(entry, placement, schedulingContext);
        score += ScoreFinalDayPreference(entry, placement, schedulingContext);

        if (IsImportantMatch(entry))
        {
            if (candidateStart < originalStart)
            {
                score += 1_000;
            }
            else
            {
                score -= Math.Min(500, (candidateStart - originalStart) / 2);
            }
        }

        return score;
    }

    private static long ScoreDayLoad(
        GlobalScheduleEntry entry,
        GlobalSchedulePlacement placement,
        GlobalSchedulingContext context,
        IReadOnlyDictionary<string, int> placedMinutesByDay)
    {
        if (!context.DayLoadTargets.TryGetValue(placement.DayLabel, out var target)
            || !context.DayCapacityMinutes.TryGetValue(placement.DayLabel, out var capacity)
            || capacity <= 0)
        {
            return 0;
        }

        placedMinutesByDay.TryGetValue(placement.DayLabel, out var placedMinutes);
        var usageAfter = placedMinutes + entry.Match.DurationMinutes;
        var targetMinutes = capacity * target.TargetUtilization;
        var warningMinutes = capacity * target.WarningUtilization;
        var overTarget = Math.Max(0, usageAfter - targetMinutes);
        var overWarning = Math.Max(0, usageAfter - warningMinutes);
        var score = (long)Math.Round(overTarget * overTarget * context.TargetLoadWeight);
        score += (long)Math.Round(overWarning * overWarning * context.WarningLoadWeight);

        var dayIndex = context.DayIndex.TryGetValue(placement.DayLabel, out var index) ? index : 0;
        if (context.Options.Strategy == CrossEventSchedulingStrategy.Compact)
        {
            score += dayIndex * 50L;
        }

        return score;
    }

    private static long ScoreStageWave(
        GlobalScheduleEntry entry,
        GlobalSchedulePlacement placement,
        GlobalSchedulingContext context)
    {
        if (!context.Options.SynchronizeStageWaves || context.StageWaveTargets.Count == 0)
        {
            return 0;
        }

        var desiredDayIndex = FindDesiredStageDayIndex(entry, context);
        var candidateDayIndex = context.DayIndex.TryGetValue(placement.DayLabel, out var index) ? index : desiredDayIndex;
        if (candidateDayIndex < desiredDayIndex)
        {
            return (desiredDayIndex - candidateDayIndex) * context.EarlyStageWavePenalty;
        }

        if (candidateDayIndex > desiredDayIndex)
        {
            return (candidateDayIndex - desiredDayIndex) * context.LateStageWavePenalty;
        }

        return 0;
    }

    private static long ScoreFinalDayPreference(
        GlobalScheduleEntry entry,
        GlobalSchedulePlacement placement,
        GlobalSchedulingContext context)
    {
        var category = ClassifyFinalDayCategory(entry);
        if (category is null)
        {
            return 0;
        }

        var key = new FinalDayRuleKey(entry.Source.EventName, category.Value);
        if (!context.FinalDayRules.TryGetValue(key, out var policy))
        {
            return 0;
        }

        var isFinalDay = string.Equals(placement.DayLabel, context.FinalDayLabel, StringComparison.Ordinal);
        return policy switch
        {
            CrossEventFinalDayPolicy.MustFinalDay => isFinalDay ? -50_000 : 700_000,
            CrossEventFinalDayPolicy.PreferFinalDay => isFinalDay ? -30_000 : 90_000,
            CrossEventFinalDayPolicy.AvoidFinalDay => isFinalDay ? 80_000 : -5_000,
            _ => 0
        };
    }

    private static IReadOnlyList<CrossEventScheduleSource> ApplyGlobalPlacements(
        IReadOnlyList<CrossEventScheduleSource> sources,
        IReadOnlyDictionary<string, GlobalSchedulePlacement> placements)
    {
        return sources
            .Select(source =>
            {
                var matches = source.Matches
                    .Select(match =>
                    {
                        var key = BuildItemKey(source.SourceId, match.MatchName);
                        if (!placements.TryGetValue(key, out var placement))
                        {
                            return match;
                        }

                        return match with
                        {
                            DayLabel = placement.DayLabel,
                            StartTime = placement.StartTime,
                            EndTime = placement.EndTime,
                            Court = placement.Court
                        };
                    })
                    .ToList();
                return source with { Matches = NormalizeMatchOrders(matches) };
            })
            .ToList();
    }


    private static bool IsImportantMatch(GlobalScheduleEntry entry)
    {
        var text = $"{entry.Match.Phase} {entry.Match.MatchName}";
        return text.Contains("决赛", StringComparison.Ordinal)
            || text.Contains("半决赛", StringComparison.Ordinal)
            || text.Contains("名次", StringComparison.Ordinal)
            || text.Contains("3-8", StringComparison.Ordinal)
            || text.Contains("5-8", StringComparison.Ordinal)
            || text.Contains("8进4", StringComparison.Ordinal)
            || text.Contains("4进2", StringComparison.Ordinal);
    }

    private static BoardDayBuilder GetDayBuilder(IDictionary<string, BoardDayBuilder> builders, string dayLabel)
    {
        if (!builders.TryGetValue(dayLabel, out var builder))
        {
            builder = new BoardDayBuilder();
            builders.Add(dayLabel, builder);
        }

        return builder;
    }
}
