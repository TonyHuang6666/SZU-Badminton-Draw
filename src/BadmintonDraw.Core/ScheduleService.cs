using static BadmintonDraw.Core.MatchTopologyBuilder;

namespace BadmintonDraw.Core;

public sealed class ScheduleService
{
    public SchedulePlan Generate(DrawResult result, ScheduleSettings settings)
    {
        Validate(settings);

        var unscheduled = MatchTopologyBuilder.Build(result);
        if (unscheduled.Count == 0)
        {
            throw new DrawValidationException("当前抽签结果没有可编排的比赛场次。");
        }

        return AssignTimeAndCourts(unscheduled, settings);
    }

    public ScheduleSchedulingOptions CreateSchedulingOptions(
        DrawResult result,
        ScheduleSettings settings,
        ScheduleAutoSchedulingStrategy strategy)
    {
        Validate(settings);

        var matches = MatchTopologyBuilder.Build(result);
        var orderedDays = settings.Days.OrderBy(day => day.Date).ToList();
        var capacities = orderedDays.ToDictionary(
            day => day.DayLabel,
            day => (double)CalculateDayCapacityMinutes(day, settings.RefereeCount),
            StringComparer.Ordinal);
        var totalMinutes = matches.Sum(match => ResolveTiming(match, settings).MatchMinutes);
        var dayLoadTargets = BuildDefaultDayLoadTargets(orderedDays, capacities, totalMinutes, strategy);
        var stageWaveTargets = BuildDefaultStageWaveTargets(orderedDays, strategy);

        return new ScheduleSchedulingOptions(
            strategy,
            dayLoadTargets,
            SynchronizeStageWaves: strategy is ScheduleAutoSchedulingStrategy.BalancedRelaxed
                or ScheduleAutoSchedulingStrategy.FinalsDayFriendly
                or ScheduleAutoSchedulingStrategy.Custom,
            stageWaveTargets);
    }

    private static SchedulePlan AssignTimeAndCourts(
        IReadOnlyList<UnscheduledMatch> matches,
        ScheduleSettings settings)
    {
        var remaining = matches.ToDictionary(match => match.Id);
        var scheduledById = new Dictionary<int, ScheduledAssignment>();
        var scheduled = new List<ScheduledMatch>(matches.Count);
        var dayLoadTargets = BuildDayLoadTargets(matches, settings);
        var stageWaveTargets = BuildStageWaveTargets(settings);
        var scheduledMinutesByDay = settings.Days.ToDictionary(day => day.DayLabel, _ => 0, StringComparer.Ordinal);
        var order = 1;

        foreach (var day in settings.Days.OrderBy(day => day.Date))
        {
            var dailyAssignments = new List<ScheduledAssignment>();
            var courtAvailableAt = day.Courts.ToDictionary(court => court, _ => day.DayStart, StringComparer.Ordinal);

            while (remaining.Count > 0)
            {
                var availableCourtTimes = courtAvailableAt.Values
                    .Where(time => (day.DayEnd - time).TotalMinutes >= settings.MinimumMatchMinutes)
                    .ToList();
                if (availableCourtTimes.Count == 0)
                {
                    break;
                }

                var currentStart = availableCourtTimes.Min();
                var currentCourts = day.Courts
                    .Where(court => courtAvailableAt[court] == currentStart)
                    .ToList();
                var idleCourts = new List<string>();

                foreach (var court in currentCourts)
                {
                    // IsEligible is the hard-constraint gate. Strategy ordering below only ranks
                    // already legal candidates and must never make an ineligible match schedulable.
                    var candidate = remaining.Values
                        .Select(match => new CandidateMatch(match, ResolveTiming(match, settings)))
                        .Where(candidate => IsEligible(
                            candidate.Match,
                            candidate.Timing,
                            day,
                            currentStart,
                            court,
                            remaining.Values,
                            scheduledById,
                            dailyAssignments,
                            settings)
                            && IsWithinDayLoadTarget(
                                day,
                                candidate.Timing,
                                scheduledMinutesByDay,
                                dayLoadTargets,
                                settings.MaximumMatchMinutes,
                                settings.RefereeCount))
                        .OrderBy(candidate => GetStageWaveSortKey(candidate.Match, day, stageWaveTargets, settings))
                        .ThenBy(candidate => GetSchedulingStageRank(candidate.Match))
                        .ThenBy(candidate => GetRestSortKey(candidate.Match, currentStart, dailyAssignments))
                        .ThenBy(candidate => candidate.Match.Id)
                        .FirstOrDefault();
                    if (candidate is null)
                    {
                        idleCourts.Add(court);
                        continue;
                    }

                    var slotEnd = currentStart.AddMinutes(candidate.Timing.MatchMinutes);
                    scheduled.Add(new ScheduledMatch(
                        order++,
                        day.DayLabel,
                        currentStart,
                        slotEnd,
                        court,
                        candidate.Match.GroupNumber,
                        candidate.Match.GroupName,
                        candidate.Match.Phase,
                        candidate.Match.MatchName,
                        candidate.Match.SideA,
                        candidate.Match.SideB,
                        candidate.Match.Note,
                        candidate.Match.SameUnit,
                        candidate.Match.MatchId,
                        candidate.Match.Dependencies,
                        ToPlayerIdentities(candidate.Match.SideAEntrantPaths),
                        ToPlayerIdentities(candidate.Match.SideBEntrantPaths)));

                    var assignment = new ScheduledAssignment(
                        candidate.Match.Id,
                        day.Date,
                        currentStart,
                        slotEnd,
                        candidate.Match.EntrantPaths,
                        candidate.Timing.Bucket);
                    scheduledById[candidate.Match.Id] = assignment;
                    dailyAssignments.Add(assignment);
                    scheduledMinutesByDay[day.DayLabel] = scheduledMinutesByDay.GetValueOrDefault(day.DayLabel) + candidate.Timing.MatchMinutes;
                    remaining.Remove(candidate.Match.Id);
                    courtAvailableAt[court] = slotEnd;
                }

                foreach (var court in idleCourts)
                {
                    courtAvailableAt[court] = FindNextWakeTime(currentStart, day, court, courtAvailableAt, dailyAssignments);
                }

                if (remaining.Count == 0)
                {
                    return BuildFinalSchedulePlan(scheduled, settings);
                }
            }
        }

        var incompletePlan = new SchedulePlan(
            scheduled,
            settings,
            BuildUnscheduledPreviews(remaining.Values, scheduledById.Keys.ToHashSet(), scheduled.Count));
        return incompletePlan with { QualityReport = BuildScheduleQualityReport(incompletePlan, spreadApplied: false) };
    }

    private static SchedulePlan BuildFinalSchedulePlan(
        IReadOnlyList<ScheduledMatch> scheduled,
        ScheduleSettings settings)
    {
        var plan = new SchedulePlan(scheduled, settings);
        if (settings.AutoSchedulingStrategy == ScheduleAutoSchedulingStrategy.Compact)
        {
            return plan with { QualityReport = BuildScheduleQualityReport(plan, spreadApplied: false) };
        }

        // Spreading is a reversible post-process over a complete legal plan. Any dependency or
        // resource failure keeps the original compact plan as the stable fallback.
        var finalPlan = TrySpreadMatchesWithinDays(plan, out var spreadPlan)
            ? spreadPlan
            : plan;
        return finalPlan with { QualityReport = BuildScheduleQualityReport(finalPlan, !ReferenceEquals(finalPlan, plan)) };
    }

    public static ScheduleQualityReport EvaluateScheduleQuality(
        SchedulePlan plan,
        bool spreadApplied = false)
    {
        return BuildScheduleQualityReport(plan, spreadApplied);
    }

    private static ScheduleQualityReport BuildScheduleQualityReport(
        SchedulePlan plan,
        bool spreadApplied)
    {
        var insights = new List<ScheduleQualityInsight>();
        var orderViolations = ScheduleDependencyGraph.Build(plan).FindOrderViolations().Count;
        var unscheduledCount = plan.UnscheduledMatches.Count;
        var hardViolations = orderViolations + unscheduledCount;
        insights.Add(new ScheduleQualityInsight(
            "硬约束",
            hardViolations == 0
                ? "淘汰树依赖、场地占用和裁判并发已在排程过程中作为硬约束处理。"
                : $"仍有 {orderViolations} 条淘汰树依赖顺序问题、{unscheduledCount} 场未安排，需要增加资源或人工处理。",
            hardViolations * 100_000));
        insights.Add(new ScheduleQualityInsight(
            "策略",
            $"{GetScheduleStrategyName(plan.Settings.AutoSchedulingStrategy)}；{(spreadApplied ? "已按并发波次整体插入空档。" : "保持紧凑或未能进一步分散。")}"));

        var softScore = 0;
        foreach (var day in plan.Settings.Days.OrderBy(day => day.Date))
        {
            var dayMatches = plan.Matches
                .Where(match => string.Equals(match.DayLabel, day.DayLabel, StringComparison.Ordinal))
                .ToList();
            var capacity = CalculateDayCapacityMinutes(day, plan.Settings.RefereeCount);
            var minutes = dayMatches.Sum(match => match.DurationMinutes);
            var utilization = capacity <= 0 ? 0 : minutes * 100d / capacity;
            var waveCount = dayMatches
                .GroupBy(match => match.StartTime)
                .Count();
            var gapPenalty = CalculateWaveGapPenalty(dayMatches);
            softScore += gapPenalty;
            insights.Add(new ScheduleQualityInsight(
                "每日负载",
                $"{day.DayLabel}：{dayMatches.Count} 场，约 {utilization:0.#}% 负载，{waveCount} 个开赛波次。",
                gapPenalty));
        }

        return new ScheduleQualityReport(
            GetScheduleStrategyName(plan.Settings.AutoSchedulingStrategy),
            hardViolations,
            softScore + hardViolations * 100_000,
            insights);
    }

    private static int CalculateWaveGapPenalty(IReadOnlyList<ScheduledMatch> dayMatches)
    {
        var waves = dayMatches
            .GroupBy(match => match.StartTime)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                Start = group.Key,
                End = group.Max(match => match.EndTime),
                Duration = group.Max(match => match.DurationMinutes)
            })
            .ToList();
        var penalty = 0;
        for (var index = 1; index < waves.Count; index++)
        {
            var gap = Math.Max(0, (int)(waves[index].Start - waves[index - 1].End).TotalMinutes);
            penalty += Math.Max(0, gap - waves[index - 1].Duration);
        }

        return penalty;
    }

    private static string GetScheduleStrategyName(ScheduleAutoSchedulingStrategy strategy)
    {
        return strategy switch
        {
            ScheduleAutoSchedulingStrategy.BalancedRelaxed => "均衡宽松",
            ScheduleAutoSchedulingStrategy.FinalsDayFriendly => "决赛日友好",
            ScheduleAutoSchedulingStrategy.Custom => "自定义",
            _ => "紧凑完成"
        };
    }

    private static bool TrySpreadMatchesWithinDays(
        SchedulePlan plan,
        out SchedulePlan spreadPlan)
    {
        var spreadMatches = new List<ScheduledMatch>(plan.Matches.Count);
        var handledDayLabels = new HashSet<string>(StringComparer.Ordinal);
        var orderedDays = plan.Settings.Days
            .OrderBy(day => day.Date)
            .ToList();

        foreach (var day in orderedDays)
        {
            var dayMatches = plan.Matches
                .Where(match => string.Equals(match.DayLabel, day.DayLabel, StringComparison.Ordinal))
                .OrderBy(match => match.StartTime)
                .ThenBy(match => GetCourtSortIndex(day, match.Court))
                .ThenBy(match => match.Order)
                .ToList();
            handledDayLabels.Add(day.DayLabel);

            if (dayMatches.Count <= 1)
            {
                spreadMatches.AddRange(dayMatches);
                continue;
            }

            if (!TrySpreadDayMatches(dayMatches, day, plan, out var spreadDayMatches))
            {
                spreadPlan = plan;
                return false;
            }

            spreadMatches.AddRange(spreadDayMatches);
        }

        spreadMatches.AddRange(plan.Matches
            .Where(match => !handledDayLabels.Contains(match.DayLabel))
            .OrderBy(match => match.DayLabel, StringComparer.Ordinal)
            .ThenBy(match => match.StartTime)
            .ThenBy(match => match.Order));

        var dayOrder = orderedDays
            .Select((day, index) => (day.DayLabel, Index: index))
            .ToDictionary(item => item.DayLabel, item => item.Index, StringComparer.Ordinal);
        var reordered = spreadMatches
            .OrderBy(match => dayOrder.TryGetValue(match.DayLabel, out var index) ? index : int.MaxValue)
            .ThenBy(match => match.StartTime)
            .ThenBy(match =>
            {
                var day = orderedDays.FirstOrDefault(item => string.Equals(item.DayLabel, match.DayLabel, StringComparison.Ordinal));
                return day is null ? int.MaxValue : GetCourtSortIndex(day, match.Court);
            })
            .ThenBy(match => match.Order)
            .Select((match, index) => match with { Order = index + 1 })
            .ToList();

        spreadPlan = plan with { Matches = reordered };
        if (ScheduleDependencyGraph.Build(spreadPlan).FindOrderViolations().Count > 0)
        {
            spreadPlan = plan;
            return false;
        }

        return true;
    }

    private static bool TrySpreadDayMatches(
        IReadOnlyList<ScheduledMatch> matches,
        ScheduleDaySettings day,
        SchedulePlan originalPlan,
        out IReadOnlyList<ScheduledMatch> spreadMatches)
    {
        spreadMatches = matches;
        var slotStarts = BuildSpreadSlotStarts(day, originalPlan.Settings.MinimumMatchMinutes);
        if (slotStarts.Count == 0)
        {
            return false;
        }

        var sameDayMatchIds = matches
            .Where(match => !string.IsNullOrWhiteSpace(match.MatchId))
            .Select(match => match.MatchId)
            .ToHashSet(StringComparer.Ordinal);
        var waves = BuildSpreadWaves(matches, day);
        var placed = new List<ScheduledMatch>(matches.Count);
        var placedByMatchId = new Dictionary<string, ScheduledMatch>(StringComparer.Ordinal);

        for (var waveIndex = 0; waveIndex < waves.Count; waveIndex++)
        {
            var wave = waves[waveIndex];
            var idealSlotIndex = CalculateIdealSpreadSlotIndex(waveIndex, waves.Count, slotStarts.Count);
            var earliestStart = slotStarts[idealSlotIndex];
            if (placed.Count > 0)
            {
                earliestStart = LimitSpreadGapByPreviousWave(earliestStart, placed);
            }

            foreach (var match in wave)
            {
                foreach (var dependency in match.Dependencies.Where(dependency => sameDayMatchIds.Contains(dependency.SourceMatchId)))
                {
                    if (!placedByMatchId.TryGetValue(dependency.SourceMatchId, out var source))
                    {
                        spreadMatches = matches;
                        return false;
                    }

                    if (source.EndTime > earliestStart)
                    {
                        earliestStart = source.EndTime;
                    }
                }
            }

            var startIndex = FindFirstSpreadSlotIndex(slotStarts, earliestStart);
            if (!TryPlaceSpreadWave(
                wave,
                day,
                slotStarts,
                startIndex,
                originalPlan.Settings.RefereeCount,
                placed,
                out var placedWave))
            {
                spreadMatches = matches;
                return false;
            }

            foreach (var placedMatch in placedWave)
            {
                placed.Add(placedMatch);
                if (!string.IsNullOrWhiteSpace(placedMatch.MatchId))
                {
                    placedByMatchId[placedMatch.MatchId] = placedMatch;
                }
            }
        }

        spreadMatches = placed
            .OrderBy(match => match.StartTime)
            .ThenBy(match => GetCourtSortIndex(day, match.Court))
            .ThenBy(match => match.Order)
            .ToList();
        return true;
    }

    private static TimeOnly LimitSpreadGapByPreviousWave(
        TimeOnly desiredStart,
        IReadOnlyList<ScheduledMatch> placed)
    {
        var previousWaveStart = placed.Max(match => match.StartTime);
        var previousWaveMatches = placed
            .Where(match => match.StartTime == previousWaveStart)
            .ToList();
        var previousWaveEnd = previousWaveMatches.Max(match => match.EndTime);
        var previousWaveDuration = previousWaveMatches.Max(match => match.DurationMinutes);
        var latestSoftStart = previousWaveEnd.AddMinutes(previousWaveDuration);
        return desiredStart > latestSoftStart ? latestSoftStart : desiredStart;
    }

    private static IReadOnlyList<IReadOnlyList<ScheduledMatch>> BuildSpreadWaves(
        IReadOnlyList<ScheduledMatch> matches,
        ScheduleDaySettings day)
    {
        return matches
            .GroupBy(match => match.StartTime)
            .OrderBy(group => group.Key)
            .Select(group => (IReadOnlyList<ScheduledMatch>)group
                .OrderBy(match => GetCourtSortIndex(day, match.Court))
                .ThenBy(match => match.Order)
                .ToList())
            .ToList();
    }

    private static bool TryPlaceSpreadWave(
        IReadOnlyList<ScheduledMatch> wave,
        ScheduleDaySettings day,
        IReadOnlyList<TimeOnly> slotStarts,
        int startIndex,
        int? refereeCount,
        IReadOnlyList<ScheduledMatch> placed,
        out IReadOnlyList<ScheduledMatch> placedWave)
    {
        for (var slotIndex = startIndex; slotIndex < slotStarts.Count; slotIndex++)
        {
            var start = slotStarts[slotIndex];
            if (wave.Any(match => start.AddMinutes(match.DurationMinutes) > day.DayEnd))
            {
                continue;
            }

            if (WouldExceedSpreadConcurrency(day, start, wave, placed, refereeCount))
            {
                continue;
            }

            if (HasSpreadPlayerOverlap(wave, start, placed))
            {
                continue;
            }

            var proposed = new List<ScheduledMatch>(wave.Count);
            foreach (var match in wave)
            {
                var end = start.AddMinutes(match.DurationMinutes);
                var court = FindSpreadWaveCourt(match, start, end, day, placed, proposed);
                if (court is null)
                {
                    proposed.Clear();
                    break;
                }

                proposed.Add(match with
                {
                    StartTime = start,
                    EndTime = end,
                    Court = court!
                });
            }

            if (proposed.Count == wave.Count)
            {
                placedWave = proposed;
                return true;
            }
        }

        placedWave = wave;
        return false;
    }

    private static string? FindSpreadWaveCourt(
        ScheduledMatch match,
        TimeOnly start,
        TimeOnly end,
        ScheduleDaySettings day,
        IReadOnlyList<ScheduledMatch> placed,
        IReadOnlyList<ScheduledMatch> proposed)
    {
        var courtPreference = day.Courts
            .OrderBy(court => string.Equals(court, match.Court, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(court => GetCourtSortIndex(day, court));

        foreach (var court in courtPreference)
        {
            if (!ScheduleResourceCalculator.IsCourtAvailable(day, court, start, end))
            {
                continue;
            }

            if (proposed.Any(candidate => string.Equals(candidate.Court, court, StringComparison.Ordinal)))
            {
                continue;
            }

            if (placed.Any(candidate =>
                string.Equals(candidate.Court, court, StringComparison.Ordinal)
                && candidate.StartTime < end
                && start < candidate.EndTime))
            {
                continue;
            }

            return court;
        }

        return null;
    }

    private static IReadOnlyList<TimeOnly> BuildSpreadSlotStarts(
        ScheduleDaySettings day,
        int slotMinutes)
    {
        var result = new List<TimeOnly>();
        var cursor = day.DayStart;
        var step = Math.Max(1, slotMinutes);
        while (cursor < day.DayEnd)
        {
            result.Add(cursor);
            cursor = cursor.AddMinutes(step);
        }

        return result;
    }

    private static int CalculateIdealSpreadSlotIndex(int matchIndex, int matchCount, int slotCount)
    {
        if (slotCount <= 1 || matchCount <= 1)
        {
            return 0;
        }

        var ideal = (int)Math.Round(matchIndex * (slotCount - 1d) / (matchCount - 1d), MidpointRounding.AwayFromZero);
        return Math.Clamp(ideal, 0, slotCount - 1);
    }

    private static int FindFirstSpreadSlotIndex(
        IReadOnlyList<TimeOnly> slotStarts,
        TimeOnly earliestStart)
    {
        for (var index = 0; index < slotStarts.Count; index++)
        {
            if (slotStarts[index] >= earliestStart)
            {
                return index;
            }
        }

        return slotStarts.Count;
    }

    private static bool WouldExceedSpreadConcurrency(
        ScheduleDaySettings day,
        TimeOnly start,
        IReadOnlyList<ScheduledMatch> wave,
        IReadOnlyList<ScheduledMatch> placed,
        int? refereeCount)
    {
        var longestDuration = wave
            .Select(match => match.DurationMinutes)
            .DefaultIfEmpty(1)
            .Max();
        var end = start.AddMinutes(longestDuration);
        var hardLimit = ScheduleResourceCalculator.GetConcurrentMatchLimit(day, refereeCount, start, end);
        var overlappingMatches = placed.Count(match => match.StartTime < end && start < match.EndTime);
        return overlappingMatches + wave.Count > hardLimit;
    }

    private static bool HasSpreadPlayerOverlap(
        IReadOnlyList<ScheduledMatch> wave,
        TimeOnly start,
        IReadOnlyList<ScheduledMatch> placed)
    {
        foreach (var match in wave)
        {
            var end = start.AddMinutes(match.DurationMinutes);
            var playerKeys = GetScheduledPlayerKeys(match).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (playerKeys.Count == 0)
            {
                continue;
            }

            if (placed
                .Where(candidate => candidate.StartTime < end && start < candidate.EndTime)
                .Any(candidate => GetScheduledPlayerKeys(candidate).Any(playerKeys.Contains)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetScheduledPlayerKeys(ScheduledMatch match)
    {
        return match.SideAPlayerIdentities
            .Concat(match.SideBPlayerIdentities)
            .Select(identity => identity.IdentityKey)
            .Where(key => !string.IsNullOrWhiteSpace(key));
    }

    private static int GetCourtSortIndex(ScheduleDaySettings day, string court)
    {
        for (var index = 0; index < day.Courts.Count; index++)
        {
            if (string.Equals(day.Courts[index], court, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static IReadOnlyDictionary<string, double> BuildDayLoadTargets(
        IReadOnlyList<UnscheduledMatch> matches,
        ScheduleSettings settings)
    {
        var orderedDays = settings.Days.OrderBy(day => day.Date).ToList();
        var capacities = orderedDays.ToDictionary(
            day => day.DayLabel,
            day => (double)CalculateDayCapacityMinutes(day, settings.RefereeCount),
            StringComparer.Ordinal);

        if (settings.AutoSchedulingStrategy == ScheduleAutoSchedulingStrategy.Custom
            && settings.DayLoadTargets.Count > 0)
        {
            var customTargets = settings.DayLoadTargets
                .GroupBy(target => target.DayLabel, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            var resolvedTargets = orderedDays.ToDictionary(
                day => day.DayLabel,
                day => capacities[day.DayLabel] * (customTargets.TryGetValue(day.DayLabel, out var target)
                    ? target.TargetUtilization
                    : 0.75),
                StringComparer.Ordinal);
            EnsureTotalTargetCapacity(
                orderedDays,
                capacities,
                resolvedTargets,
                matches.Sum(match => ResolveTiming(match, settings).MatchMinutes));
            return resolvedTargets;
        }

        if (settings.AutoSchedulingStrategy == ScheduleAutoSchedulingStrategy.Compact || orderedDays.Count <= 1)
        {
            return capacities;
        }

        var totalMinutes = matches.Sum(match => ResolveTiming(match, settings).MatchMinutes);
        return BuildDefaultDayLoadTargets(orderedDays, capacities, totalMinutes, settings.AutoSchedulingStrategy)
            .ToDictionary(
                target => target.DayLabel,
                target => capacities[target.DayLabel] * target.TargetUtilization,
                StringComparer.Ordinal);
    }

    private static IReadOnlyList<ScheduleDayLoadTarget> BuildDefaultDayLoadTargets(
        IReadOnlyList<ScheduleDaySettings> orderedDays,
        IReadOnlyDictionary<string, double> capacities,
        double totalMinutes,
        ScheduleAutoSchedulingStrategy strategy)
    {
        if (orderedDays.Count == 0)
        {
            return [];
        }

        if (strategy == ScheduleAutoSchedulingStrategy.Compact || orderedDays.Count <= 1)
        {
            return orderedDays
                .Select(day => new ScheduleDayLoadTarget(day.DayLabel, 0.95, 1.0))
                .ToList();
        }

        var totalCapacity = Math.Max(1d, capacities.Values.Sum());
        var totalUtilization = totalMinutes / totalCapacity;
        var targetMinutesByDay = new Dictionary<string, double>(StringComparer.Ordinal);

        if (strategy == ScheduleAutoSchedulingStrategy.FinalsDayFriendly)
        {
            var lastDay = orderedDays.Last().DayLabel;
            var regularUtilization = Math.Clamp(totalUtilization * 0.95, 0.25, 0.70);
            foreach (var day in orderedDays)
            {
                targetMinutesByDay[day.DayLabel] = string.Equals(day.DayLabel, lastDay, StringComparison.Ordinal)
                    ? capacities[day.DayLabel]
                    : capacities[day.DayLabel] * regularUtilization;
            }
        }
        else
        {
            var balancedUtilization = Math.Clamp(totalUtilization * 1.15, 0.35, 0.85);
            foreach (var day in orderedDays)
            {
                targetMinutesByDay[day.DayLabel] = capacities[day.DayLabel] * balancedUtilization;
            }
        }

        EnsureTotalTargetCapacity(orderedDays, capacities, targetMinutesByDay, totalMinutes);
        return orderedDays
            .Select(day =>
            {
                var capacity = Math.Max(1d, capacities[day.DayLabel]);
                var target = Math.Clamp(targetMinutesByDay[day.DayLabel] / capacity, 0.05, 1.0);
                return new ScheduleDayLoadTarget(day.DayLabel, target, Math.Min(1.0, target + 0.15));
            })
            .ToList();
    }

    private static IReadOnlyList<ScheduleStageWaveTarget> BuildDefaultStageWaveTargets(
        IReadOnlyList<ScheduleDaySettings> orderedDays,
        ScheduleAutoSchedulingStrategy strategy)
    {
        if (orderedDays.Count == 0)
        {
            return [];
        }

        if (strategy == ScheduleAutoSchedulingStrategy.Compact)
        {
            return orderedDays
                .Select((day, index) => new ScheduleStageWaveTarget(day.DayLabel, (index + 1d) / orderedDays.Count))
                .ToList();
        }

        var result = new List<ScheduleStageWaveTarget>();
        for (var index = 0; index < orderedDays.Count; index++)
        {
            var isLast = index == orderedDays.Count - 1;
            double progress;
            if (isLast)
            {
                progress = 1.0;
            }
            else if (strategy == ScheduleAutoSchedulingStrategy.FinalsDayFriendly)
            {
                progress = Math.Clamp(0.45 + (index * 0.25), 0.3, 0.82);
            }
            else
            {
                progress = Math.Clamp(0.55 + (index * 0.28), 0.35, 0.9);
            }

            result.Add(new ScheduleStageWaveTarget(orderedDays[index].DayLabel, progress));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, double> BuildStageWaveTargets(ScheduleSettings settings)
    {
        if (settings.AutoSchedulingStrategy != ScheduleAutoSchedulingStrategy.Custom
            || !settings.SynchronizeStageWaves
            || settings.StageWaveTargets.Count == 0)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        return settings.StageWaveTargets
            .GroupBy(target => target.DayLabel, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().CumulativeProgress, StringComparer.Ordinal);
    }

    private static void EnsureTotalTargetCapacity(
        IReadOnlyList<ScheduleDaySettings> orderedDays,
        IReadOnlyDictionary<string, double> capacities,
        IDictionary<string, double> targets,
        double totalMinutes)
    {
        var deficit = totalMinutes - targets.Values.Sum();
        if (deficit <= 0)
        {
            return;
        }

        foreach (var day in orderedDays.Reverse())
        {
            var current = targets[day.DayLabel];
            var capacity = capacities[day.DayLabel];
            var room = Math.Max(0, capacity - current);
            if (room <= 0)
            {
                continue;
            }

            var added = Math.Min(room, deficit);
            targets[day.DayLabel] = current + added;
            deficit -= added;
            if (deficit <= 0)
            {
                return;
            }
        }
    }

    private static bool IsWithinDayLoadTarget(
        ScheduleDaySettings day,
        ResolvedScheduleTiming timing,
        IReadOnlyDictionary<string, int> scheduledMinutesByDay,
        IReadOnlyDictionary<string, double> dayLoadTargets,
        int maximumMatchMinutes,
        int? refereeCount)
    {
        var capacity = CalculateDayCapacityMinutes(day, refereeCount);
        var target = dayLoadTargets.TryGetValue(day.DayLabel, out var value) ? value : capacity;
        if (target >= capacity)
        {
            return true;
        }

        var scheduledMinutes = scheduledMinutesByDay.TryGetValue(day.DayLabel, out var minutes) ? minutes : 0;
        return scheduledMinutes + timing.MatchMinutes <= target + maximumMatchMinutes;
    }

    private static IReadOnlyList<UnscheduledMatchPreview> BuildUnscheduledPreviews(
        IEnumerable<UnscheduledMatch> remaining,
        IReadOnlySet<int> completed,
        int scheduledCount)
    {
        return remaining
            .OrderBy(match => match.Id)
            .Select((match, index) => new UnscheduledMatchPreview(
                scheduledCount + index + 1,
                match.GroupNumber,
                match.GroupName,
                match.Phase,
                match.MatchName,
                match.SideA,
                match.SideB,
                match.Note,
                match.SameUnit,
                match.DependencyIds.All(completed.Contains)
                    ? "资源不足，未能安排到时间和场地"
                    : "前置比赛未能全部安排"))
            .ToList();
    }

    private static bool IsEligible(
        UnscheduledMatch match,
        ResolvedScheduleTiming timing,
        ScheduleDaySettings day,
        TimeOnly start,
        string court,
        IEnumerable<UnscheduledMatch> remaining,
        IReadOnlyDictionary<int, ScheduledAssignment> scheduledById,
        IReadOnlyList<ScheduledAssignment> dailyAssignments,
        ScheduleSettings settings)
    {
        if ((day.DayEnd - start).TotalMinutes < timing.MatchMinutes)
        {
            return false;
        }

        var end = start.AddMinutes(timing.MatchMinutes);
        if (!ScheduleResourceCalculator.IsCourtAvailable(day, court, start, end))
        {
            return false;
        }

        if (WouldExceedRefereeCapacity(day, start, end, dailyAssignments, settings.RefereeCount))
        {
            return false;
        }

        if (match.IsChampionshipFinal && remaining.Any(item => item.Id != match.Id && item.IsPlacementPlayoff))
        {
            return false;
        }

        if (!match.DependencyIds.All(id => IsDependencyCompleted(id, scheduledById, day.Date, start)))
        {
            return false;
        }

        var overlappingEntrants = dailyAssignments
            .Where(assignment => assignment.StartTime < end && start < assignment.EndTime)
            .SelectMany(assignment => assignment.EntrantPaths)
            .ToList();
        if (HasCompatibleEntrantOverlap(match.EntrantPaths, overlappingEntrants))
        {
            return false;
        }

        var sameTimingBucketMatches = dailyAssignments
            .Where(assignment => assignment.TimingBucket == timing.Bucket)
            .Select(assignment => assignment.EntrantPaths)
            .ToList();
        var allTimingBucketMatches = dailyAssignments
            .Select(assignment => assignment.EntrantPaths)
            .ToList();
        return !WouldExceedDailyLimit(match, allTimingBucketMatches, timing.MaxMatchesPerEntrantPerDayAcrossDay)
               && !WouldExceedDailyLimit(match, sameTimingBucketMatches, timing.MaxMatchesPerEntrantPerDay);
    }

    private static bool IsDependencyCompleted(
        int dependencyId,
        IReadOnlyDictionary<int, ScheduledAssignment> scheduledById,
        DateOnly date,
        TimeOnly start)
    {
        return scheduledById.TryGetValue(dependencyId, out var dependency)
            && (dependency.Date < date || dependency.Date == date && dependency.EndTime <= start);
    }

    private static ResolvedScheduleTiming ResolveTiming(UnscheduledMatch match, ScheduleSettings settings)
    {
        if (settings.HasKnockoutTimingSplit && IsBeforeBoundaryTiming(match, settings))
        {
            var timing = settings.BeforeBoundaryTiming!;
            return new ResolvedScheduleTiming(
                ScheduleTimingBucket.BeforeBoundary,
                timing.MatchMinutes,
                timing.MaxMatchesPerEntrantPerDay,
                Math.Max(timing.MaxMatchesPerEntrantPerDay, settings.MaxMatchesPerEntrantPerDay));
        }

        return new ResolvedScheduleTiming(
            ScheduleTimingBucket.Default,
            settings.MatchMinutes,
            settings.MaxMatchesPerEntrantPerDay,
            settings.HasKnockoutTimingSplit
                ? Math.Max(settings.MaxMatchesPerEntrantPerDay, settings.BeforeBoundaryTiming!.MaxMatchesPerEntrantPerDay)
                : settings.MaxMatchesPerEntrantPerDay);
    }

    private static bool IsBeforeBoundaryTiming(UnscheduledMatch match, ScheduleSettings settings)
    {
        if (match.IsPlacementPlayoff)
        {
            return false;
        }

        if (match.ForceBeforeTimingBoundary)
        {
            return true;
        }

        return match.KnockoutEntrantCount.HasValue
            && match.KnockoutEntrantCount.Value > settings.KnockoutTimingBoundaryEntrants!.Value;
    }

    private static int GetSchedulingStageRank(UnscheduledMatch match)
    {
        if (match.IsPlacementPlayoff)
        {
            return GetPlacementStageRank(match.Phase);
        }

        if (match.IsChampionshipFinal)
        {
            return 10_000;
        }

        if (match.Phase.Contains("首轮", StringComparison.Ordinal))
        {
            return 0;
        }

        if (match.KnockoutEntrantCount.HasValue)
        {
            return 1_000 - match.KnockoutEntrantCount.Value;
        }

        if (TryParseKnockoutEntrantCount(match.Phase, out var entrantCount))
        {
            return 1_000 - entrantCount;
        }

        return 5_000;
    }

    private static int GetStageWaveSortKey(
        UnscheduledMatch match,
        ScheduleDaySettings day,
        IReadOnlyDictionary<string, double> stageWaveTargets,
        ScheduleSettings settings)
    {
        if (settings.AutoSchedulingStrategy != ScheduleAutoSchedulingStrategy.Custom
            || !settings.SynchronizeStageWaves
            || stageWaveTargets.Count == 0
            || !stageWaveTargets.TryGetValue(day.DayLabel, out var target))
        {
            return 0;
        }

        var progress = EstimateStageProgress(match);
        if (progress <= target)
        {
            return 0;
        }

        return (int)Math.Round((progress - target) * 10_000);
    }

    private static double EstimateStageProgress(UnscheduledMatch match)
    {
        var text = $"{match.Phase} {match.MatchName}";
        if (match.IsChampionshipFinal
            || text.Contains("决赛", StringComparison.Ordinal)
            || text.Contains("3/4名", StringComparison.Ordinal))
        {
            return 1.0;
        }

        if (text.Contains("5-8名", StringComparison.Ordinal)
            || text.Contains("5/6名", StringComparison.Ordinal)
            || text.Contains("7/8名", StringComparison.Ordinal))
        {
            return 0.88;
        }

        if (text.Contains("半决赛", StringComparison.Ordinal) || text.Contains("4进2", StringComparison.Ordinal))
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

        if (match.KnockoutEntrantCount is >= 2)
        {
            return match.KnockoutEntrantCount.Value switch
            {
                <= 2 => 1.0,
                <= 4 => 0.82,
                <= 8 => 0.72,
                <= 16 => 0.65,
                <= 32 => 0.48,
                <= 64 => 0.32,
                _ => 0.16
            };
        }

        return 0.5;
    }

    private static int GetPlacementStageRank(string phase)
    {
        if (string.Equals(phase, PlacementPlayoffLabels.FifthToEighthSemiPhase, StringComparison.Ordinal))
        {
            return 995;
        }

        return 997;
    }

    private static bool TryParseKnockoutEntrantCount(string phase, out int entrantCount)
    {
        entrantCount = 0;
        var separatorIndex = phase.IndexOf('进', StringComparison.Ordinal);
        return separatorIndex > 0
            && int.TryParse(phase[..separatorIndex], out entrantCount);
    }

    private static int GetRestSortKey(
        UnscheduledMatch match,
        TimeOnly start,
        IReadOnlyList<ScheduledAssignment> dailyAssignments)
    {
        return -GetMinimumRestMinutes(match.EntrantPaths, start, dailyAssignments);
    }

    private static int GetMinimumRestMinutes(
        IReadOnlyList<EntrantPath> entrantPaths,
        TimeOnly start,
        IReadOnlyList<ScheduledAssignment> dailyAssignments)
    {
        var minimumRestMinutes = int.MaxValue;
        foreach (var entrantPath in entrantPaths)
        {
            TimeOnly? latestPreviousEnd = null;
            foreach (var assignment in dailyAssignments)
            {
                if (!assignment.EntrantPaths.Any(previousPath =>
                    string.Equals(previousPath.EntrantKey, entrantPath.EntrantKey, StringComparison.Ordinal)
                    && AreConditionsCompatible(previousPath.Conditions, entrantPath.Conditions)))
                {
                    continue;
                }

                if (!latestPreviousEnd.HasValue || assignment.EndTime > latestPreviousEnd.Value)
                {
                    latestPreviousEnd = assignment.EndTime;
                }
            }

            if (latestPreviousEnd.HasValue)
            {
                var restMinutes = (int)Math.Max(0, (start - latestPreviousEnd.Value).TotalMinutes);
                minimumRestMinutes = Math.Min(minimumRestMinutes, restMinutes);
            }
        }

        return minimumRestMinutes == int.MaxValue ? 24 * 60 : minimumRestMinutes;
    }

    private static TimeOnly FindNextWakeTime(
        TimeOnly current,
        ScheduleDaySettings day,
        string court,
        IReadOnlyDictionary<string, TimeOnly> courtAvailableAt,
        IReadOnlyList<ScheduledAssignment> dailyAssignments)
    {
        var nextCourtTime = courtAvailableAt.Values
            .Where(time => time > current)
            .DefaultIfEmpty(day.DayEnd)
            .Min();
        var nextMatchEnd = dailyAssignments
            .Select(assignment => assignment.EndTime)
            .Where(time => time > current)
            .DefaultIfEmpty(day.DayEnd)
            .Min();
        var nextUnavailableEnd = (day.UnavailableCourtWindows ?? Array.Empty<ScheduleCourtAvailabilityBlock>())
            .Where(window => window.AppliesTo(court) && window.StartTime <= current && window.EndTime > current)
            .Select(window => window.EndTime)
            .DefaultIfEmpty(day.DayEnd)
            .Min();
        var next = new[] { nextCourtTime, nextMatchEnd, nextUnavailableEnd }.Min();
        return next > current ? next : day.DayEnd;
    }

    private static int CalculateDayCapacityMinutes(ScheduleDaySettings day, int? refereeCount)
    {
        return ScheduleResourceCalculator.CalculateDayCapacityMinutes(day, refereeCount, slotMinutes: 1);
    }

    private static bool WouldExceedRefereeCapacity(
        ScheduleDaySettings day,
        TimeOnly start,
        TimeOnly end,
        IReadOnlyList<ScheduledAssignment> dailyAssignments,
        int? refereeCount)
    {
        var concurrentLimit = ScheduleResourceCalculator.GetConcurrentMatchLimit(day, refereeCount, start, end);
        var overlappingMatches = dailyAssignments.Count(assignment =>
            assignment.StartTime < end && start < assignment.EndTime);
        return overlappingMatches >= concurrentLimit;
    }

    private static bool HasCompatibleEntrantOverlap(
        IReadOnlyList<EntrantPath> first,
        IReadOnlyList<EntrantPath> second)
    {
        return first.Any(left => second.Any(right =>
            string.Equals(left.EntrantKey, right.EntrantKey, StringComparison.Ordinal)
            && AreConditionsCompatible(left.Conditions, right.Conditions)));
    }

    private static bool WouldExceedDailyLimit(
        UnscheduledMatch candidate,
        IReadOnlyList<IReadOnlyList<EntrantPath>> dailyMatches,
        int maxMatchesPerEntrantPerDay)
    {
        var entrantKeys = candidate.EntrantPaths
            .Select(path => path.EntrantKey)
            .Distinct(StringComparer.Ordinal);
        foreach (var entrantKey in entrantKeys)
        {
            var appearances = dailyMatches
                .SelectMany(paths => paths)
                .Where(path => string.Equals(path.EntrantKey, entrantKey, StringComparison.Ordinal))
                .Concat(candidate.EntrantPaths.Where(path => string.Equals(path.EntrantKey, entrantKey, StringComparison.Ordinal)))
                .ToList();
            if (CanSelectCompatibleAppearances(
                appearances,
                maxMatchesPerEntrantPerDay + 1,
                startIndex: 0,
                conditions: new Dictionary<int, MatchOutcome>()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanSelectCompatibleAppearances(
        IReadOnlyList<EntrantPath> appearances,
        int targetCount,
        int startIndex,
        IReadOnlyDictionary<int, MatchOutcome> conditions)
    {
        if (targetCount <= 0)
        {
            return true;
        }

        if (appearances.Count - startIndex < targetCount)
        {
            return false;
        }

        for (var index = startIndex; index < appearances.Count; index++)
        {
            if (!TryMergeConditions(conditions, appearances[index].Conditions, out var mergedConditions))
            {
                continue;
            }

            if (CanSelectCompatibleAppearances(appearances, targetCount - 1, index + 1, mergedConditions))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMergeConditions(
        IReadOnlyDictionary<int, MatchOutcome> existingConditions,
        IReadOnlyList<OutcomeCondition> candidateConditions,
        out IReadOnlyDictionary<int, MatchOutcome> mergedConditions)
    {
        var merged = new Dictionary<int, MatchOutcome>(existingConditions);
        foreach (var condition in candidateConditions)
        {
            if (merged.TryGetValue(condition.MatchId, out var existingOutcome)
                && existingOutcome != condition.Outcome)
            {
                mergedConditions = existingConditions;
                return false;
            }

            merged[condition.MatchId] = condition.Outcome;
        }

        mergedConditions = merged;
        return true;
    }

    private static bool AreConditionsCompatible(
        IReadOnlyList<OutcomeCondition> first,
        IReadOnlyList<OutcomeCondition> second)
    {
        return !first.Any(left => second.Any(right =>
            left.MatchId == right.MatchId && left.Outcome != right.Outcome));
    }

    private static void Validate(ScheduleSettings settings)
    {
        if (settings.Days.Count == 0)
        {
            throw new DrawValidationException("请至少添加一个赛程日。");
        }

        if (settings.MatchMinutes <= 0)
        {
            throw new DrawValidationException("单场比赛耗时必须大于 0 分钟。");
        }

        if (settings.MaxMatchesPerEntrantPerDay <= 0)
        {
            throw new DrawValidationException("单名选手每日最多场次必须大于 0。");
        }

        if (settings.RefereeCount is <= 0)
        {
            throw new DrawValidationException("裁判人数必须是大于 0 的整数。");
        }

        if (settings.HasKnockoutTimingSplit)
        {
            if (settings.KnockoutTimingBoundaryEntrants < 2)
            {
                throw new DrawValidationException("赛程分界线至少应为 2 强。");
            }

            if (settings.BeforeBoundaryTiming!.MatchMinutes <= 0)
            {
                throw new DrawValidationException("分界线前单场比赛耗时必须大于 0 分钟。");
            }

            if (settings.BeforeBoundaryTiming.MaxMatchesPerEntrantPerDay <= 0)
            {
                throw new DrawValidationException("分界线前单名选手每日最多场次必须大于 0。");
            }
        }

        foreach (var day in settings.Days)
        {
            if (day.Courts.Count == 0 || day.Courts.Any(string.IsNullOrWhiteSpace))
            {
                throw new DrawValidationException($"{day.DayLabel} 请至少设置一片可用场地。");
            }

            if (day.DayEnd <= day.DayStart)
            {
                throw new DrawValidationException($"{day.DayLabel} 的结束时间必须晚于开始时间。");
            }

            if ((day.DayEnd - day.DayStart).TotalMinutes < settings.MinimumMatchMinutes)
            {
                throw new DrawValidationException($"{day.DayLabel} 的时间段无法容纳一场比赛。");
            }
        }
    }

    private sealed record CandidateMatch(
        UnscheduledMatch Match,
        ResolvedScheduleTiming Timing);

    private sealed record ScheduledAssignment(
        int MatchId,
        DateOnly Date,
        TimeOnly StartTime,
        TimeOnly EndTime,
        IReadOnlyList<EntrantPath> EntrantPaths,
        ScheduleTimingBucket TimingBucket);

    private readonly record struct ResolvedScheduleTiming(
        ScheduleTimingBucket Bucket,
        int MatchMinutes,
        int MaxMatchesPerEntrantPerDay,
        int MaxMatchesPerEntrantPerDayAcrossDay);

    private enum ScheduleTimingBucket
    {
        Default,
        BeforeBoundary
    }


}
