using System.Text.RegularExpressions;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows;

public sealed partial class CrossEventConflictWorkflow
{
    public CrossEventScheduleBoard MoveScheduleItem(
        CrossEventScheduleBoard board,
        string itemKey,
        string dayLabel,
        TimeOnly startTime,
        string court)
    {
        return MoveScheduleItemCore(board, itemKey, dayLabel, startTime, court, ensureDependencyOrder: true);
    }

    private CrossEventScheduleBoard MoveScheduleItemCore(
        CrossEventScheduleBoard board,
        string itemKey,
        string dayLabel,
        TimeOnly startTime,
        string court,
        bool ensureDependencyOrder)
    {
        if (string.IsNullOrWhiteSpace(itemKey))
        {
            throw new DrawValidationException("请选择要调整的比赛。");
        }

        var item = board.Items.FirstOrDefault(candidate => string.Equals(candidate.Key, itemKey, StringComparison.Ordinal))
            ?? throw new DrawValidationException("找不到要调整的比赛。");
        if (item.IsCompleted)
        {
            throw new DrawValidationException("已完成场次不允许在多项目编排中拖动，避免和赛果记录不一致。");
        }

        var targetDay = board.Days.FirstOrDefault(day => string.Equals(day.DayLabel, dayLabel, StringComparison.Ordinal))
            ?? throw new DrawValidationException($"找不到比赛日：{dayLabel}");
        if (!targetDay.Courts.Contains(court, StringComparer.Ordinal))
        {
            throw new DrawValidationException($"{dayLabel} 没有场地 {court}。");
        }

        var endTime = startTime.AddMinutes(item.DurationMinutes);
        if (startTime < targetDay.StartTime || endTime > targetDay.EndTime)
        {
            throw new DrawValidationException($"{dayLabel} {startTime:HH:mm}-{endTime:HH:mm} 超出可用时间段 {targetDay.TimeRange}。");
        }

        if (!ScheduleResourceCalculator.IsCourtAvailable(targetDay, court, startTime, endTime))
        {
            throw new DrawValidationException("目标场地在该时间段不可用，请选择其他空位。");
        }

        var hasCourtOverlap = board.Items.Any(other =>
            !string.Equals(other.Key, itemKey, StringComparison.Ordinal)
            && string.Equals(other.DayLabel, dayLabel, StringComparison.Ordinal)
            && string.Equals(other.Court, court, StringComparison.OrdinalIgnoreCase)
            && TimeRangesOverlap(startTime, endTime, other.StartTime, other.EndTime));
        if (hasCourtOverlap)
        {
            throw new DrawValidationException("目标时间和场地已有比赛，请选择空位后再调整。");
        }

        if (WouldExceedRefereeCapacity(board, itemKey, dayLabel, startTime, endTime, board.SchedulingOptions?.RefereeCount))
        {
            throw new DrawValidationException("目标时间段已达到裁判人数可承载的同时比赛上限。");
        }

        var dailyLimitOverages = FindPlayerDailyLimitOverages(board, item, dayLabel);
        if (dailyLimitOverages.Count > 0)
        {
            throw new DrawValidationException(
                $"目标比赛日会超过跨项目选手每日最多 {CrossEventScheduleRules.MaxPlayerMatchesPerDay} 场的规则上限：{string.Join("；", dailyLimitOverages)}。");
        }

        var sources = board.Sources
            .Select(source => MoveMatchInSource(source, itemKey, dayLabel, startTime, endTime, court))
            .ToList();
        if (ensureDependencyOrder)
        {
            foreach (var source in sources)
            {
                ScheduleDependencyGraph.Build(BuildSchedulePlan(source)).EnsureDependencyOrder();
            }
        }

        return BuildScheduleBoard(sources, board.MinimumRestMinutes, hasUnsavedChanges: true, board.SchedulingOptions);
    }

    public ScheduleBoardMoveValidationResult ValidateScheduleItemMove(
        CrossEventScheduleBoard board,
        string itemKey,
        string dayLabel,
        TimeOnly startTime,
        string court)
    {
        var targetText = $"目标：{dayLabel} {startTime:HH:mm} · {court}";
        try
        {
            var movedBoard = MoveScheduleItemCore(board, itemKey, dayLabel, startTime, court, ensureDependencyOrder: false);
            var movedItem = movedBoard.Items.FirstOrDefault(item => string.Equals(item.Key, itemKey, StringComparison.Ordinal));
            if (movedItem is null)
            {
                return ScheduleBoardMoveValidationResult.Blocked("找不到移动后的比赛。", [itemKey]);
            }

            var source = movedBoard.Sources.FirstOrDefault(candidate => string.Equals(candidate.SourceId, movedItem.SourceId, StringComparison.Ordinal));
            if (source is not null)
            {
                var fixableOrderViolations = FindFixableCascadeOrderViolations(BuildSchedulePlan(source), movedItem.MatchName);
                var allOrderViolations = ScheduleDependencyGraph.Build(BuildSchedulePlan(source)).FindOrderViolations();
                var unfixableOrderViolation = allOrderViolations.FirstOrDefault(violation =>
                    !fixableOrderViolations.Any(fixable => string.Equals(
                        fixable.Edge.Dependent.MatchId,
                        violation.Edge.Dependent.MatchId,
                        StringComparison.Ordinal)));
                if (unfixableOrderViolation is not null)
                {
                    return ScheduleBoardMoveValidationResult.Blocked(
                        $"目标：{dayLabel} {startTime:HH:mm} · {court} 不可放置：{BuildDependencyOrderMessage(unfixableOrderViolation)}",
                        [movedItem.MatchName]);
                }

                if (fixableOrderViolations.Count > 0)
                {
                    return ScheduleBoardMoveValidationResult.Warning(
                        $"{targetText} 可放置，但会影响 {fixableOrderViolations.Count} 条本项目后续依赖；可选择连锁移动后续场次自动修复。",
                        fixableOrderViolations
                            .Select(violation => violation.Edge.Dependent.MatchName)
                            .Prepend(movedItem.MatchName)
                            .Distinct(StringComparer.Ordinal)
                            .ToList());
                }
            }

            return movedItem.ConflictSeverity switch
            {
                CrossEventConflictSeverity.Severe => ScheduleBoardMoveValidationResult.Blocked(
                    $"{targetText} 不可放置：{movedItem.ConflictSummary}",
                    [movedItem.MatchName]),
                CrossEventConflictSeverity.Warning or CrossEventConflictSeverity.Notice => ScheduleBoardMoveValidationResult.Warning(
                    $"{targetText} 可放置，但有提醒：{movedItem.ConflictSummary}",
                    [movedItem.MatchName]),
                _ => ScheduleBoardMoveValidationResult.Allowed($"{targetText} 可以放置。")
            };
        }
        catch (DrawValidationException ex)
        {
            return ScheduleBoardMoveValidationResult.Blocked($"{targetText} 不可放置：{ex.Message}", [itemKey]);
        }
    }

    public ScheduleBoardCascadeMovePreview BuildScheduleItemCascadeMovePreview(
        CrossEventScheduleBoard board,
        string itemKey,
        string dayLabel,
        TimeOnly startTime,
        string court)
    {
        var movedBoard = MoveScheduleItemCore(board, itemKey, dayLabel, startTime, court, ensureDependencyOrder: false);
        var movedItem = movedBoard.Items.FirstOrDefault(item => string.Equals(item.Key, itemKey, StringComparison.Ordinal))
            ?? throw new DrawValidationException("找不到移动后的比赛。");
        var source = movedBoard.Sources.FirstOrDefault(candidate => string.Equals(candidate.SourceId, movedItem.SourceId, StringComparison.Ordinal))
            ?? throw new DrawValidationException("找不到移动场次对应的项目。");
        var completedMatchNames = source.Matches
            .Where(match => match.IsCompleted)
            .Select(match => match.MatchName)
            .ToHashSet(StringComparer.Ordinal);
        var preview = ScheduleWorkflow.BuildCascadeMovePreviewFromSchedule(
            BuildSchedulePlan(source),
            movedItem.MatchName,
            completedMatchNames,
            movedItem.EventName);
        return preview with
        {
            CrossEventImpacts = BuildCrossEventImpactPreviewItems(movedBoard, movedItem),
            CrossEventImpactNote = BuildCrossEventImpactPreviewNote(movedItem)
        };
    }

    public ScheduleBoardCascadeMoveResult<CrossEventScheduleBoard> CascadeMoveScheduleItem(
        CrossEventScheduleBoard board,
        string itemKey,
        string dayLabel,
        TimeOnly startTime,
        string court)
    {
        var entries = BuildGlobalScheduleEntries(board);
        var entryLookup = entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        if (!entryLookup.TryGetValue(itemKey, out var rootEntry))
        {
            throw new DrawValidationException("找不到需要连锁移动的比赛。");
        }

        if (rootEntry.Match.IsCompleted)
        {
            throw new DrawValidationException("已完成场次不允许在多项目编排中拖动，避免和赛果记录不一致。");
        }

        var dayNumbers = BuildBoardDayNumberLookup(board.Days);
        var cascadeEntries = BuildGlobalCascadeEntries(rootEntry, entryLookup);
        var cascadeKeys = cascadeEntries
            .Select(entry => entry.Entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        var locked = cascadeEntries.FirstOrDefault(entry => entry.Entry.Match.IsCompleted);
        if (locked is not null)
        {
            throw new DrawValidationException($"无法连锁移动：后续场次“{locked.Entry.Match.MatchName}”已有赛果，不能自动调整。");
        }

        // Build a complete replacement placement map off to the side. The board is rebuilt only
        // after every dependent match finds a legal slot, so a failed cascade leaves the input intact.
        var placements = entries
            .Where(entry => !cascadeKeys.Contains(entry.Key))
            .ToDictionary(
                entry => entry.Key,
                entry => new GlobalSchedulePlacement(entry.Match.DayLabel, entry.Match.StartTime, entry.Match.EndTime, entry.Match.Court),
                StringComparer.Ordinal);
        var targetPlacement = BuildTargetGlobalPlacement(board, rootEntry, dayLabel, startTime, court);
        if (!IsGlobalPlacementValid(board, rootEntry, targetPlacement, placements, entryLookup, dayNumbers, board.SchedulingOptions?.RefereeCount))
        {
            throw new DrawValidationException("无法连锁移动：当前场次目标位置不满足场地、依赖或兼项休息约束。");
        }

        placements[rootEntry.Key] = targetPlacement;
        foreach (var cascadeEntry in cascadeEntries.Where(entry => entry.Depth > 0))
        {
            var entry = cascadeEntry.Entry;
            var originalStart = BuildComparableMinute(entry.Match.DayLabel, entry.Match.StartTime, dayNumbers);
            var minimumStart = Math.Max(
                originalStart,
                FindMinimumGlobalDependencyStart(entry, placements, entryLookup, dayNumbers, board.MinimumRestMinutes));
            var placement = FindEarliestGlobalCascadePlacement(
                board,
                entry,
                placements,
                entryLookup,
                dayNumbers,
                minimumStart)
                ?? throw new DrawValidationException($"无法连锁移动：找不到“{entry.Source.EventName} · {entry.Match.MatchName}”满足依赖、场地、每日上限和兼项休息约束的后续位置。");
            placements[entry.Key] = placement;
        }

        var sources = ApplyGlobalPlacements(board.Sources, placements);
        var adjusted = BuildScheduleBoard(sources, board.MinimumRestMinutes, hasUnsavedChanges: true, board.SchedulingOptions);
        var movedItems = BuildGlobalCascadeMovedItems(cascadeEntries, placements);
        var severeConflict = adjusted.Items.FirstOrDefault(item =>
            cascadeKeys.Contains(item.Key)
            && item.ConflictSeverity == CrossEventConflictSeverity.Severe);
        if (severeConflict is not null)
        {
            throw new DrawValidationException($"无法连锁移动：{severeConflict.EventName} · {severeConflict.MatchName} 仍有严重跨项目冲突：{severeConflict.ConflictSummary}");
        }

        return new ScheduleBoardCascadeMoveResult<CrossEventScheduleBoard>(
            adjusted,
            movedItems,
            movedItems.Count == 0 ? ["当前位置已经满足连锁依赖，无需移动后续场次。"] : []);
    }


    private static IReadOnlyDictionary<string, int> BuildBoardDayNumberLookup(IReadOnlyList<CrossEventScheduleBoardDay> days)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var fallback = 1_000_000;
        foreach (var day in days.OrderBy(day => day.DayLabel, StringComparer.Ordinal))
        {
            if (DateOnly.TryParse(day.DayLabel, out var date))
            {
                result[day.DayLabel] = date.DayNumber;
            }
            else
            {
                result[day.DayLabel] = fallback++;
            }
        }

        return result;
    }

    private static IReadOnlyList<ScheduleDependencyOrderViolation> FindFixableCascadeOrderViolations(
        SchedulePlan schedule,
        string matchName)
    {
        var root = schedule.Matches.FirstOrDefault(match => string.Equals(match.MatchName, matchName, StringComparison.Ordinal));
        if (root is null)
        {
            return [];
        }

        var graph = ScheduleDependencyGraph.Build(schedule);
        var cascadeMatchIds = BuildCascadeMatchIdSet(graph, root.MatchId);
        return graph.FindOrderViolations()
            .Where(violation => cascadeMatchIds.Contains(violation.Edge.Source.MatchId)
                                && cascadeMatchIds.Contains(violation.Edge.Dependent.MatchId))
            .ToList();
    }

    private static HashSet<string> BuildCascadeMatchIdSet(ScheduleDependencyGraph graph, string rootMatchId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { rootMatchId };
        var queue = new Queue<string>();
        queue.Enqueue(rootMatchId);
        var edgesBySource = graph.Edges
            .GroupBy(edge => edge.Source.MatchId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            var sourceMatchId = queue.Dequeue();
            if (!edgesBySource.TryGetValue(sourceMatchId, out var edges))
            {
                continue;
            }

            foreach (var edge in edges)
            {
                if (result.Add(edge.Dependent.MatchId))
                {
                    queue.Enqueue(edge.Dependent.MatchId);
                }
            }
        }

        return result;
    }

    private static string BuildDependencyOrderMessage(ScheduleDependencyOrderViolation violation)
    {
        var edge = violation.Edge;
        return $"{edge.Dependent.MatchName} 依赖 {edge.Source.MatchName} 的{ScheduleDependencyGraph.FormatOutcome(edge.Dependency.Outcome)}，但后续场次在前序场次结束前开始。";
    }

    private static IReadOnlyList<GlobalCascadeEntry> BuildGlobalCascadeEntries(
        GlobalScheduleEntry root,
        IReadOnlyDictionary<string, GlobalScheduleEntry> entryLookup)
    {
        var result = new List<GlobalCascadeEntry> { new(0, root) };
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.Key };
        var queue = new Queue<(GlobalScheduleEntry Entry, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (source, depth) = queue.Dequeue();
            foreach (var dependentKey in source.DependentKeys
                         .OrderBy(key => entryLookup.TryGetValue(key, out var entry) ? entry.Match.DayLabel : "", StringComparer.Ordinal)
                         .ThenBy(key => entryLookup.TryGetValue(key, out var entry) ? entry.Match.StartTime : TimeOnly.MinValue)
                         .ThenBy(key => entryLookup.TryGetValue(key, out var entry) ? entry.Match.Court : "", StringComparer.Ordinal)
                         .ThenBy(key => entryLookup.TryGetValue(key, out var entry) ? entry.Match.MatchName : "", StringComparer.Ordinal))
            {
                if (!entryLookup.TryGetValue(dependentKey, out var dependent)
                    || !visited.Add(dependent.Key))
                {
                    continue;
                }

                var nextDepth = depth + 1;
                result.Add(new GlobalCascadeEntry(nextDepth, dependent));
                queue.Enqueue((dependent, nextDepth));
            }
        }

        return result
            .OrderBy(item => item.Depth)
            .ThenBy(item => item.Entry.Match.DayLabel, StringComparer.Ordinal)
            .ThenBy(item => item.Entry.Match.StartTime)
            .ThenBy(item => item.Entry.Match.Court, StringComparer.Ordinal)
            .ThenBy(item => item.Entry.Match.MatchName, StringComparer.Ordinal)
            .ToList();
    }

    private static GlobalSchedulePlacement BuildTargetGlobalPlacement(
        CrossEventScheduleBoard board,
        GlobalScheduleEntry entry,
        string dayLabel,
        TimeOnly startTime,
        string court)
    {
        var targetDay = board.Days.FirstOrDefault(day => string.Equals(day.DayLabel, dayLabel, StringComparison.Ordinal))
            ?? throw new DrawValidationException($"找不到比赛日：{dayLabel}");
        if (!targetDay.Courts.Contains(court, StringComparer.OrdinalIgnoreCase))
        {
            throw new DrawValidationException($"{dayLabel} 没有场地 {court}。");
        }

        var endTime = startTime.AddMinutes(entry.Match.DurationMinutes);
        if (startTime < targetDay.StartTime || endTime > targetDay.EndTime)
        {
            throw new DrawValidationException($"{dayLabel} {startTime:HH:mm}-{endTime:HH:mm} 超出可用时间段 {targetDay.TimeRange}。");
        }

        return new GlobalSchedulePlacement(dayLabel, startTime, endTime, court);
    }

    private static int FindMinimumGlobalDependencyStart(
        GlobalScheduleEntry entry,
        IReadOnlyDictionary<string, GlobalSchedulePlacement> placements,
        IReadOnlyDictionary<string, GlobalScheduleEntry> entryLookup,
        IReadOnlyDictionary<string, int> dayNumbers,
        int minimumRestMinutes)
    {
        var minimumStart = int.MinValue;
        foreach (var dependencyKey in entry.DependencyKeys)
        {
            if (!placements.TryGetValue(dependencyKey, out var dependency)
                || !entryLookup.ContainsKey(dependencyKey))
            {
                continue;
            }

            minimumStart = Math.Max(
                minimumStart,
                BuildComparableMinute(dependency.DayLabel, dependency.EndTime, dayNumbers) + minimumRestMinutes);
        }

        return minimumStart == int.MinValue
            ? BuildComparableMinute(entry.Match.DayLabel, entry.Match.StartTime, dayNumbers)
            : minimumStart;
    }

    private static GlobalSchedulePlacement? FindEarliestGlobalCascadePlacement(
        CrossEventScheduleBoard board,
        GlobalScheduleEntry entry,
        IReadOnlyDictionary<string, GlobalSchedulePlacement> placements,
        IReadOnlyDictionary<string, GlobalScheduleEntry> entryLookup,
        IReadOnlyDictionary<string, int> dayNumbers,
        int minimumComparableStart)
    {
        foreach (var day in board.Days.OrderBy(day => day.DayLabel, StringComparer.Ordinal))
        {
            foreach (var slot in day.TimeSlots)
            {
                var endTime = slot.AddMinutes(entry.Match.DurationMinutes);
                if (endTime > day.EndTime
                    || BuildComparableMinute(day.DayLabel, slot, dayNumbers) < minimumComparableStart)
                {
                    continue;
                }

                foreach (var court in day.Courts)
                {
                    var placement = new GlobalSchedulePlacement(day.DayLabel, slot, endTime, court);
                    if (IsGlobalPlacementValid(board, entry, placement, placements, entryLookup, dayNumbers, board.SchedulingOptions?.RefereeCount))
                    {
                        return placement;
                    }
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<ScheduleBoardCascadeMovedItem> BuildGlobalCascadeMovedItems(
        IReadOnlyList<GlobalCascadeEntry> cascadeEntries,
        IReadOnlyDictionary<string, GlobalSchedulePlacement> placements)
    {
        var result = new List<ScheduleBoardCascadeMovedItem>();
        foreach (var cascadeEntry in cascadeEntries)
        {
            var entry = cascadeEntry.Entry;
            if (!placements.TryGetValue(entry.Key, out var placement)
                || (string.Equals(entry.Match.DayLabel, placement.DayLabel, StringComparison.Ordinal)
                    && entry.Match.StartTime == placement.StartTime
                    && entry.Match.EndTime == placement.EndTime
                    && string.Equals(entry.Match.Court, placement.Court, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(new ScheduleBoardCascadeMovedItem(
                cascadeEntry.Depth,
                entry.Source.EventName,
                entry.Match.MatchName,
                entry.Match.DayLabel,
                entry.Match.StartTime,
                entry.Match.EndTime,
                entry.Match.Court,
                placement.DayLabel,
                placement.StartTime,
                placement.EndTime,
                placement.Court,
                cascadeEntry.Depth == 0
                    ? "当前场次移动到裁判长选择的位置"
                    : "连锁后移以满足本项目淘汰树依赖、场地、每日上限和兼项休息约束"));
        }

        return result;
    }

    private static IReadOnlyList<ScheduleBoardCrossEventImpactPreviewItem> BuildCrossEventImpactPreviewItems(
        CrossEventScheduleBoard board,
        CrossEventScheduleBoardItem movedItem)
    {
        var movedPlayers = GetScheduleBoardItemPlayers(movedItem)
            .GroupBy(player => player.IdentityKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (movedPlayers.Count == 0)
        {
            return [];
        }

        var dayNumbers = BuildBoardDayNumberLookup(board.Days);
        var movedStart = BuildComparableMinute(movedItem.DayLabel, movedItem.StartTime, dayNumbers);
        var movedEnd = BuildComparableMinute(movedItem.DayLabel, movedItem.EndTime, dayNumbers);
        var impacts = new List<ScheduleBoardCrossEventImpactPreviewItem>();
        foreach (var other in board.Items)
        {
            if (string.Equals(other.Key, movedItem.Key, StringComparison.Ordinal)
                || string.Equals(other.SourceId, movedItem.SourceId, StringComparison.Ordinal))
            {
                continue;
            }

            var sharedPlayers = GetScheduleBoardItemPlayers(other)
                .Where(player => movedPlayers.ContainsKey(player.IdentityKey))
                .Select(player => movedPlayers[player.IdentityKey])
                .GroupBy(player => player.IdentityKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (sharedPlayers.Count == 0)
            {
                continue;
            }

            var otherStart = BuildComparableMinute(other.DayLabel, other.StartTime, dayNumbers);
            var otherEnd = BuildComparableMinute(other.DayLabel, other.EndTime, dayNumbers);
            var overlap = movedStart < otherEnd && otherStart < movedEnd;
            var restMinutes = overlap
                ? (int?)null
                : movedEnd <= otherStart
                    ? otherStart - movedEnd
                    : movedStart - otherEnd;
            var sameDay = string.Equals(movedItem.DayLabel, other.DayLabel, StringComparison.Ordinal);
            CrossEventConflictSeverity? severity = overlap
                ? CrossEventConflictSeverity.Severe
                : restMinutes < board.MinimumRestMinutes
                    ? CrossEventConflictSeverity.Warning
                    : sameDay
                        ? CrossEventConflictSeverity.Notice
                        : null;
            if (severity is null)
            {
                continue;
            }

            foreach (var player in sharedPlayers)
            {
                var detail = severity switch
                {
                    CrossEventConflictSeverity.Severe => "移动后该选手在两个项目中比赛时间重叠。",
                    CrossEventConflictSeverity.Warning => $"移动后与该选手另一项目比赛间隔 {restMinutes} 分钟，低于最小休息间隔 {board.MinimumRestMinutes} 分钟。",
                    _ => $"移动后该选手同日还有另一项目比赛，间隔 {restMinutes} 分钟，建议人工确认体能安排。"
                };
                impacts.Add(new ScheduleBoardCrossEventImpactPreviewItem(
                    severity.Value,
                    player.DisplayName,
                    other.EventName,
                    other.MatchName,
                    other.DayLabel,
                    other.StartTime,
                    other.EndTime,
                    other.Court,
                    other.Phase,
                    restMinutes,
                    detail,
                    other.IsCompleted));
            }
        }

        return impacts
            .OrderBy(item => SeverityOrder(item.Severity))
            .ThenBy(item => item.DayLabel, StringComparer.Ordinal)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.EventName, StringComparer.Ordinal)
            .ThenBy(item => item.MatchName, StringComparer.Ordinal)
            .ThenBy(item => item.PlayerName, StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildCrossEventImpactPreviewNote(CrossEventScheduleBoardItem movedItem)
    {
        var knownPlayerCount = GetScheduleBoardItemPlayers(movedItem)
            .Select(player => player.IdentityKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (knownPlayerCount == 0)
        {
            return "当前场次仍含未决占位或缺少可识别选手身份，跨项目兼项影响会在胜方/负方确定后更准确。";
        }

        if (movedItem.SideAPlayerIdentities.Count == 0 || movedItem.SideBPlayerIdentities.Count == 0)
        {
            return "当前场次仍有一侧未决出具体选手；这里仅检查已确定选手的跨项目影响，不展开所有理论晋级可能。";
        }

        return "";
    }

    private static IEnumerable<CrossEventPlayerIdentity> GetScheduleBoardItemPlayers(CrossEventScheduleBoardItem item)
    {
        return item.SideAPlayerIdentities.Concat(item.SideBPlayerIdentities)
            .Where(player => !string.IsNullOrWhiteSpace(player.IdentityKey));
    }

    private static int BuildComparableMinute(
        string dayLabel,
        TimeOnly time,
        IReadOnlyDictionary<string, int> dayNumbers)
    {
        var dayNumber = dayNumbers.TryGetValue(dayLabel, out var value) ? value : 0;
        return (dayNumber * 24 * 60) + (time.Hour * 60) + time.Minute;
    }

    private static bool HasMinimumRest(
        int firstStart,
        int firstEnd,
        int secondStart,
        int secondEnd,
        int minimumRestMinutes)
    {
        if (firstEnd <= secondStart)
        {
            return secondStart - firstEnd >= minimumRestMinutes;
        }

        if (secondEnd <= firstStart)
        {
            return firstStart - secondEnd >= minimumRestMinutes;
        }

        return false;
    }

    private static bool TimeRangesOverlap(TimeOnly firstStart, TimeOnly firstEnd, TimeOnly secondStart, TimeOnly secondEnd)
    {
        return firstStart < secondEnd && secondStart < firstEnd;
    }

    private static bool SharesPlayer(GlobalScheduleEntry first, GlobalScheduleEntry second)
    {
        return first.PlayerKeys.Count > 0
            && second.PlayerKeys.Count > 0
            && first.PlayerKeys.Intersect(second.PlayerKeys, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static IReadOnlyList<string> NormalizePlayerKeys(IEnumerable<CrossEventPlayerIdentity> players)
    {
        return players
            .Select(player => player.IdentityKey)
            .Where(player => !string.IsNullOrWhiteSpace(player))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }


    private static CrossEventScheduleSource MoveMatchInSource(
        CrossEventScheduleSource source,
        string itemKey,
        string dayLabel,
        TimeOnly startTime,
        TimeOnly endTime,
        string court)
    {
        var changed = false;
        var matches = source.Matches
            .Select(match =>
            {
                if (!string.Equals(BuildItemKey(source.SourceId, match.MatchName), itemKey, StringComparison.Ordinal))
                {
                    return match;
                }

                changed = true;
                return match with
                {
                    DayLabel = dayLabel,
                    StartTime = startTime,
                    EndTime = endTime,
                    Court = court
                };
            })
            .ToList();
        return changed ? source with { Matches = NormalizeMatchOrders(matches) } : source;
    }

    private static IReadOnlyList<CrossEventScheduledMatch> NormalizeMatchOrders(IReadOnlyList<CrossEventScheduledMatch> matches)
    {
        return matches
            .OrderBy(match => match.DayLabel, StringComparer.Ordinal)
            .ThenBy(match => match.StartTime)
            .ThenBy(match => match.Court, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Order)
            .Select((match, index) => match with { Order = index + 1 })
            .ToList();
    }

    private static SchedulePlan BuildSchedulePlan(CrossEventScheduleSource source)
    {
        var matches = NormalizeMatchOrders(source.Matches)
            .Select(match => new ScheduledMatch(
                match.Order,
                match.DayLabel,
                match.StartTime,
                match.EndTime,
                match.Court,
                match.GroupNumber,
                match.GroupName,
                match.Phase,
                match.MatchName,
                match.SideA,
                match.SideB,
                match.Note,
                match.SameUnit,
                match.MatchId,
                match.Dependencies,
                match.SideAPlayerIdentities,
                match.SideBPlayerIdentities))
            .ToList();
        return new SchedulePlan(matches, BuildScheduleSettings(source.ScheduleSettings, matches));
    }
}
