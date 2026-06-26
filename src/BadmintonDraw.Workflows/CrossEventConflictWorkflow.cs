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
        return BuildBoardConflictReport(sources, minimumRestMinutes);
    }

    public CrossEventConflictExportResult ExportProgressReport(
        IEnumerable<string> progressFilePaths,
        string outputPath,
        int minimumRestMinutes)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new DrawValidationException("请选择多项目排程检查报告保存位置。");
        }

        var board = LoadScheduleBoard(progressFilePaths, minimumRestMinutes);
        _writer.WriteScheduleAudit(outputPath, board);
        return new CrossEventConflictExportResult(outputPath, board.Report);
    }

    public CrossEventConflictExportResult ExportScheduleBoardReport(
        CrossEventScheduleBoard board,
        string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new DrawValidationException("请选择多项目排程检查报告保存位置。");
        }

        _writer.WriteScheduleAudit(outputPath, board);
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

    public CrossEventScheduleBoard RebuildScheduleBoard(
        CrossEventScheduleBoard board,
        int minimumRestMinutes,
        CrossEventSchedulingOptions? schedulingOptions = null)
    {
        return BuildScheduleBoard(
            board.Sources,
            minimumRestMinutes,
            board.HasUnsavedChanges,
            schedulingOptions ?? board.SchedulingOptions);
    }

    public CrossEventSchedulingOptions CreateSchedulingOptions(
        CrossEventScheduleBoard board,
        CrossEventSchedulingStrategy strategy)
    {
        return CreateDefaultSchedulingOptions(board, strategy);
    }

    public static ScheduleBoardView BuildScheduleBoardView(CrossEventScheduleBoard board)
    {
        var days = board.Days
            .Select(day => new ScheduleBoardDay(
                day.DayLabel,
                day.StartTime,
                day.EndTime,
                day.Courts,
                day.SlotMinutes,
                day.TimeSlots))
            .ToList();
        var items = board.Items
            .Select(item => new ScheduleBoardItem(
                item.Key,
                item.Key,
                item.Key,
                item.DayLabel,
                item.StartTime,
                item.EndTime,
                item.Court,
                item.Order,
                item.MatchLabel,
                $"{item.TimeRange} · {item.Status}",
                $"{item.SideA}  vs  {item.SideB}",
                item.IsBlockingConflict ? item.ConflictSummary : "",
                item.ConflictSummary,
                item.IsCompleted,
                item.IsBlockingConflict,
                item.EventName))
            .ToList();

        return new ScheduleBoardView(
            ScheduleBoardKind.CrossEvent,
            days,
            items);
    }

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

    public CrossEventScheduleAutoAdjustResult AutoAdjustScheduleBoard(
        CrossEventScheduleBoard board,
        CrossEventSchedulingOptions? options = null)
    {
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
        var auditPath = Path.Combine(packageDirectory, "多项目排程检查报告.xlsx");
        _writer.WriteScheduleAudit(auditPath, board);
        outputPaths.Add(auditPath);

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

            var scoreSheetPath = Path.Combine(packageDirectory, BuildDefaultMergedScoreSheetFileName(dayLabel));
            _scheduleWorkflow.ExportIndividualScoreSheetPdf(
                scoreSheetPath,
                schedule,
                "多项目合并赛程",
                dayLabel);
            outputPaths.Add(scoreSheetPath);
        }

        var manifestPath = Path.Combine(packageDirectory, "合并材料包说明.txt");
        File.WriteAllLines(
            manifestPath,
            BuildMergedMaterialsManifestLines(board, schedule, dayLabels, outputPaths));
        outputPaths.Add(manifestPath);

        return new CrossEventMergedMaterialsExportResult(packageDirectory, outputPaths, schedule, dayLabels);
    }

    private static IReadOnlyList<string> BuildMergedMaterialsManifestLines(
        CrossEventScheduleBoard board,
        SchedulePlan schedule,
        IReadOnlyList<string> dayLabels,
        IReadOnlyList<string> outputPaths)
    {
        var lines = new List<string>
        {
            "多项目合并材料包",
            $"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"项目数：{board.Sources.Count}",
            $"总场次：{schedule.Matches.Count}",
            $"比赛日：{string.Join("、", dayLabels)}",
            $"排程检查：严重 {board.Report.SevereCount} 条，警告 {board.Report.WarningCount} 条，提醒/推演 {board.Report.NoticeCount} 条。",
            "",
            "每日材料："
        };

        foreach (var dayLabel in dayLabels)
        {
            var matchCount = schedule.Matches.Count(match => string.Equals(match.DayLabel, dayLabel, StringComparison.Ordinal));
            lines.Add($"- {dayLabel}：{matchCount} 场；包含合并赛程记录表、合并赛程安排表 Excel/PDF、单场计分表 PDF。");
        }

        lines.Add("");
        lines.Add("通用材料：");
        lines.Add("- 多项目排程检查报告.xlsx：用于复核严重冲突、警告和提醒/推演。");
        lines.Add("");
        lines.Add("文件清单：");
        foreach (var outputPath in outputPaths.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            lines.Add($"- {Path.GetFileName(outputPath)}");
        }

        lines.Add("- 合并材料包说明.txt");
        return lines;
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
                    mergedDependencies,
                    boardItem.SideAPlayerIdentities,
                    boardItem.SideBPlayerIdentities);
            })
            .ToList();
        return new SchedulePlan(matches, BuildMergedScheduleSettings(board, matches));
    }

    public static string BuildDefaultReportFileName()
    {
        return $"多项目排程检查报告_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
    }

    private static string BuildDefaultMergedMatchRecordFileName(string dayLabel)
    {
        return $"{WorkflowFileNames.Sanitize(BuildDayFileNameStem(dayLabel, "合并赛程记录表"))}.xlsx";
    }

    private static string BuildDefaultMergedDailyScheduleFileName(string dayLabel)
    {
        return $"{WorkflowFileNames.Sanitize(BuildDayFileNameStem(dayLabel, "合并赛程安排表"))}.xlsx";
    }

    private static string BuildDefaultMergedScoreSheetFileName(string dayLabel)
    {
        return $"{WorkflowFileNames.Sanitize(BuildDayFileNameStem(dayLabel, "合并单场比赛计分表"))}.pdf";
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
                var sideAPlayerIdentities = ResolvePlayerIdentities(sideA, eventKind, playerLookup);
                var sideBPlayerIdentities = ResolvePlayerIdentities(sideB, eventKind, playerLookup);
                var sideAPlayers = sideAPlayerIdentities.Select(identity => identity.Name).ToList();
                var sideBPlayers = sideBPlayerIdentities.Select(identity => identity.Name).ToList();

                if (!sideAResolved && sideAPlayerIdentities.Count == 0)
                {
                    unresolvedSideCount++;
                }

                if (!sideBResolved && sideBPlayerIdentities.Count == 0)
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
                    match.Dependencies,
                    sideAPlayerIdentities,
                    sideBPlayerIdentities);
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
        bool hasUnsavedChanges,
        CrossEventSchedulingOptions? schedulingOptions = null)
    {
        var report = BuildBoardConflictReport(sources, minimumRestMinutes);
        var conflicts = BuildBoardConflicts(report);
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
                    match.Dependencies,
                    match.SideAPlayerIdentities,
                    match.SideBPlayerIdentities);
            }))
            .OrderBy(item => item.DayLabel, StringComparer.Ordinal)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.Court, StringComparer.Ordinal)
            .ThenBy(item => item.EventName, StringComparer.Ordinal)
            .ThenBy(item => item.Order)
            .ToList();
        var playerDetails = BuildPlayerDetails(sources, items, report);
        var board = new CrossEventScheduleBoard(
            sources,
            days,
            items,
            playerDetails,
            report,
            minimumRestMinutes,
            hasUnsavedChanges,
            schedulingOptions);
        return board with
        {
            QualityReport = BuildCrossEventQualityReport(
                board,
                movedCount: 0,
                messages: Array.Empty<string>())
        };
    }

    private CrossEventConflictReport BuildBoardConflictReport(
        IReadOnlyList<CrossEventScheduleSource> sources,
        int minimumRestMinutes)
    {
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

                builder.RefereeCapacityWindows.AddRange(day.RefereeCapacityWindows ?? []);
                builder.UnavailableCourtWindows.AddRange(day.UnavailableCourtWindows ?? []);
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
                    BuildTimeSlots(start, end, slotMinutes),
                    pair.Value.RefereeCapacityWindows
                        .Distinct()
                        .OrderBy(window => window.StartTime)
                        .ThenBy(window => window.EndTime)
                        .ThenBy(window => window.RefereeCount)
                        .ToList(),
                    pair.Value.UnavailableCourtWindows
                        .Distinct()
                        .OrderBy(window => window.StartTime)
                        .ThenBy(window => window.EndTime)
                        .ToList());
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
                match.Dependencies,
                match.SideAPlayerIdentities,
                match.SideBPlayerIdentities))
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
                day.Courts,
                day.RefereeCapacityWindows,
                day.UnavailableCourtWindows))
            .ToList();
        var matchMinutes = matches
            .Select(match => match.DurationMinutes)
            .DefaultIfEmpty(20)
            .Min();
        return new ScheduleSettings(
            days,
            matchMinutes,
            MaxMatchesPerEntrantPerDay: 2,
            RefereeCount: board.SchedulingOptions?.RefereeCount);
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

            builder.RefereeCapacityWindows.AddRange(day.RefereeCapacityWindows ?? []);
            builder.UnavailableCourtWindows.AddRange(day.UnavailableCourtWindows ?? []);
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
                pair.Value.Courts.OrderBy(court => court, StringComparer.OrdinalIgnoreCase).ToList(),
                pair.Value.RefereeCapacityWindows
                    .Distinct()
                    .OrderBy(window => window.StartTime)
                    .ThenBy(window => window.EndTime)
                    .ThenBy(window => window.RefereeCount)
                    .ToList(),
                pair.Value.UnavailableCourtWindows
                    .Distinct()
                    .OrderBy(window => window.StartTime)
                    .ThenBy(window => window.EndTime)
                    .ToList()))
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

    private static Dictionary<string, IReadOnlyList<CrossEventPlayerIdentity>> BuildPlayerLookup(
        IEnumerable<DrawParticipant> participants,
        EventKind eventKind)
    {
        var lookup = new Dictionary<string, IReadOnlyList<CrossEventPlayerIdentity>>(StringComparer.OrdinalIgnoreCase);
        foreach (var participant in participants)
        {
            var players = GetParticipantPlayerIdentities(participant, eventKind);
            AddLookup(lookup, participant.DisplayName, players);
            AddLookup(lookup, CleanCompetitorText(participant.DisplayName), players);
        }

        return lookup;
    }

    private static IReadOnlyList<CrossEventPlayerIdentity> GetParticipantPlayerIdentities(
        DrawParticipant participant,
        EventKind eventKind)
    {
        if (eventKind == EventKind.Doubles)
        {
            var players = new[]
                {
                    BuildPlayerIdentity(participant.PrimaryName, participant.PrimaryStudentId),
                    BuildPlayerIdentity(participant.PartnerName, participant.PartnerStudentId)
                }
                .Where(identity => identity is not null)
                .Select(identity => identity!)
                .GroupBy(identity => identity.IdentityKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            return players.Count > 0
                ? players
                : SplitCompetitorIdentities(participant.DisplayName, eventKind);
        }

        if (eventKind == EventKind.Singles)
        {
            var name = !string.IsNullOrWhiteSpace(participant.PrimaryName)
                ? participant.PrimaryName!
                : participant.DisplayName;
            return [new CrossEventPlayerIdentity(name.Trim(), participant.PrimaryStudentId ?? "")];
        }

        var teamName = !string.IsNullOrWhiteSpace(participant.TeamName)
            ? participant.TeamName!
            : participant.DisplayName;
        return [new CrossEventPlayerIdentity(teamName.Trim(), "", IsTeam: true)];
    }

    private static void AddLookup(
        IDictionary<string, IReadOnlyList<CrossEventPlayerIdentity>> lookup,
        string value,
        IReadOnlyList<CrossEventPlayerIdentity> players)
    {
        var key = NormalizeCompetitorKey(value);
        if (!string.IsNullOrWhiteSpace(key) && !lookup.ContainsKey(key))
        {
            lookup.Add(key, players);
        }
    }

    private static IReadOnlyList<CrossEventPlayerIdentity> ResolvePlayerIdentities(
        string? side,
        EventKind eventKind,
        IReadOnlyDictionary<string, IReadOnlyList<CrossEventPlayerIdentity>> playerLookup)
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

        return SplitCompetitorIdentities(side, eventKind);
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

    private static CrossEventPlayerIdentity? BuildPlayerIdentity(string? name, string? studentId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new CrossEventPlayerIdentity(name.Trim(), studentId ?? "");
    }

    private static IReadOnlyList<CrossEventPlayerIdentity> SplitCompetitorIdentities(string value, EventKind eventKind)
    {
        var cleaned = CleanCompetitorText(value);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return [];
        }

        if (eventKind == EventKind.Team)
        {
            return [CrossEventPlayerIdentity.FromName(cleaned, isTeam: true)];
        }

        if (eventKind != EventKind.Doubles)
        {
            return [CrossEventPlayerIdentity.FromName(cleaned)];
        }

        var parts = Regex.Split(cleaned, @"[\s,，、/／&]+")
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return parts.Count > 0
            ? parts.Select(part => CrossEventPlayerIdentity.FromName(part)).ToList()
            : [CrossEventPlayerIdentity.FromName(cleaned)];
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

        public List<ScheduleRefereeCapacityWindow> RefereeCapacityWindows { get; } = [];

        public List<ScheduleCourtAvailabilityBlock> UnavailableCourtWindows { get; } = [];
    }

    private sealed class BoardConflictAccumulator(CrossEventConflictSeverity severity)
    {
        public CrossEventConflictSeverity Severity { get; set; } = severity;

        public List<string> Messages { get; } = [];
    }

    private sealed record GlobalSchedulingContext(
        CrossEventSchedulingOptions Options,
        string FinalDayLabel,
        IReadOnlyDictionary<string, int> DayIndex,
        IReadOnlyDictionary<string, int> DayCapacityMinutes,
        IReadOnlyDictionary<string, CrossEventDayLoadTarget> DayLoadTargets,
        IReadOnlyList<CrossEventStageWaveTarget> StageWaveTargets,
        IReadOnlyDictionary<FinalDayRuleKey, CrossEventFinalDayPolicy> FinalDayRules,
        int OriginalPositionWeight,
        int CrossDayMoveWeight,
        double TargetLoadWeight,
        double WarningLoadWeight,
        long EarlyStageWavePenalty,
        long LateStageWavePenalty);

    private sealed record FinalDayRuleKey(
        string EventName,
        CrossEventFinalDayMatchCategory Category);

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

    private sealed record GlobalCascadeEntry(
        int Depth,
        GlobalScheduleEntry Entry);

    private sealed record PlayerAppearanceBuilder(
        string PlayerName,
        string NormalizedPlayerName,
        CrossEventPlayerScheduleAppearance Appearance);

    private sealed record CrossEventLoadForecastContribution(
        CrossEventScheduleSource Source,
        PlayerDailyLoadForecast Forecast,
        CrossEventPlayerAppearance Anchor);
}

public sealed record CrossEventConflictExportResult(
    string OutputPath,
    CrossEventConflictReport Report);

public sealed record CrossEventMergedMaterialsExportResult(
    string OutputDirectory,
    IReadOnlyList<string> OutputPaths,
    SchedulePlan Schedule,
    IReadOnlyList<string> DayLabels);
