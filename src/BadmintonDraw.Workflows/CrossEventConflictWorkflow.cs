using System.Text.RegularExpressions;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows;

public sealed class CrossEventConflictWorkflow
{
    private const char ItemKeySeparator = '\u001F';

    private readonly TournamentProgressStore _progressStore = new();
    private readonly CrossEventConflictDetector _detector = new();
    private readonly CrossEventConflictReportExcelWriter _writer = new();
    private readonly ScheduleWorkflow _scheduleWorkflow = new();

    public CrossEventConflictReport AnalyzeProgressFiles(
        IEnumerable<string> progressFilePaths,
        int minimumRestMinutes)
    {
        var paths = NormalizeProgressPaths(progressFilePaths);
        var sources = paths.Select(ReadSource).ToList();
        return _detector.Analyze(sources, minimumRestMinutes);
    }

    public CrossEventConflictExportResult ExportProgressReport(
        IEnumerable<string> progressFilePaths,
        string outputPath,
        int minimumRestMinutes)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new DrawValidationException("请选择跨项目冲突报告保存位置。");
        }

        var report = AnalyzeProgressFiles(progressFilePaths, minimumRestMinutes);
        _writer.Write(outputPath, report);
        return new CrossEventConflictExportResult(outputPath, report);
    }

    public CrossEventConflictExportResult ExportScheduleBoardReport(
        CrossEventScheduleBoard board,
        string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new DrawValidationException("请选择跨项目冲突报告保存位置。");
        }

        _writer.Write(outputPath, board.Report);
        return new CrossEventConflictExportResult(outputPath, board.Report);
    }

    public CrossEventScheduleBoard LoadScheduleBoard(
        IEnumerable<string> progressFilePaths,
        int minimumRestMinutes)
    {
        var paths = NormalizeProgressPaths(progressFilePaths);
        var sources = paths.Select(ReadSource).ToList();
        return BuildScheduleBoard(sources, minimumRestMinutes, hasUnsavedChanges: false);
    }

    public CrossEventScheduleBoard MoveScheduleItem(
        CrossEventScheduleBoard board,
        string itemKey,
        string dayLabel,
        TimeOnly startTime,
        string court)
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

        var sources = board.Sources
            .Select(source => MoveMatchInSource(source, itemKey, dayLabel, startTime, endTime, court))
            .ToList();
        foreach (var source in sources)
        {
            ScheduleDependencyGraph.Build(BuildSchedulePlan(source)).EnsureDependencyOrder();
        }

        return BuildScheduleBoard(sources, board.MinimumRestMinutes, hasUnsavedChanges: true);
    }

    public CrossEventScheduleAutoAdjustResult AutoAdjustScheduleBoard(CrossEventScheduleBoard board)
    {
        var originalPlacements = board.Items.ToDictionary(item => item.Key, item => item, StringComparer.Ordinal);
        var entries = BuildGlobalScheduleEntries(board);
        var entryLookup = entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var dayNumbers = BuildBoardDayNumberLookup(board.Days);
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
        var messages = new List<string>();

        foreach (var entry in BuildGlobalScheduleOrder(entries))
        {
            if (entry.Match.IsCompleted)
            {
                continue;
            }

            var placement = FindBestGlobalPlacement(board, entry, placements, entryLookup, dayNumbers);
            if (placement is null)
            {
                placements[entry.Key] = new GlobalSchedulePlacement(
                    entry.Match.DayLabel,
                    entry.Match.StartTime,
                    entry.Match.EndTime,
                    entry.Match.Court);
                messages.Add($"{entry.Source.EventName} {entry.Match.MatchName} 未找到满足依赖、场地和休息约束的全局位置，已保留原位置。");
                continue;
            }

            placements[entry.Key] = placement;
        }

        var sources = ApplyGlobalPlacements(board.Sources, placements);
        var working = BuildScheduleBoard(sources, board.MinimumRestMinutes, hasUnsavedChanges: true);
        var movedCount = working.Items.Count(item =>
            originalPlacements.TryGetValue(item.Key, out var original)
            && !item.IsCompleted
            && (!string.Equals(item.DayLabel, original.DayLabel, StringComparison.Ordinal)
                || item.StartTime != original.StartTime
                || !string.Equals(item.Court, original.Court, StringComparison.Ordinal)));

        return new CrossEventScheduleAutoAdjustResult(
            working,
            movedCount,
            working.BlockingConflictItemCount,
            messages);
    }

    public CrossEventScheduleSaveResult SaveScheduleBoard(CrossEventScheduleBoard board)
    {
        var updatedPaths = new List<string>();
        var backupPaths = new List<string>();
        foreach (var source in board.Sources)
        {
            var schedule = BuildSchedulePlan(source);
            var outcome = _progressStore.UpdateSchedule(source.SourcePath, schedule);
            updatedPaths.Add(source.SourcePath);
            if (!string.IsNullOrWhiteSpace(outcome.BackupPath))
            {
                backupPaths.Add(outcome.BackupPath!);
            }
        }

        return new CrossEventScheduleSaveResult(updatedPaths, backupPaths);
    }

    public CrossEventMergedMaterialsExportResult ExportMergedScheduleMaterials(
        CrossEventScheduleBoard board,
        string outputDirectory)
    {
        if (board.Report.SevereCount > 0)
        {
            throw new DrawValidationException("多项目赛程仍有严重冲突，请先调整到严重冲突为 0 后再导出合并材料。");
        }

        if (board.Items.Count == 0)
        {
            throw new DrawValidationException("当前多项目赛程没有可导出的比赛。");
        }

        var schedule = BuildMergedSchedulePlan(board);
        var dayLabels = schedule.Matches
            .Select(match => match.DayLabel)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(day => day, StringComparer.Ordinal)
            .ToList();
        var packageDirectory = WorkflowExportHelpers.CreateUniqueDirectory(
            outputDirectory,
            BuildMergedPackageFolderName(dayLabels));
        var outputPaths = new List<string>();
        foreach (var dayLabel in dayLabels)
        {
            var recordPath = Path.Combine(packageDirectory, BuildDefaultMergedMatchRecordFileName(dayLabel));
            _scheduleWorkflow.ExportMatchRecord(recordPath, schedule, dayLabel);
            outputPaths.Add(recordPath);

            var schedulePath = Path.Combine(packageDirectory, BuildDefaultMergedDailyScheduleFileName(dayLabel));
            outputPaths.AddRange(_scheduleWorkflow.ExportDailyScheduleFiles(
                schedulePath,
                WorkflowExportFormat.Excel,
                schedule,
                dayLabel));
            outputPaths.AddRange(_scheduleWorkflow.ExportDailyScheduleFiles(
                Path.ChangeExtension(schedulePath, WorkflowExportHelpers.GetExtension(WorkflowExportFormat.A4Pdf)),
                WorkflowExportFormat.A4Pdf,
                schedule,
                dayLabel));
        }

        return new CrossEventMergedMaterialsExportResult(packageDirectory, outputPaths, schedule, dayLabels);
    }

    public static SchedulePlan BuildMergedSchedulePlan(CrossEventScheduleBoard board)
    {
        var nameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var mergedItems = board.Items
            .OrderBy(item => item.DayLabel, StringComparer.Ordinal)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.Court, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.EventName, StringComparer.Ordinal)
            .ThenBy(item => item.Order)
            .Select(item => (Item: item, MergedMatchName: BuildMergedMatchName(item, nameCounts)))
            .ToList();
        var mergedIdBySourceMatchId = mergedItems.ToDictionary(
            item => BuildSourceMatchIdKey(item.Item.SourceId, item.Item.MatchId),
            item => BuildMergedMatchId(item.Item.SourceId, item.Item.MatchId),
            StringComparer.Ordinal);
        var mergedNameBySourceMatchId = mergedItems.ToDictionary(
            item => BuildSourceMatchIdKey(item.Item.SourceId, item.Item.MatchId),
            item => item.MergedMatchName,
            StringComparer.Ordinal);
        var matches = mergedItems
            .Select((item, index) =>
            {
                var boardItem = item.Item;
                var mergedDependencies = RewriteMergedDependencies(
                    boardItem.Dependencies,
                    boardItem.SourceId,
                    mergedIdBySourceMatchId,
                    mergedNameBySourceMatchId);
                var groupName = string.IsNullOrWhiteSpace(boardItem.GroupName)
                    ? boardItem.EventName
                    : $"{boardItem.EventName} {boardItem.GroupName}";
                var note = string.IsNullOrWhiteSpace(boardItem.Note)
                    ? $"来源：{boardItem.EventName}"
                    : $"{boardItem.Note}；来源：{boardItem.EventName}";
                return new ScheduledMatch(
                    index + 1,
                    boardItem.DayLabel,
                    boardItem.StartTime,
                    boardItem.EndTime,
                    boardItem.Court,
                    index + 1,
                    groupName,
                    boardItem.Phase,
                    item.MergedMatchName,
                    RewriteMergedSide(boardItem.SideA, mergedDependencies, ScheduleMatchSide.SideA),
                    RewriteMergedSide(boardItem.SideB, mergedDependencies, ScheduleMatchSide.SideB),
                    note,
                    false,
                    BuildMergedMatchId(boardItem.SourceId, boardItem.MatchId),
                    mergedDependencies);
            })
            .ToList();
        return new SchedulePlan(matches, BuildMergedScheduleSettings(board, matches));
    }

    public static string BuildDefaultReportFileName()
    {
        return $"跨项目选手冲突报告_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
    }

    private static string BuildDefaultMergedMatchRecordFileName(string dayLabel)
    {
        return $"{WorkflowFileNames.Sanitize(BuildDayFileNameStem(dayLabel, "合并赛程记录表"))}.xlsx";
    }

    private static string BuildDefaultMergedDailyScheduleFileName(string dayLabel)
    {
        return $"{WorkflowFileNames.Sanitize(BuildDayFileNameStem(dayLabel, "合并赛程安排表"))}.xlsx";
    }

    private static string BuildMergedPackageFolderName(IReadOnlyList<string> dayLabels)
    {
        if (dayLabels.Count == 0)
        {
            return $"多项目合并材料包_{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        var dayPart = dayLabels.Count == 1
            ? BuildDayFileNameStem(dayLabels[0], "")
            : $"{BuildDayFileNameStem(dayLabels.First(), "")}-{BuildDayFileNameStem(dayLabels.Last(), "")}";
        return $"{dayPart}多项目合并材料包";
    }

    private static string BuildDayFileNameStem(string dayLabel, string suffix)
    {
        return DateOnly.TryParse(dayLabel, out var date)
            ? $"{date.Month}月{date.Day}日{suffix}"
            : $"{dayLabel}{suffix}";
    }

    private static IReadOnlyList<string> NormalizeProgressPaths(IEnumerable<string> progressFilePaths)
    {
        var paths = progressFilePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count < 2)
        {
            throw new DrawValidationException("请至少选择两个赛事存档用于跨项目冲突检查。");
        }

        return paths;
    }

    private CrossEventScheduleSource ReadSource(string path)
    {
        var state = _progressStore.Read(path);
        var snapshot = state.Snapshot;
        var eventKind = snapshot.DrawResult.Settings.EventKind;
        var playerLookup = BuildPlayerLookup(snapshot.Participants, eventKind);
        var unresolvedSideCount = 0;
        var matches = snapshot.Schedule.Matches
            .Select(match =>
            {
                var sideA = ResolveSide(match.SideA, state.Results, out var sideAResolved);
                var sideB = ResolveSide(match.SideB, state.Results, out var sideBResolved);
                var sideAPlayers = ResolvePlayers(sideA, eventKind, playerLookup);
                var sideBPlayers = ResolvePlayers(sideB, eventKind, playerLookup);

                if (!sideAResolved && sideAPlayers.Count == 0)
                {
                    unresolvedSideCount++;
                }

                if (!sideBResolved && sideBPlayers.Count == 0)
                {
                    unresolvedSideCount++;
                }

                return new CrossEventScheduledMatch(
                    match.Order,
                    match.DayLabel,
                    match.StartTime,
                    match.EndTime,
                    match.Court,
                    match.GroupName,
                    match.Phase,
                    match.MatchName,
                    sideA ?? match.SideA,
                    sideB ?? match.SideB,
                    sideAPlayers,
                    sideBPlayers,
                    match.GroupNumber,
                    match.Note,
                    match.SameUnit,
                    state.Results.ContainsKey(match.MatchName),
                    match.MatchId,
                    match.Dependencies);
            })
            .ToList();

        return new CrossEventScheduleSource(
            path,
            string.IsNullOrWhiteSpace(snapshot.EventName) ? Path.GetFileNameWithoutExtension(path) : snapshot.EventName,
            path,
            eventKind,
            matches,
            unresolvedSideCount,
            snapshot.Schedule.Settings);
    }

    private CrossEventScheduleBoard BuildScheduleBoard(
        IReadOnlyList<CrossEventScheduleSource> sources,
        int minimumRestMinutes,
        bool hasUnsavedChanges)
    {
        var report = _detector.Analyze(sources, minimumRestMinutes);
        var conflicts = BuildBoardConflicts(sources, report);
        var days = BuildBoardDays(sources);
        var items = sources
            .SelectMany(source => source.Matches.Select(match =>
            {
                var key = BuildItemKey(source.SourceId, match.MatchName);
                var hasConflict = conflicts.TryGetValue(key, out var conflict);
                return new CrossEventScheduleBoardItem(
                    key,
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
                    match.SideA,
                    match.SideB,
                    match.Note,
                    match.DurationMinutes,
                    match.IsCompleted,
                    hasConflict ? conflict!.Severity : null,
                    hasConflict ? string.Join("；", conflict!.Messages.Distinct(StringComparer.Ordinal)) : "",
                    match.MatchId,
                    match.Dependencies);
            }))
            .OrderBy(item => item.DayLabel, StringComparer.Ordinal)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.Court, StringComparer.Ordinal)
            .ThenBy(item => item.EventName, StringComparer.Ordinal)
            .ThenBy(item => item.Order)
            .ToList();
        var playerDetails = BuildPlayerDetails(sources, items, report);
        return new CrossEventScheduleBoard(sources, days, items, playerDetails, report, minimumRestMinutes, hasUnsavedChanges);
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
                foreach (var player in match.SideAPlayers)
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

                foreach (var player in match.SideBPlayers)
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
        string playerName,
        string side,
        string sideText,
        string opponentText,
        IReadOnlyDictionary<string, CrossEventScheduleBoardItem> itemLookup,
        IReadOnlyDictionary<string, BoardConflictAccumulator> issueLookup,
        ISet<string> seen)
    {
        var normalized = NormalizePlayerName(playerName);
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
            playerName.Trim(),
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

    private static Dictionary<string, BoardConflictAccumulator> BuildBoardConflicts(
        IReadOnlyList<CrossEventScheduleSource> sources,
        CrossEventConflictReport report)
    {
        var conflicts = new Dictionary<string, BoardConflictAccumulator>(StringComparer.Ordinal);
        foreach (var issue in report.Issues.Where(issue => issue.Severity != CrossEventConflictSeverity.Notice))
        {
            var firstKey = BuildItemKey(issue.FirstMatch.SourceId, issue.FirstMatch.MatchName);
            var secondKey = BuildItemKey(issue.SecondMatch.SourceId, issue.SecondMatch.MatchName);
            AddConflict(conflicts, firstKey, issue.Severity, $"{issue.PlayerName}：{issue.Detail}");
            AddConflict(conflicts, secondKey, issue.Severity, $"{issue.PlayerName}：{issue.Detail}");
        }

        foreach (var courtGroup in sources
                     .SelectMany(source => source.Matches.Select(match => (Source: source, Match: match)))
                     .GroupBy(item => (item.Match.DayLabel, item.Match.Court)))
        {
            var matches = courtGroup
                .OrderBy(item => item.Match.StartTime)
                .ThenBy(item => item.Source.EventName, StringComparer.Ordinal)
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

                    var detail = $"{courtGroup.Key.DayLabel} {courtGroup.Key.Court} 同一场地时间重叠。";
                    AddConflict(conflicts, BuildItemKey(first.Source.SourceId, first.Match.MatchName), CrossEventConflictSeverity.Severe, detail);
                    AddConflict(conflicts, BuildItemKey(second.Source.SourceId, second.Match.MatchName), CrossEventConflictSeverity.Severe, detail);
                }
            }
        }

        return conflicts;
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

    private static IReadOnlyList<CrossEventScheduleBoardDay> BuildBoardDays(IReadOnlyList<CrossEventScheduleSource> sources)
    {
        var dayBuilders = new Dictionary<string, BoardDayBuilder>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var day in source.ScheduleSettings?.Days ?? [])
            {
                var builder = GetDayBuilder(dayBuilders, day.DayLabel);
                builder.StartTime = MinTime(builder.StartTime, day.DayStart);
                builder.EndTime = MaxTime(builder.EndTime, day.DayEnd);
                foreach (var court in day.Courts)
                {
                    builder.Courts.Add(court);
                }
            }

            foreach (var match in source.Matches)
            {
                var builder = GetDayBuilder(dayBuilders, match.DayLabel);
                builder.StartTime = MinTime(builder.StartTime, match.StartTime);
                builder.EndTime = MaxTime(builder.EndTime, match.EndTime);
                builder.Courts.Add(match.Court);
                builder.Durations.Add(match.DurationMinutes);
            }
        }

        return dayBuilders
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var start = pair.Value.StartTime ?? new TimeOnly(8, 0);
                var end = pair.Value.EndTime ?? new TimeOnly(22, 0);
                var slotMinutes = NormalizeSlotMinutes(pair.Value.Durations);
                return new CrossEventScheduleBoardDay(
                    pair.Key,
                    start,
                    end,
                    pair.Value.Courts.OrderBy(court => court, StringComparer.OrdinalIgnoreCase).ToList(),
                    slotMinutes,
                    BuildTimeSlots(start, end, slotMinutes));
            })
            .ToList();
    }

    private static IReadOnlyList<GlobalScheduleEntry> BuildGlobalScheduleEntries(CrossEventScheduleBoard board)
    {
        var entries = board.Sources
            .SelectMany(source => source.Matches.Select(match => new GlobalScheduleEntry(
                source,
                match,
                BuildItemKey(source.SourceId, match.MatchName),
                NormalizePlayerKeys(match.SideAPlayers.Concat(match.SideBPlayers)))))
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
        }

        return entries;
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
                .OrderBy(entry => entry.Match.DayLabel, StringComparer.Ordinal)
                .ThenBy(entry => entry.Match.StartTime)
                .ThenBy(entry => IsImportantMatch(entry) ? 1 : 0)
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
                .ThenBy(entry => entry.Source.EventName, StringComparer.Ordinal)
                .ThenBy(entry => entry.Match.Order));
        }

        return result;
    }

    private static GlobalSchedulePlacement? FindBestGlobalPlacement(
        CrossEventScheduleBoard board,
        GlobalScheduleEntry entry,
        IReadOnlyDictionary<string, GlobalSchedulePlacement> placements,
        IReadOnlyDictionary<string, GlobalScheduleEntry> entryLookup,
        IReadOnlyDictionary<string, int> dayNumbers)
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
                    if (!IsGlobalPlacementValid(board, entry, placement, placements, entryLookup, dayNumbers))
                    {
                        continue;
                    }

                    var score = ScoreGlobalPlacement(entry, placement, dayNumbers);
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
        IReadOnlyDictionary<string, int> dayNumbers)
    {
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
        IReadOnlyDictionary<string, int> dayNumbers)
    {
        var originalStart = BuildComparableMinute(entry.Match.DayLabel, entry.Match.StartTime, dayNumbers);
        var candidateStart = BuildComparableMinute(placement.DayLabel, placement.StartTime, dayNumbers);
        var originalDay = dayNumbers.TryGetValue(entry.Match.DayLabel, out var sourceDay) ? sourceDay : 0;
        var candidateDay = dayNumbers.TryGetValue(placement.DayLabel, out var targetDay) ? targetDay : originalDay;
        var score = (long)Math.Abs(candidateStart - originalStart) * 10;
        score += Math.Abs(candidateDay - originalDay) * 10_000L;
        if (!string.Equals(entry.Match.Court, placement.Court, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

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

    private static IReadOnlyList<string> NormalizePlayerKeys(IEnumerable<string> players)
    {
        return players
            .Select(NormalizePlayerName)
            .Where(player => !string.IsNullOrWhiteSpace(player))
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
                match.Dependencies))
            .ToList();
        return new SchedulePlan(matches, BuildScheduleSettings(source.ScheduleSettings, matches));
    }

    private static ScheduleSettings BuildMergedScheduleSettings(
        CrossEventScheduleBoard board,
        IReadOnlyList<ScheduledMatch> matches)
    {
        var days = board.Days
            .OrderBy(day => day.DayLabel, StringComparer.Ordinal)
            .Select(day => new ScheduleDaySettings(
                DateOnly.TryParse(day.DayLabel, out var date) ? date : DateOnly.FromDateTime(DateTime.Today),
                day.StartTime,
                day.EndTime,
                day.Courts))
            .ToList();
        var matchMinutes = matches
            .Select(match => match.DurationMinutes)
            .DefaultIfEmpty(20)
            .Min();
        return new ScheduleSettings(days, matchMinutes, MaxMatchesPerEntrantPerDay: 2);
    }

    private static string BuildMergedMatchName(
        CrossEventScheduleBoardItem item,
        IDictionary<string, int> nameCounts)
    {
        var baseName = $"{item.EventName} · {item.MatchName}";
        nameCounts.TryGetValue(baseName, out var count);
        count++;
        nameCounts[baseName] = count;
        return count == 1 ? baseName : $"{baseName} #{count}";
    }

    private static string RewriteMergedSide(
        string side,
        IReadOnlyList<ScheduleMatchDependency> dependencies,
        ScheduleMatchSide targetSide)
    {
        var dependency = dependencies.FirstOrDefault(item => item.TargetSide == targetSide);
        return dependency is null
            ? side
            : $"{dependency.SourceMatchName}{ScheduleDependencyGraph.FormatOutcome(dependency.Outcome)}";
    }

    private static IReadOnlyList<ScheduleMatchDependency> RewriteMergedDependencies(
        IReadOnlyList<ScheduleMatchDependency> dependencies,
        string sourceId,
        IReadOnlyDictionary<string, string> mergedIdBySourceMatchId,
        IReadOnlyDictionary<string, string> mergedNameBySourceMatchId)
    {
        return dependencies
            .Select(dependency =>
            {
                var sourceKey = BuildSourceMatchIdKey(sourceId, dependency.SourceMatchId);
                return dependency with
                {
                    SourceMatchId = mergedIdBySourceMatchId.TryGetValue(sourceKey, out var mergedId)
                        ? mergedId
                        : BuildMergedMatchId(sourceId, dependency.SourceMatchId),
                    SourceMatchName = mergedNameBySourceMatchId.TryGetValue(sourceKey, out var mergedName)
                        ? mergedName
                        : dependency.SourceMatchName
                };
            })
            .ToList();
    }

    private static ScheduleSettings BuildScheduleSettings(
        ScheduleSettings? currentSettings,
        IReadOnlyList<ScheduledMatch> matches)
    {
        var dayBuilders = new Dictionary<string, BoardDayBuilder>(StringComparer.Ordinal);
        foreach (var day in currentSettings?.Days ?? [])
        {
            var builder = GetDayBuilder(dayBuilders, day.DayLabel);
            builder.StartTime = MinTime(builder.StartTime, day.DayStart);
            builder.EndTime = MaxTime(builder.EndTime, day.DayEnd);
            foreach (var court in day.Courts)
            {
                builder.Courts.Add(court);
            }
        }

        foreach (var match in matches)
        {
            var builder = GetDayBuilder(dayBuilders, match.DayLabel);
            builder.StartTime = MinTime(builder.StartTime, match.StartTime);
            builder.EndTime = MaxTime(builder.EndTime, match.EndTime);
            builder.Courts.Add(match.Court);
        }

        var days = dayBuilders
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ScheduleDaySettings(
                DateOnly.Parse(pair.Key),
                pair.Value.StartTime ?? new TimeOnly(8, 0),
                pair.Value.EndTime ?? new TimeOnly(22, 0),
                pair.Value.Courts.OrderBy(court => court, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
        var matchMinutes = currentSettings?.MatchMinutes
            ?? matches.Select(match => (int)(match.EndTime - match.StartTime).TotalMinutes).DefaultIfEmpty(20).Min();
        return new ScheduleSettings(
            days,
            matchMinutes,
            currentSettings?.MaxMatchesPerEntrantPerDay ?? 2,
            currentSettings?.KnockoutTimingBoundaryEntrants,
            currentSettings?.BeforeBoundaryTiming);
    }

    private static TimeOnly? MinTime(TimeOnly? current, TimeOnly candidate)
    {
        return current.HasValue && current.Value <= candidate ? current.Value : candidate;
    }

    private static TimeOnly? MaxTime(TimeOnly? current, TimeOnly candidate)
    {
        return current.HasValue && current.Value >= candidate ? current.Value : candidate;
    }

    private static int NormalizeSlotMinutes(IReadOnlyCollection<int> durations)
    {
        if (durations.Count == 0)
        {
            return 20;
        }

        return Math.Clamp(durations.Aggregate(GreatestCommonDivisor), 5, 30);
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        left = Math.Abs(left);
        right = Math.Abs(right);
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        return Math.Max(1, left);
    }

    private static IReadOnlyList<TimeOnly> BuildTimeSlots(TimeOnly startTime, TimeOnly endTime, int slotMinutes)
    {
        var slots = new List<TimeOnly>();
        for (var cursor = startTime; cursor < endTime; cursor = cursor.AddMinutes(slotMinutes))
        {
            slots.Add(cursor);
        }

        return slots;
    }

    private static string BuildItemKey(string sourceId, string matchName)
    {
        return $"{sourceId}{ItemKeySeparator}{matchName}";
    }

    private static string BuildSourceMatchIdKey(string sourceId, string matchId)
    {
        return $"{sourceId}{ItemKeySeparator}{matchId}";
    }

    private static string BuildMergedMatchId(string sourceId, string matchId)
    {
        return BuildSourceMatchIdKey(sourceId, matchId);
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

    private static Dictionary<string, IReadOnlyList<string>> BuildPlayerLookup(
        IEnumerable<DrawParticipant> participants,
        EventKind eventKind)
    {
        var lookup = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var participant in participants)
        {
            var players = GetParticipantPlayers(participant, eventKind);
            AddLookup(lookup, participant.DisplayName, players);
            AddLookup(lookup, CleanCompetitorText(participant.DisplayName), players);
        }

        return lookup;
    }

    private static IReadOnlyList<string> GetParticipantPlayers(DrawParticipant participant, EventKind eventKind)
    {
        if (eventKind == EventKind.Doubles)
        {
            var players = new[] { participant.PrimaryName, participant.PartnerName }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return players.Count > 0
                ? players
                : SplitCompetitorText(participant.DisplayName, eventKind);
        }

        var name = eventKind == EventKind.Singles && !string.IsNullOrWhiteSpace(participant.PrimaryName)
            ? participant.PrimaryName!
            : participant.DisplayName;
        return [name.Trim()];
    }

    private static void AddLookup(
        IDictionary<string, IReadOnlyList<string>> lookup,
        string value,
        IReadOnlyList<string> players)
    {
        var key = NormalizeCompetitorKey(value);
        if (!string.IsNullOrWhiteSpace(key) && !lookup.ContainsKey(key))
        {
            lookup.Add(key, players);
        }
    }

    private static IReadOnlyList<string> ResolvePlayers(
        string? side,
        EventKind eventKind,
        IReadOnlyDictionary<string, IReadOnlyList<string>> playerLookup)
    {
        if (string.IsNullOrWhiteSpace(side))
        {
            return [];
        }

        var key = NormalizeCompetitorKey(side);
        if (playerLookup.TryGetValue(key, out var players))
        {
            return players;
        }

        return SplitCompetitorText(side, eventKind);
    }

    private static string? ResolveSide(
        string side,
        IReadOnlyDictionary<string, MatchRecordResult> results,
        out bool isKnown)
    {
        if (side.EndsWith("胜者", StringComparison.Ordinal))
        {
            var sourceMatchName = side[..^"胜者".Length];
            if (results.TryGetValue(sourceMatchName, out var result))
            {
                isKnown = true;
                return result.Winner;
            }

            isKnown = false;
            return null;
        }

        if (side.EndsWith("负者", StringComparison.Ordinal))
        {
            var sourceMatchName = side[..^"负者".Length];
            if (results.TryGetValue(sourceMatchName, out var result))
            {
                isKnown = true;
                return result.Loser;
            }

            isKnown = false;
            return null;
        }

        isKnown = true;
        return side;
    }

    private static IReadOnlyList<string> SplitCompetitorText(string value, EventKind eventKind)
    {
        var cleaned = CleanCompetitorText(value);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return [];
        }

        if (eventKind != EventKind.Doubles)
        {
            return [cleaned];
        }

        var parts = Regex.Split(cleaned, @"[\s,，、/／&]+")
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return parts.Count > 0 ? parts : [cleaned];
    }

    private static string NormalizeCompetitorKey(string value)
    {
        return string.Concat(CleanCompetitorText(value).Where(character => !char.IsWhiteSpace(character)));
    }

    private static string NormalizePlayerName(string value)
    {
        return string.Concat(value.Trim().Where(character => !char.IsWhiteSpace(character)));
    }

    private static string BuildPlayerIssueKey(string normalizedPlayerName, string itemKey)
    {
        return $"{normalizedPlayerName}{ItemKeySeparator}{itemKey}";
    }

    private static string CleanCompetitorText(string value)
    {
        var text = Regex.Replace(value.Trim(), @"\s+", " ");
        var optionMatch = Regex.Match(text, @"^[ABab]\s*[【\[](?<name>.+)[】\]]$");
        if (optionMatch.Success)
        {
            text = optionMatch.Groups["name"].Value.Trim();
        }

        if (text.Length >= 2
            && ((text[0] == '[' && text[^1] == ']')
                || (text[0] == '【' && text[^1] == '】')))
        {
            text = text[1..^1].Trim();
        }

        return text;
    }

    private sealed class BoardDayBuilder
    {
        public TimeOnly? StartTime { get; set; }

        public TimeOnly? EndTime { get; set; }

        public HashSet<string> Courts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<int> Durations { get; } = [];
    }

    private sealed class BoardConflictAccumulator(CrossEventConflictSeverity severity)
    {
        public CrossEventConflictSeverity Severity { get; set; } = severity;

        public List<string> Messages { get; } = [];
    }

    private sealed class GlobalScheduleEntry(
        CrossEventScheduleSource source,
        CrossEventScheduledMatch match,
        string key,
        IReadOnlyList<string> playerKeys)
    {
        public CrossEventScheduleSource Source { get; } = source;

        public CrossEventScheduledMatch Match { get; } = match;

        public string Key { get; } = key;

        public IReadOnlyList<string> PlayerKeys { get; } = playerKeys;

        public List<string> DependencyKeys { get; } = [];

        public List<string> DependentKeys { get; } = [];
    }

    private sealed record GlobalSchedulePlacement(
        string DayLabel,
        TimeOnly StartTime,
        TimeOnly EndTime,
        string Court);

    private sealed record PlayerAppearanceBuilder(
        string PlayerName,
        string NormalizedPlayerName,
        CrossEventPlayerScheduleAppearance Appearance);
}

public sealed record CrossEventConflictExportResult(
    string OutputPath,
    CrossEventConflictReport Report);

public sealed record CrossEventMergedMaterialsExportResult(
    string OutputDirectory,
    IReadOnlyList<string> OutputPaths,
    SchedulePlan Schedule,
    IReadOnlyList<string> DayLabels);
