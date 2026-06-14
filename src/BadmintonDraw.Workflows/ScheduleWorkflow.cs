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

    public static SchedulePlan MoveScheduledMatch(
        SchedulePlan schedule,
        string matchName,
        string dayLabel,
        TimeOnly startTime,
        string court,
        IReadOnlySet<string>? lockedMatchNames = null)
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

        var hasCourtOverlap = schedule.Matches.Any(other =>
            !string.Equals(other.MatchName, match.MatchName, StringComparison.Ordinal)
            && string.Equals(other.DayLabel, dayLabel, StringComparison.Ordinal)
            && string.Equals(other.Court, court, StringComparison.OrdinalIgnoreCase)
            && HasTimeOverlap(startTime, endTime, other.StartTime, other.EndTime));
        if (hasCourtOverlap)
        {
            throw new DrawValidationException("目标时间和场地已有比赛，请选择空位后再调整。");
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
        return schedule with { Matches = NormalizeScheduledMatchOrders(matches) };
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
        ScheduleConstraintProfile constraintProfile = ScheduleConstraintProfile.Campus)
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

                return new ScheduleDaySettings(day.Date, day.Start, day.End, ParseCourts(day.CourtsText));
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
            beforeBoundaryTiming)
        {
            ConstraintProfile = constraintProfile
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
                var minutes = (day.DayEnd - day.DayStart).TotalMinutes;
                var slots = Math.Max(0, (int)Math.Floor(minutes / minutesPerMatch));
                return $"{day.DayLabel} {day.Courts.Count}片/{slots * day.Courts.Count}场";
            }));
        }

        if (!settings.HasKnockoutTimingSplit)
        {
            return $"每日上限{settings.MaxMatchesPerEntrantPerDay}场；{BuildCapacity(settings.MatchMinutes)}";
        }

        return $"分界线前每日上限{settings.BeforeBoundaryTiming!.MaxMatchesPerEntrantPerDay}场、每场{settings.BeforeBoundaryTiming.MatchMinutes}分钟：{BuildCapacity(settings.BeforeBoundaryTiming.MatchMinutes)}；"
            + $"分界线后每日上限{settings.MaxMatchesPerEntrantPerDay}场、每场{settings.MatchMinutes}分钟：{BuildCapacity(settings.MatchMinutes)}";
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
    string CourtsText)
{
    public string DateText => Date.ToString("yyyy-MM-dd");

    public string TimeRange => $"{Start:HH:mm}-{End:HH:mm}";

    public IReadOnlyList<string> Courts => ScheduleWorkflow.ParseCourts(CourtsText);

    public string CourtSummary
    {
        get
        {
            var courts = Courts;
            return $"{courts.Count}片：" + string.Join("、", courts.Take(8)) + (courts.Count > 8 ? "…" : "");
        }
    }
}
