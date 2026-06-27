using System.Text.RegularExpressions;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows;

public sealed class ScheduleWorkflow
{
    private const string BracketSheetName = "对阵表";
    private const string ScheduleGridSheetName = "时间场地网格";

    private readonly ScheduleService _scheduleService = new();
    private readonly ScheduleExcelWriter _scheduleWriter = new();
    private readonly ScoreSheetExcelWriter _scoreSheetWriter = new();
    private readonly DrawResultExcelWriter _drawWriter = new();
    private readonly DrawResultVisualWriter _visualWriter = new();
    private readonly MatchRecordReader _matchRecordReader = new();

    public SchedulePlan Generate(DrawResult result, ScheduleSettings settings)
    {
        return _scheduleService.Generate(result, settings);
    }

    public void ExportExcel(string outputPath, SchedulePlan schedule)
    {
        _scheduleWriter.Write(outputPath, schedule);
    }

    public IReadOnlyList<string> ExportFiles(
        string selectedPath,
        WorkflowExportFormat exportFormat,
        SchedulePlan schedule)
    {
        return DrawWorkflow.ExportFromWorkbook(
            selectedPath,
            exportFormat,
            ScheduleGridSheetName,
            path => _scheduleWriter.Write(path, schedule),
            _visualWriter,
            new DrawResultVisualOptions());
    }

    public IReadOnlyList<string> ExportTimedBracketFiles(
        string scheduleSelectedPath,
        WorkflowExportFormat exportFormat,
        DrawWorkflowResult workflowResult,
        SchedulePlan schedule,
        DrawResultVisualOptions? visualOptions = null)
    {
        return ExportTimedBracketFilesAtPath(
            BuildTimedBracketPath(scheduleSelectedPath, exportFormat),
            exportFormat,
            workflowResult,
            schedule,
            visualOptions);
    }

    public IReadOnlyList<string> ExportTimedBracketFilesAtPath(
        string selectedPath,
        WorkflowExportFormat exportFormat,
        DrawWorkflowResult workflowResult,
        SchedulePlan schedule,
        DrawResultVisualOptions? visualOptions = null)
    {
        return DrawWorkflow.ExportFromWorkbook(
            selectedPath,
            exportFormat,
            BracketSheetName,
            path => _drawWriter.Write(path, workflowResult.Result, workflowResult.Participants, schedule),
            _visualWriter,
            visualOptions ?? new DrawResultVisualOptions());
    }

    public IReadOnlyList<string> ExportDailyScheduleFiles(
        string selectedPath,
        WorkflowExportFormat exportFormat,
        SchedulePlan schedule,
        string dayLabel,
        IReadOnlyDictionary<string, MatchRecordResult>? completedResults = null,
        IReadOnlyCollection<string>? carryOverMatchNames = null,
        string? tournamentId = null)
    {
        return DrawWorkflow.ExportFromWorkbook(
            selectedPath,
            exportFormat,
            ScheduleGridSheetName,
            path => _scheduleWriter.WriteDailySchedule(
                path,
                schedule,
                dayLabel,
                completedResults,
                carryOverMatchNames,
                tournamentId),
            _visualWriter,
            new DrawResultVisualOptions());
    }

    public void ExportMatchRecord(
        string outputPath,
        SchedulePlan schedule,
        string? dayLabel,
        IReadOnlyDictionary<string, MatchRecordResult>? completedResults = null,
        IReadOnlySet<string>? carryOverMatchNames = null,
        string? tournamentId = null)
    {
        _scheduleWriter.WriteMatchRecord(
            outputPath,
            schedule,
            dayLabel,
            completedResults,
            carryOverMatchNames,
            tournamentId);
    }

    public void ExportIndividualScoreSheetPdf(
        string outputPath,
        SchedulePlan schedule,
        string projectName,
        string? dayLabel,
        IReadOnlyDictionary<string, MatchRecordResult>? completedResults = null,
        IReadOnlyCollection<string>? carryOverMatchNames = null)
    {
        _scoreSheetWriter.WriteIndividualMatchScorePdf(
            outputPath,
            schedule,
            projectName,
            dayLabel,
            completedResults,
            carryOverMatchNames);
    }

    public void ExportTeamScoreSheets(
        string outputPath,
        SchedulePlan schedule,
        string? dayLabel,
        IReadOnlyDictionary<string, MatchRecordResult>? completedResults = null,
        IReadOnlyCollection<string>? carryOverMatchNames = null)
    {
        _scoreSheetWriter.WriteTeamScoreSheets(
            outputPath,
            schedule,
            dayLabel,
            completedResults,
            carryOverMatchNames);
    }

    public MatchRecordImportResult ImportMatchRecords(IEnumerable<string> filePaths)
    {
        return _matchRecordReader.ReadMany(filePaths);
    }

    public static IReadOnlyList<ScheduleBoardDay> BuildBoardDays(SchedulePlan schedule)
    {
        var dayBuilders = new Dictionary<string, BoardDayBuilder>(StringComparer.Ordinal);
        foreach (var day in schedule.Settings.Days)
        {
            var builder = GetDayBuilder(dayBuilders, day.DayLabel);
            builder.StartTime = MinTime(builder.StartTime, day.DayStart);
            builder.EndTime = MaxTime(builder.EndTime, day.DayEnd);
            foreach (var court in day.Courts)
            {
                builder.Courts.Add(court);
            }
        }

        foreach (var match in schedule.Matches)
        {
            var builder = GetDayBuilder(dayBuilders, match.DayLabel);
            builder.StartTime = MinTime(builder.StartTime, match.StartTime);
            builder.EndTime = MaxTime(builder.EndTime, match.EndTime);
            builder.Courts.Add(match.Court);
            builder.Durations.Add(match.DurationMinutes);
        }

        return dayBuilders
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var start = pair.Value.StartTime ?? new TimeOnly(8, 0);
                var end = pair.Value.EndTime ?? new TimeOnly(22, 0);
                var slotMinutes = NormalizeSlotMinutes(pair.Value.Durations);
                return new ScheduleBoardDay(
                    pair.Key,
                    start,
                    end,
                    pair.Value.Courts.OrderBy(court => court, StringComparer.OrdinalIgnoreCase).ToList(),
                    slotMinutes,
                    BuildTimeSlots(start, end, slotMinutes));
            })
            .ToList();
    }

    public static ScheduleBoardView BuildScheduleBoardView(
        SchedulePlan schedule,
        IReadOnlySet<string>? lockedMatchNames = null)
    {
        var items = schedule.Matches
            .Select(match =>
            {
                var isLocked = lockedMatchNames?.Contains(match.MatchName) == true;
                return new ScheduleBoardItem(
                    match.MatchName,
                    ScheduleBoardDrag.BuildSingleEventPayload(match.MatchName),
                    match.MatchName,
                    match.DayLabel,
                    match.StartTime,
                    match.EndTime,
                    match.Court,
                    match.Order,
                    $"{match.GroupName} · {match.MatchName}",
                    $"{match.TimeRange} · {match.Phase}" + (isLocked ? " · 已完成" : ""),
                    $"{match.SideA}  vs  {match.SideB}",
                    IsLocked: isLocked);
            })
            .ToList();

        return new ScheduleBoardView(
            ScheduleBoardKind.SingleEvent,
            BuildBoardDays(schedule),
            items);
    }

    public static SchedulePlan MoveScheduledMatch(
        SchedulePlan schedule,
        string matchName,
        string dayLabel,
        TimeOnly startTime,
        string court,
        IReadOnlySet<string>? lockedMatchNames = null)
    {
        return MoveScheduledMatchCore(
            schedule,
            matchName,
            dayLabel,
            startTime,
            court,
            lockedMatchNames,
            ensureDependencyOrder: true);
    }

    private static SchedulePlan MoveScheduledMatchCore(
        SchedulePlan schedule,
        string matchName,
        string dayLabel,
        TimeOnly startTime,
        string court,
        IReadOnlySet<string>? lockedMatchNames,
        bool ensureDependencyOrder,
        bool refreshQualityReport = true)
    {
        var match = schedule.Matches.FirstOrDefault(item => string.Equals(item.MatchName, matchName, StringComparison.Ordinal))
            ?? throw new DrawValidationException("找不到需要调整的比赛。");
        if (lockedMatchNames?.Contains(match.MatchName) == true)
        {
            throw new DrawValidationException("该场比赛已有赛果，不能再拖动调整。");
        }

        var day = schedule.Settings.Days.FirstOrDefault(item => string.Equals(item.DayLabel, dayLabel, StringComparison.Ordinal))
            ?? throw new DrawValidationException("目标比赛日不在当前赛程设置中。");
        if (!day.Courts.Contains(court, StringComparer.OrdinalIgnoreCase))
        {
            throw new DrawValidationException("目标场地不在当前比赛日的场地列表中。");
        }

        var endTime = startTime.AddMinutes(match.DurationMinutes);
        if (startTime < day.DayStart || endTime > day.DayEnd)
        {
            throw new DrawValidationException("目标时间超出了当前比赛日的可用时间段。");
        }

        if (!ScheduleResourceCalculator.IsCourtAvailable(day, court, startTime, endTime))
        {
            throw new DrawValidationException("目标场地在该时间段不可用，请选择其他空位。");
        }

        var hasCourtOverlap = schedule.Matches.Any(other =>
            !string.Equals(other.MatchName, match.MatchName, StringComparison.Ordinal)
            && string.Equals(other.DayLabel, dayLabel, StringComparison.Ordinal)
            && string.Equals(other.Court, court, StringComparison.OrdinalIgnoreCase)
            && HasTimeOverlap(startTime, endTime, other.StartTime, other.EndTime));
        if (hasCourtOverlap)
        {
            throw new DrawValidationException("目标时间和场地已有比赛，请选择空位后再调整。");
        }

        if (WouldExceedRefereeCapacity(schedule, match.MatchName, dayLabel, startTime, endTime))
        {
            throw new DrawValidationException("目标时间段超过裁判人数可承载的同时开赛上限。");
        }

        var matches = schedule.Matches
            .Select(item => string.Equals(item.MatchName, match.MatchName, StringComparison.Ordinal)
                ? item with
                {
                    DayLabel = dayLabel,
                    StartTime = startTime,
                    EndTime = endTime,
                    Court = court
                }
                : item)
            .ToList();
        var moved = schedule with { Matches = NormalizeScheduledMatchOrders(matches) };
        if (ensureDependencyOrder)
        {
            ScheduleDependencyGraph.Build(moved).EnsureDependencyOrder();
        }

        return refreshQualityReport
            ? moved with { QualityReport = ScheduleService.EvaluateScheduleQuality(moved) }
            : moved;
    }

    public static ScheduleBoardMoveValidationResult ValidateScheduledMatchMove(
        SchedulePlan schedule,
        string matchName,
        string dayLabel,
        TimeOnly startTime,
        string court,
        IReadOnlySet<string>? lockedMatchNames = null)
    {
        try
        {
            var moved = MoveScheduledMatchCore(
                schedule,
                matchName,
                dayLabel,
                startTime,
                court,
                lockedMatchNames,
                ensureDependencyOrder: false,
                refreshQualityReport: false);
            var targetText = $"目标：{dayLabel} {startTime:HH:mm} · {court}";
            var fixableOrderViolations = FindFixableCascadeOrderViolations(moved, matchName);
            var newIssues = FindNewRelevantMoveIssues(schedule, moved, matchName);
            var blockingIssue = newIssues.FirstOrDefault(issue =>
                issue.Severity == ScheduleConstraintSeverity.Severe
                && !IsFixableCascadeOrderIssue(issue, fixableOrderViolations));
            if (blockingIssue is not null)
            {
                return ScheduleBoardMoveValidationResult.Blocked(
                    $"{targetText} 不可放置：{blockingIssue.Message}",
                    BuildAffectedMatches(matchName, blockingIssue));
            }

            if (fixableOrderViolations.Count > 0)
            {
                return ScheduleBoardMoveValidationResult.Warning(
                    $"{targetText} 可放置，但会影响 {fixableOrderViolations.Count} 条后续依赖；可选择连锁移动后续场次自动修复。",
                    fixableOrderViolations
                        .Select(violation => violation.Edge.Dependent.MatchName)
                        .Prepend(matchName)
                        .Distinct(StringComparer.Ordinal)
                        .ToList());
            }

            var warningIssue = newIssues.FirstOrDefault(issue =>
                issue.Severity is ScheduleConstraintSeverity.Warning or ScheduleConstraintSeverity.Notice);
            if (warningIssue is not null)
            {
                return ScheduleBoardMoveValidationResult.Warning(
                    $"{targetText} 可放置，但有提醒：{warningIssue.Message}",
                    BuildAffectedMatches(matchName, warningIssue));
            }

            return ScheduleBoardMoveValidationResult.Allowed($"{targetText} 可以放置。");
        }
        catch (DrawValidationException ex)
        {
            return ScheduleBoardMoveValidationResult.Blocked(ex.Message, [matchName]);
        }
    }

    public static ScheduleBoardCascadeMovePreview BuildScheduledMatchCascadeMovePreview(
        SchedulePlan schedule,
        string matchName,
        string dayLabel,
        TimeOnly startTime,
        string court,
        IReadOnlySet<string>? lockedMatchNames = null)
    {
        var moved = MoveScheduledMatchCore(
            schedule,
            matchName,
            dayLabel,
            startTime,
            court,
            lockedMatchNames,
            ensureDependencyOrder: false,
            refreshQualityReport: false);
        return BuildCascadeMovePreviewFromSchedule(moved, matchName, lockedMatchNames);
    }

    public static ScheduleBoardCascadeMovePreview BuildCascadeMovePreviewFromSchedule(
        SchedulePlan schedule,
        string matchName,
        IReadOnlySet<string>? completedMatchNames = null,
        string eventName = "")
    {
        var movedMatch = schedule.Matches.FirstOrDefault(match => string.Equals(match.MatchName, matchName, StringComparison.Ordinal))
            ?? throw new DrawValidationException("找不到需要预览的比赛。");
        var graph = ScheduleDependencyGraph.Build(schedule);
        var affected = BuildCascadePreviewItems(graph, movedMatch, completedMatchNames, eventName);
        return new ScheduleBoardCascadeMovePreview(
            movedMatch.MatchName,
            movedMatch.DayLabel,
            movedMatch.StartTime,
            movedMatch.EndTime,
            movedMatch.Court,
            affected);
    }

    public static ScheduleBoardCascadeMoveResult<SchedulePlan> CascadeMoveScheduledMatch(
        SchedulePlan schedule,
        string matchName,
        string dayLabel,
        TimeOnly startTime,
        string court,
        IReadOnlySet<string>? lockedMatchNames = null,
        string eventName = "")
    {
        var originalByName = schedule.Matches.ToDictionary(match => match.MatchName, StringComparer.Ordinal);
        var working = MoveScheduledMatchCore(
            schedule,
            matchName,
            dayLabel,
            startTime,
            court,
            lockedMatchNames,
            ensureDependencyOrder: false,
            refreshQualityReport: false);
        var root = working.Matches.FirstOrDefault(match => string.Equals(match.MatchName, matchName, StringComparison.Ordinal))
            ?? throw new DrawValidationException("找不到需要连锁移动的比赛。");
        var movedItems = new List<ScheduleBoardCascadeMovedItem>();
        AddMovedItemIfChanged(
            movedItems,
            originalByName,
            root,
            depth: 0,
            eventName,
            "当前场次移动到裁判长选择的位置");

        var rules = ScheduleConstraintRules.For(working.Settings.ConstraintProfile);
        var nodes = BuildCascadeDependencyNodes(ScheduleDependencyGraph.Build(working), root.MatchId);
        foreach (var node in nodes)
        {
            var current = working.Matches.FirstOrDefault(match => string.Equals(match.MatchId, node.Edge.Dependent.MatchId, StringComparison.Ordinal));
            if (current is null)
            {
                continue;
            }

            if (lockedMatchNames?.Contains(current.MatchName) == true)
            {
                throw new DrawValidationException($"无法连锁移动：后续场次“{current.MatchName}”已有赛果，不能自动调整。");
            }

            var minimumStart = FindMinimumDependencyStart(working, current, rules);
            var dayNumbers = BuildScheduleDayNumberLookup(working.Settings, working.Matches);
            var currentStart = BuildComparableMinute(current.DayLabel, current.StartTime, dayNumbers);
            var minimumAllowedStart = Math.Max(currentStart, minimumStart);
            if (currentStart >= minimumAllowedStart
                && IsSchedulePlacementAvailable(working, current.MatchName, current.DayLabel, current.StartTime, current.Court))
            {
                continue;
            }

            var placement = FindEarliestCascadePlacement(working, current, minimumAllowedStart)
                ?? throw new DrawValidationException($"无法连锁移动：找不到“{current.MatchName}”满足依赖顺序、休息间隔和场地空位的后续位置。");
            var before = current;
            working = ApplyScheduledMatchPlacement(working, current.MatchName, placement);
            var after = working.Matches.First(match => string.Equals(match.MatchName, current.MatchName, StringComparison.Ordinal));
            movedItems.Add(new ScheduleBoardCascadeMovedItem(
                node.Depth,
                eventName,
                after.MatchName,
                before.DayLabel,
                before.StartTime,
                before.EndTime,
                before.Court,
                after.DayLabel,
                after.StartTime,
                after.EndTime,
                after.Court,
                $"连锁后移以满足 {node.Edge.Source.MatchName} 的后续依赖"));
        }

        ScheduleDependencyGraph.Build(working).EnsureDependencyOrder();
        var severeIssue = new ScheduleConstraintAnalyzer()
            .Analyze(working)
            .Issues
            .FirstOrDefault(issue =>
                issue.Severity == ScheduleConstraintSeverity.Severe
                && movedItems.Any(item => IsRelevantMoveIssue(issue, item.MatchName)));
        if (severeIssue is not null)
        {
            throw new DrawValidationException($"无法连锁移动：{severeIssue.Message}");
        }

        var refreshed = working with { QualityReport = ScheduleService.EvaluateScheduleQuality(working) };
        return new ScheduleBoardCascadeMoveResult<SchedulePlan>(
            refreshed,
            movedItems,
            movedItems.Count == 0 ? ["当前位置已经满足连锁依赖，无需移动后续场次。"] : []);
    }

    public static ScheduleSettings BuildSettings(ScheduleWorkflowRequest request)
    {
        var day = new ScheduleDayWorkflowRequest(
            request.Date,
            request.Start,
            request.End,
            "自定义",
            request.CourtsText);
        return BuildSettings(
            [day],
            request.MatchMinutes,
            request.MaxMatchesPerEntrantPerDay,
            request.KnockoutTimingBoundaryEntrants,
            request.BeforeBoundaryMatchMinutes,
            request.BeforeBoundaryMaxMatchesPerEntrantPerDay,
            request.ConstraintProfile);
    }

    public static ScheduleSettings BuildSettings(
        IReadOnlyList<ScheduleDayWorkflowRequest> days,
        int matchMinutes,
        int maxMatchesPerEntrantPerDay,
        int? knockoutTimingBoundaryEntrants = null,
        int? beforeBoundaryMatchMinutes = null,
        int? beforeBoundaryMaxMatchesPerEntrantPerDay = null,
        ScheduleConstraintProfile constraintProfile = ScheduleConstraintProfile.Campus,
        ScheduleAutoSchedulingStrategy autoSchedulingStrategy = ScheduleAutoSchedulingStrategy.Compact,
        int? refereeCount = null)
    {
        if (days.Count == 0)
        {
            throw new DrawValidationException("请至少添加一个赛程日。");
        }

        var daySettings = days
            .OrderBy(day => day.Date)
            .Select(day =>
            {
                if (day.End <= day.Start)
                {
                    throw new DrawValidationException("赛程结束时间必须晚于开始时间。");
                }

                return new ScheduleDaySettings(
                    day.Date,
                    day.Start,
                    day.End,
                    ParseCourts(day.CourtsText),
                    null,
                    day.UnavailableCourtWindows);
            })
            .ToList();

        ScheduleTimingSettings? beforeBoundaryTiming = null;
        if (knockoutTimingBoundaryEntrants is > 0)
        {
            if (beforeBoundaryMatchMinutes is null or <= 0)
            {
                throw new DrawValidationException("分界线前单场比赛耗时必须是大于 0 的整数。");
            }

            if (beforeBoundaryMaxMatchesPerEntrantPerDay is null or <= 0)
            {
                throw new DrawValidationException("分界线前单名选手每日最多场次必须是大于 0 的整数。");
            }

            beforeBoundaryTiming = new ScheduleTimingSettings(
                beforeBoundaryMatchMinutes.Value,
                beforeBoundaryMaxMatchesPerEntrantPerDay.Value);
        }

        return new ScheduleSettings(
            daySettings,
            matchMinutes,
            maxMatchesPerEntrantPerDay,
            knockoutTimingBoundaryEntrants,
            beforeBoundaryTiming,
            refereeCount)
        {
            ConstraintProfile = constraintProfile,
            AutoSchedulingStrategy = autoSchedulingStrategy
        };
    }

    public static IReadOnlyList<string> ParseCourts(string value)
    {
        var courts = Regex.Split(value, @"[\s,，、;；]+")
            .SelectMany(ExpandCourtToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (courts.Count == 0)
        {
            throw new DrawValidationException("请至少填写一片场地。");
        }

        return courts;
    }

    private static IReadOnlyList<string> ExpandCourtToken(string token)
    {
        token = token.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return [];
        }

        var match = Regex.Match(
            token,
            @"^([A-Za-z]+)(\d+)\s*[-~－–—]\s*([A-Za-z]+)?(\d+)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return [token];
        }

        var startPrefix = match.Groups[1].Value.ToUpperInvariant();
        var startNumber = int.Parse(match.Groups[2].Value);
        var endPrefix = string.IsNullOrWhiteSpace(match.Groups[3].Value)
            ? startPrefix
            : match.Groups[3].Value.ToUpperInvariant();
        var endNumber = int.Parse(match.Groups[4].Value);

        if (startPrefix == endPrefix)
        {
            return ExpandNumberRange(startPrefix, startNumber, endNumber);
        }

        if (startPrefix.Length != 1 || endPrefix.Length != 1)
        {
            return [token];
        }

        var prefixStep = startPrefix[0] <= endPrefix[0] ? 1 : -1;
        var courts = new List<string>();
        for (var prefix = startPrefix[0];; prefix = (char)(prefix + prefixStep))
        {
            courts.AddRange(ExpandNumberRange(prefix.ToString(), startNumber, endNumber));
            if (prefix == endPrefix[0])
            {
                break;
            }
        }

        return courts;
    }

    private static IReadOnlyList<ScheduledMatch> NormalizeScheduledMatchOrders(IReadOnlyList<ScheduledMatch> matches)
    {
        return matches
            .OrderBy(match => match.DayLabel, StringComparer.Ordinal)
            .ThenBy(match => match.StartTime)
            .ThenBy(match => match.Court, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Order)
            .Select((match, index) => match with { Order = index + 1 })
            .ToList();
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

    private static bool HasTimeOverlap(TimeOnly leftStart, TimeOnly leftEnd, TimeOnly rightStart, TimeOnly rightEnd)
    {
        return leftStart < rightEnd && rightStart < leftEnd;
    }

    private static IReadOnlyList<string> ExpandNumberRange(string prefix, int startNumber, int endNumber)
    {
        var step = startNumber <= endNumber ? 1 : -1;
        var courts = new List<string>();
        for (var number = startNumber;; number += step)
        {
            courts.Add($"{prefix}{number}");
            if (number == endNumber)
            {
                break;
            }
        }

        return courts;
    }

    public static string BuildDefaultScheduleExcelFileName(DrawResult result, string? inputPath)
    {
        return BuildDefaultScheduleFileName(result, inputPath, WorkflowExportFormat.Excel);
    }

    public static string BuildDefaultScheduleFileName(
        DrawResult result,
        string? inputPath,
        WorkflowExportFormat format)
    {
        var stem = string.Join("_", new[]
        {
            WorkflowFileNames.ExtractEventName(inputPath),
            "赛程表",
            WorkflowFileNames.GetCompetitionModePart(result.Settings.CompetitionMode),
            WorkflowFileNames.GetEventScalePart(result.Settings.EventKind, result.Audit.ParticipantCount),
            $"{result.Audit.GroupCount}组",
            WorkflowFileNames.GetPlacementPlayoffPart(result.Settings) ?? "",
            DateTime.Now.ToString("yyyyMMdd_HHmm")
        }
            .Select(WorkflowFileNames.Sanitize)
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        return $"{WorkflowFileNames.Limit(stem)}{WorkflowExportHelpers.GetExtension(format)}";
    }

    public static string BuildDefaultMatchRecordFileName(string dayLabel)
    {
        var stem = DateOnly.TryParse(dayLabel, out var date)
            ? $"{date.Month}月{date.Day}日赛程记录表"
            : $"{dayLabel}赛程记录表";

        return $"{WorkflowFileNames.Sanitize(stem)}.xlsx";
    }

    public static string BuildDefaultDailyScheduleFileName(string dayLabel)
    {
        var stem = BuildDayFileNameStem(dayLabel, "赛程安排表");
        return $"{WorkflowFileNames.Sanitize(stem)}.xlsx";
    }

    public static string BuildDefaultDailyTimedBracketFileName(string dayLabel)
    {
        var stem = BuildDayFileNameStem(dayLabel, "带时间场地对阵表");
        return $"{WorkflowFileNames.Sanitize(stem)}.xlsx";
    }

    public static string BuildDefaultIndividualScoreSheetFileName(string dayLabel)
    {
        var stem = BuildDayFileNameStem(dayLabel, "单场比赛计分表");
        return $"{WorkflowFileNames.Sanitize(stem)}.pdf";
    }

    public static string BuildDefaultTeamScoreSheetFileName(string dayLabel)
    {
        var stem = BuildDayFileNameStem(dayLabel, "团体赛记分表");
        return $"{WorkflowFileNames.Sanitize(stem)}.xlsx";
    }

    private static string BuildDayFileNameStem(string dayLabel, string suffix)
    {
        return DateOnly.TryParse(dayLabel, out var date)
            ? $"{date.Month}月{date.Day}日{suffix}"
            : $"{dayLabel}{suffix}";
    }

    public static string BuildScheduleCapacityText(ScheduleSettings settings)
    {
        string BuildCapacity(int minutesPerMatch)
        {
            return string.Join("；", settings.Days.Select(day =>
            {
                var capacityMinutes = ScheduleResourceCalculator.CalculateDayCapacityMinutes(
                    day,
                    settings.RefereeCount,
                    slotMinutes: 1);
                var capacityMatches = Math.Max(0, capacityMinutes / Math.Max(1, minutesPerMatch));
                var refereeText = settings.RefereeCount is > 0
                    ? $"{settings.RefereeCount.Value}名裁判/"
                    : "";
                var resourceText = day.UnavailableCourtWindows is { Count: > 0 }
                    ? "资源日历/"
                    : "";
                return $"{day.DayLabel} {day.Courts.Count}片/{refereeText}{resourceText}{capacityMatches}场";
            }));
        }

        if (!settings.HasKnockoutTimingSplit)
        {
            return $"{BuildScheduleStrategyText(settings.AutoSchedulingStrategy)}；每日上限{settings.MaxMatchesPerEntrantPerDay}场；{BuildCapacity(settings.MatchMinutes)}";
        }

        return $"{BuildScheduleStrategyText(settings.AutoSchedulingStrategy)}；"
            + $"分界线前每日上限{settings.BeforeBoundaryTiming!.MaxMatchesPerEntrantPerDay}场、每场{settings.BeforeBoundaryTiming.MatchMinutes}分钟：{BuildCapacity(settings.BeforeBoundaryTiming.MatchMinutes)}；"
            + $"分界线后每日上限{settings.MaxMatchesPerEntrantPerDay}场、每场{settings.MatchMinutes}分钟：{BuildCapacity(settings.MatchMinutes)}";
    }

    public static string BuildScheduleStrategyText(ScheduleAutoSchedulingStrategy strategy)
    {
        return strategy switch
        {
            ScheduleAutoSchedulingStrategy.Compact => "紧凑完成",
            ScheduleAutoSchedulingStrategy.FinalsDayFriendly => "决赛日友好",
            _ => "均衡宽松"
        };
    }

    public static string? GetNextMatchRecordDayLabel(SchedulePlan plan, MatchRecordImportResult importResult)
    {
        var scheduleDays = plan.Matches
            .Select(match => match.DayLabel)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (scheduleDays.Count == 0 || importResult.DayLabels.Count == 0)
        {
            return null;
        }

        var importedDaySet = importResult.DayLabels.ToHashSet(StringComparer.Ordinal);
        var importedIndexes = scheduleDays
            .Select((day, index) => importedDaySet.Contains(day) ? index : -1)
            .Where(index => index >= 0)
            .ToList();
        if (importedIndexes.Count > 0)
        {
            var nextIndex = importedIndexes.Max() + 1;
            return nextIndex < scheduleDays.Count ? scheduleDays[nextIndex] : null;
        }

        var latestImportedDate = importResult.DayLabels
            .Select(day => DateOnly.TryParse(day, out var date) ? date : (DateOnly?)null)
            .Where(date => date.HasValue)
            .Select(date => date!.Value)
            .DefaultIfEmpty()
            .Max();
        if (latestImportedDate == default)
        {
            return null;
        }

        return scheduleDays.FirstOrDefault(day =>
            DateOnly.TryParse(day, out var date) && date > latestImportedDate);
    }

    public static string? GetFirstRecordDayLabel(SchedulePlan plan)
    {
        return plan.Matches
            .FirstOrDefault(HasExplicitScheduleSides)
            ?.DayLabel
            ?? plan.Matches.FirstOrDefault()?.DayLabel;
    }

    public static string BuildMatchRecordImportWarning(MatchRecordImportResult importResult, string nextDayLabel)
    {
        var parts = new List<string>();
        if (importResult.MissingResultRows.Count > 0)
        {
            parts.Add($"有 {importResult.MissingResultRows.Count} 场未填写胜方，将顺延到 {nextDayLabel}");
        }

        if (importResult.ValidationIssues.Count > 0)
        {
            parts.Add($"有 {importResult.ValidationIssues.Count} 处比分、用时或胜方提醒，将按已填写胜方推进");
        }

        var detail = WorkflowIssueText.BuildDetails(
            WorkflowIssueText.BuildSection(
                $"未填写胜方，将顺延到 {nextDayLabel}",
                importResult.MissingResultRows),
            WorkflowIssueText.BuildSection(
                "比分、用时或胜方提醒，将按已填写胜方推进",
                importResult.ValidationIssues));
        return $"记录表存在需要裁判长确认的情况：{string.Join("，", parts)}。{detail}\n\n是否仍继续导出下一比赛日赛程记录表？";
    }

    private static IReadOnlyList<ScheduleConstraintIssue> FindNewRelevantMoveIssues(
        SchedulePlan before,
        SchedulePlan after,
        string matchName)
    {
        var analyzer = new ScheduleConstraintAnalyzer();
        var beforeKeys = analyzer.Analyze(before).Issues
            .Where(issue => IsRelevantMoveIssue(issue, matchName))
            .Select(BuildMoveIssueKey)
            .ToHashSet(StringComparer.Ordinal);
        return analyzer.Analyze(after).Issues
            .Where(issue => IsRelevantMoveIssue(issue, matchName))
            .Where(issue => !beforeKeys.Contains(BuildMoveIssueKey(issue)))
            .OrderByDescending(issue => issue.Severity)
            .ThenBy(issue => issue.DayLabel, StringComparer.Ordinal)
            .ThenBy(issue => issue.StartTime ?? TimeOnly.MinValue)
            .ThenBy(issue => issue.MatchName, StringComparer.Ordinal)
            .ToList();
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

    private static bool IsFixableCascadeOrderIssue(
        ScheduleConstraintIssue issue,
        IReadOnlyList<ScheduleDependencyOrderViolation> fixableOrderViolations)
    {
        return issue.Type == ScheduleConstraintIssueType.DependencyOrder
               && issue.Scope == ScheduleConstraintIssueScope.DirectDependency
               && fixableOrderViolations.Any(violation =>
                   string.Equals(violation.Edge.Dependent.MatchName, issue.MatchName, StringComparison.Ordinal));
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

    private static IReadOnlyList<CascadeDependencyNode> BuildCascadeDependencyNodes(
        ScheduleDependencyGraph graph,
        string rootMatchId)
    {
        var edgesBySource = graph.Edges
            .GroupBy(edge => edge.Source.MatchId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(edge => edge.Dependent.DayLabel, StringComparer.Ordinal)
                    .ThenBy(edge => edge.Dependent.StartTime)
                    .ThenBy(edge => edge.Dependent.Court, StringComparer.Ordinal)
                    .ThenBy(edge => edge.Dependent.MatchName, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);
        var result = new List<CascadeDependencyNode>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootMatchId };
        var queue = new Queue<(string MatchId, int Depth)>();
        queue.Enqueue((rootMatchId, 0));

        while (queue.Count > 0)
        {
            var (sourceMatchId, depth) = queue.Dequeue();
            if (!edgesBySource.TryGetValue(sourceMatchId, out var edges))
            {
                continue;
            }

            foreach (var edge in edges)
            {
                if (!visited.Add(edge.Dependent.MatchId))
                {
                    continue;
                }

                var nextDepth = depth + 1;
                result.Add(new CascadeDependencyNode(nextDepth, edge));
                queue.Enqueue((edge.Dependent.MatchId, nextDepth));
            }
        }

        return result
            .OrderBy(node => node.Depth)
            .ThenBy(node => node.Edge.Dependent.DayLabel, StringComparer.Ordinal)
            .ThenBy(node => node.Edge.Dependent.StartTime)
            .ThenBy(node => node.Edge.Dependent.Court, StringComparer.Ordinal)
            .ThenBy(node => node.Edge.Dependent.MatchName, StringComparer.Ordinal)
            .ToList();
    }

    private static int FindMinimumDependencyStart(
        SchedulePlan schedule,
        ScheduledMatch dependent,
        ScheduleConstraintRules rules)
    {
        var matchesById = schedule.Matches
            .GroupBy(match => match.MatchId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var dayNumbers = BuildScheduleDayNumberLookup(schedule.Settings, schedule.Matches);
        var requiredRest = IsKeyCascadeMatch(dependent)
            ? rules.KeyMatchMinimumRestMinutes
            : rules.MinimumRestMinutes;
        var minimumStart = int.MinValue;
        foreach (var dependency in dependent.Dependencies)
        {
            if (!matchesById.TryGetValue(dependency.SourceMatchId, out var source))
            {
                continue;
            }

            var sourceEnd = BuildComparableMinute(source.DayLabel, source.EndTime, dayNumbers);
            minimumStart = Math.Max(minimumStart, sourceEnd + requiredRest);
        }

        return minimumStart == int.MinValue
            ? BuildComparableMinute(dependent.DayLabel, dependent.StartTime, dayNumbers)
            : minimumStart;
    }

    private static SchedulePlacement? FindEarliestCascadePlacement(
        SchedulePlan schedule,
        ScheduledMatch match,
        int minimumComparableStart)
    {
        var dayNumbers = BuildScheduleDayNumberLookup(schedule.Settings, schedule.Matches);
        var slotMinutes = NormalizeSlotMinutes(schedule.Matches.Select(item => item.DurationMinutes).ToList());
        foreach (var day in schedule.Settings.Days.OrderBy(day => day.Date))
        {
            foreach (var slot in BuildTimeSlots(day.DayStart, day.DayEnd, slotMinutes))
            {
                var endTime = slot.AddMinutes(match.DurationMinutes);
                if (endTime > day.DayEnd
                    || BuildComparableMinute(day.DayLabel, slot, dayNumbers) < minimumComparableStart)
                {
                    continue;
                }

                foreach (var candidateCourt in day.Courts)
                {
                    if (IsSchedulePlacementAvailable(schedule, match.MatchName, day.DayLabel, slot, candidateCourt))
                    {
                        return new SchedulePlacement(day.DayLabel, slot, endTime, candidateCourt);
                    }
                }
            }
        }

        return null;
    }

    private static bool IsSchedulePlacementAvailable(
        SchedulePlan schedule,
        string matchName,
        string dayLabel,
        TimeOnly startTime,
        string court)
    {
        var match = schedule.Matches.FirstOrDefault(item => string.Equals(item.MatchName, matchName, StringComparison.Ordinal));
        if (match is null)
        {
            return false;
        }

        var day = schedule.Settings.Days.FirstOrDefault(item => string.Equals(item.DayLabel, dayLabel, StringComparison.Ordinal));
        if (day is null || !day.Courts.Contains(court, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var endTime = startTime.AddMinutes(match.DurationMinutes);
        if (startTime < day.DayStart || endTime > day.DayEnd)
        {
            return false;
        }

        if (!ScheduleResourceCalculator.IsCourtAvailable(day, court, startTime, endTime)
            || WouldExceedRefereeCapacity(schedule, matchName, dayLabel, startTime, endTime))
        {
            return false;
        }

        return !schedule.Matches.Any(other =>
            !string.Equals(other.MatchName, matchName, StringComparison.Ordinal)
            && string.Equals(other.DayLabel, dayLabel, StringComparison.Ordinal)
            && string.Equals(other.Court, court, StringComparison.OrdinalIgnoreCase)
            && HasTimeOverlap(startTime, endTime, other.StartTime, other.EndTime));
    }

    private static bool WouldExceedRefereeCapacity(
        SchedulePlan schedule,
        string movingMatchName,
        string dayLabel,
        TimeOnly startTime,
        TimeOnly endTime)
    {
        var day = schedule.Settings.Days.FirstOrDefault(item => string.Equals(item.DayLabel, dayLabel, StringComparison.Ordinal));
        if (day is null)
        {
            return true;
        }

        var limit = ScheduleResourceCalculator.GetConcurrentMatchLimit(day, schedule.Settings.RefereeCount, startTime, endTime);
        if (limit <= 0)
        {
            return true;
        }

        var overlapping = schedule.Matches.Count(other =>
            !string.Equals(other.MatchName, movingMatchName, StringComparison.Ordinal)
            && string.Equals(other.DayLabel, dayLabel, StringComparison.Ordinal)
            && HasTimeOverlap(startTime, endTime, other.StartTime, other.EndTime));
        return overlapping + 1 > limit;
    }

    private static SchedulePlan ApplyScheduledMatchPlacement(
        SchedulePlan schedule,
        string matchName,
        SchedulePlacement placement)
    {
        var matches = schedule.Matches
            .Select(match => string.Equals(match.MatchName, matchName, StringComparison.Ordinal)
                ? match with
                {
                    DayLabel = placement.DayLabel,
                    StartTime = placement.StartTime,
                    EndTime = placement.EndTime,
                    Court = placement.Court
                }
                : match)
            .ToList();
        return schedule with { Matches = NormalizeScheduledMatchOrders(matches) };
    }

    private static void AddMovedItemIfChanged(
        ICollection<ScheduleBoardCascadeMovedItem> movedItems,
        IReadOnlyDictionary<string, ScheduledMatch> originalByName,
        ScheduledMatch after,
        int depth,
        string eventName,
        string reason)
    {
        if (!originalByName.TryGetValue(after.MatchName, out var before)
            || (string.Equals(before.DayLabel, after.DayLabel, StringComparison.Ordinal)
                && before.StartTime == after.StartTime
                && before.EndTime == after.EndTime
                && string.Equals(before.Court, after.Court, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        movedItems.Add(new ScheduleBoardCascadeMovedItem(
            depth,
            eventName,
            after.MatchName,
            before.DayLabel,
            before.StartTime,
            before.EndTime,
            before.Court,
            after.DayLabel,
            after.StartTime,
            after.EndTime,
            after.Court,
            reason));
    }

    private static IReadOnlyDictionary<string, int> BuildScheduleDayNumberLookup(
        ScheduleSettings settings,
        IReadOnlyList<ScheduledMatch> matches)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var day in settings.Days)
        {
            result[day.DayLabel] = day.Date.DayNumber;
        }

        var fallback = result.Count == 0 ? 1_000_000 : result.Values.Max() + 1;
        foreach (var dayLabel in matches
                     .Select(match => match.DayLabel)
                     .Where(dayLabel => !result.ContainsKey(dayLabel))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(dayLabel => dayLabel, StringComparer.Ordinal))
        {
            result[dayLabel] = DateOnly.TryParse(dayLabel, out var date)
                ? date.DayNumber
                : fallback++;
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

    private static bool IsKeyCascadeMatch(ScheduledMatch match)
    {
        var text = $"{match.Phase} {match.MatchName}";
        return text.Contains("决赛", StringComparison.Ordinal)
               || text.Contains("半决赛", StringComparison.Ordinal)
               || text.Contains("4进2", StringComparison.Ordinal)
               || text.Contains("3/4", StringComparison.Ordinal)
               || text.Contains("3-4", StringComparison.Ordinal)
               || text.Contains("铜牌", StringComparison.Ordinal);
    }

    private static IReadOnlyList<ScheduleBoardCascadeMovePreviewItem> BuildCascadePreviewItems(
        ScheduleDependencyGraph graph,
        ScheduledMatch movedMatch,
        IReadOnlySet<string>? completedMatchNames,
        string eventName)
    {
        var edgesBySource = graph.Edges
            .GroupBy(edge => edge.Source.MatchId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(edge => edge.Dependent.DayLabel, StringComparer.Ordinal)
                    .ThenBy(edge => edge.Dependent.StartTime)
                    .ThenBy(edge => edge.Dependent.Court, StringComparer.Ordinal)
                    .ThenBy(edge => edge.Dependent.MatchName, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);
        var result = new List<ScheduleBoardCascadeMovePreviewItem>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string MatchId, int Depth)>();
        visited.Add(movedMatch.MatchId);
        queue.Enqueue((movedMatch.MatchId, 0));

        while (queue.Count > 0)
        {
            var (sourceMatchId, depth) = queue.Dequeue();
            if (!edgesBySource.TryGetValue(sourceMatchId, out var edges))
            {
                continue;
            }

            foreach (var edge in edges)
            {
                if (!visited.Add(edge.Dependent.MatchId))
                {
                    continue;
                }

                var nextDepth = depth + 1;
                var outcome = ScheduleDependencyGraph.FormatOutcome(edge.Dependency.Outcome);
                result.Add(new ScheduleBoardCascadeMovePreviewItem(
                    nextDepth,
                    eventName,
                    edge.Dependent.MatchName,
                    edge.Dependent.DayLabel,
                    edge.Dependent.StartTime,
                    edge.Dependent.EndTime,
                    edge.Dependent.Court,
                    edge.Dependent.Phase,
                    $"依赖 {edge.Source.MatchName} 的{outcome}",
                    graph.GetRestMinutes(edge),
                    completedMatchNames?.Contains(edge.Dependent.MatchName) == true));
                queue.Enqueue((edge.Dependent.MatchId, nextDepth));
            }
        }

        return result
            .OrderBy(item => item.Depth)
            .ThenBy(item => item.DayLabel, StringComparer.Ordinal)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.Court, StringComparer.Ordinal)
            .ThenBy(item => item.MatchName, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsRelevantMoveIssue(ScheduleConstraintIssue issue, string matchName)
    {
        return string.Equals(issue.MatchName, matchName, StringComparison.Ordinal)
               || issue.Message.Contains(matchName, StringComparison.Ordinal);
    }

    private static string BuildMoveIssueKey(ScheduleConstraintIssue issue)
    {
        return string.Join(
            "\u001F",
            issue.Severity,
            issue.Type,
            issue.Scope,
            issue.DayLabel,
            issue.StartTime?.ToString("HH:mm") ?? "",
            issue.Court ?? "",
            issue.Phase,
            issue.MatchName,
            issue.PlayerName ?? "",
            issue.Message);
    }

    private static IReadOnlyList<string> BuildAffectedMatches(
        string movedMatchName,
        ScheduleConstraintIssue issue)
    {
        return new[] { movedMatchName, issue.MatchName }
            .Where(matchName => !string.IsNullOrWhiteSpace(matchName))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildTimedBracketPath(string scheduleOutputPath, WorkflowExportFormat format)
    {
        var directory = Path.GetDirectoryName(scheduleOutputPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(scheduleOutputPath);
        return Path.ChangeExtension(
            Path.Combine(directory, $"{stem}_带比赛时间和场地对阵表"),
            WorkflowExportHelpers.GetExtension(format));
    }

    private static bool HasExplicitScheduleSides(ScheduledMatch match)
    {
        return !IsOutcomeReference(match.SideA) && !IsOutcomeReference(match.SideB);
    }

    private static bool IsOutcomeReference(string side)
    {
        return side.EndsWith("胜者", StringComparison.Ordinal)
            || side.EndsWith("负者", StringComparison.Ordinal);
    }

    private sealed class BoardDayBuilder
    {
        public TimeOnly? StartTime { get; set; }

        public TimeOnly? EndTime { get; set; }

        public SortedSet<string> Courts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<int> Durations { get; } = [];
    }

    private sealed record CascadeDependencyNode(
        int Depth,
        ScheduleDependencyEdge Edge);

    private sealed record SchedulePlacement(
        string DayLabel,
        TimeOnly StartTime,
        TimeOnly EndTime,
        string Court);
}

public sealed record ScheduleWorkflowRequest(
    DateOnly Date,
    TimeOnly Start,
    TimeOnly End,
    string CourtsText,
    int MatchMinutes,
    int MaxMatchesPerEntrantPerDay,
    int? KnockoutTimingBoundaryEntrants = null,
    int? BeforeBoundaryMatchMinutes = null,
    int? BeforeBoundaryMaxMatchesPerEntrantPerDay = null,
    ScheduleConstraintProfile ConstraintProfile = ScheduleConstraintProfile.Campus);

public sealed record ScheduleDayWorkflowRequest(
    DateOnly Date,
    TimeOnly Start,
    TimeOnly End,
    string Venue,
    string CourtsText,
    IReadOnlyList<ScheduleCourtAvailabilityBlock>? UnavailableCourtWindows = null)
{
    public string DateText => Date.ToString("yyyy-MM-dd");

    public string TimeRange => $"{Start:HH:mm}-{End:HH:mm}";

    public IReadOnlyList<string> Courts => ScheduleWorkflow.ParseCourts(CourtsText);

    public string CourtSummary
    {
        get
        {
            var courts = Courts;
            var resourceCount = UnavailableCourtWindows?.Count ?? 0;
            var resourceText = resourceCount > 0 ? $"；资源 {resourceCount} 条" : "";
            return $"{courts.Count}片：" + string.Join("、", courts.Take(8)) + (courts.Count > 8 ? "…" : "") + resourceText;
        }
    }
}
