using System.Text.RegularExpressions;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows;

public sealed partial class CrossEventConflictWorkflow
{
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


    public CrossEventScheduleSaveResult SaveScheduleBoard(CrossEventScheduleBoard board)
    {
        var updates = board.Sources
            .Select(source => (source.SourcePath, Schedule: BuildSchedulePlan(source)))
            .ToList();
        foreach (var update in updates)
        {
            _progressStore.ValidateScheduleUpdate(update.SourcePath, update.Schedule);
        }

        var updatedPaths = new List<string>();
        var backupPaths = new List<string>();
        var completedUpdates = new List<(string FilePath, string BackupPath)>();
        try
        {
            foreach (var update in updates)
            {
                var outcome = _progressStore.UpdateSchedule(update.SourcePath, update.Schedule);
                updatedPaths.Add(update.SourcePath);
                if (!string.IsNullOrWhiteSpace(outcome.BackupPath))
                {
                    backupPaths.Add(outcome.BackupPath!);
                    completedUpdates.Add((update.SourcePath, outcome.BackupPath!));
                }
            }
        }
        catch (Exception ex)
        {
            var restoreFailures = new List<(string FilePath, string BackupPath, Exception Error)>();
            foreach (var completed in completedUpdates.AsEnumerable().Reverse())
            {
                try
                {
                    _progressStore.RestoreBackup(completed.FilePath, completed.BackupPath);
                }
                catch (Exception restoreError)
                {
                    restoreFailures.Add((completed.FilePath, completed.BackupPath, restoreError));
                }
            }

            if (restoreFailures.Count == 0)
            {
                var recoveryText = completedUpdates.Count == 0
                    ? "未修改其他赛事存档"
                    : $"已自动恢复之前更新的 {completedUpdates.Count} 个赛事存档";
                throw new TournamentProgressException(
                    $"保存多项目赛程失败，{recoveryText}。原始错误：{ex.Message}",
                    ex);
            }

            var manualRecovery = string.Join(
                Environment.NewLine,
                restoreFailures.Select(failure => $"{failure.FilePath} ← {failure.BackupPath}"));
            throw new TournamentProgressException(
                $"保存多项目赛程失败，且有 {restoreFailures.Count} 个赛事存档未能自动恢复。"
                + $"请使用以下备份手动恢复：{Environment.NewLine}{manualRecovery}"
                + $"{Environment.NewLine}原始错误：{ex.Message}",
                ex);
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
}
