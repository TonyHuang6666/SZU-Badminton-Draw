using BadmintonDraw.Core;
using ClosedXML.Excel;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BadmintonDraw.Excel;

public sealed class ScoreSheetExcelWriter
{
    private const int TeamBlockRows = 18;
    private const float ScoreSheetPdfLeftInset = 42f;
    private const float ScoreSheetPdfRightInset = 4f;
    private const string IndividualScoreSheetTemplateResourceName =
        "BadmintonDraw.Excel.Templates.IndividualScoreSheetTemplate.xlsx";

    private static readonly XLColor TitleFill = XLColor.FromHtml("#1F4E78");
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#305496");
    private static readonly XLColor LightHeaderFill = XLColor.FromHtml("#D9EAF7");
    private static readonly XLColor EditableFill = XLColor.FromHtml("#FFFFFF");
    private static readonly XLColor NoteFill = XLColor.FromHtml("#FFF2CC");
    private static readonly XLColor FormFill = XLColor.FromHtml("#F8FAFC");

    public void WriteIndividualMatchScorePdf(
        string outputPath,
        SchedulePlan plan,
        string projectName,
        string? dayLabel = null,
        IReadOnlyDictionary<string, MatchRecordResult>? completedResults = null,
        IReadOnlyCollection<string>? carryOverMatchNames = null)
    {
        EnsureCompleteSchedule(plan);

        var matches = SelectMatches(plan, dayLabel, carryOverMatchNames);
        var scheduleByName = plan.Matches
            .GroupBy(match => match.MatchName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var carryOverSet = carryOverMatchNames is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : carryOverMatchNames.ToHashSet(StringComparer.Ordinal);

        using var templateWorkbook = LoadIndividualScoreSheetTemplate();
        using var workbook = new XLWorkbook();
        var sheetNames = new List<string>();
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var data = BuildIndividualScoreSheetData(
                match,
                scheduleByName,
                completedResults,
                isCarryOver: !string.IsNullOrWhiteSpace(dayLabel)
                    && carryOverSet.Contains(match.MatchName)
                    && match.DayLabel != dayLabel,
                dayLabel,
                projectName);
            var sheet = templateWorkbook.Worksheet(1).CopyTo(workbook, $"计分表{index + 1}");
            sheetNames.Add(sheet.Name);
            FillIndividualScoreSheetTemplate(sheet, data);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        var tempWorkbookPath = Path.Combine(
            Path.GetTempPath(),
            $"badminton-score-sheets-{Guid.NewGuid():N}.xlsx");
        try
        {
            workbook.SaveAs(tempWorkbookPath);
            new DrawResultVisualWriter().WriteSheetsA4Pdf(
                outputPath,
                tempWorkbookPath,
                sheetNames,
                stretchToPrintableArea: true,
                horizontalSafetyInset: 0f,
                leftPageInset: ScoreSheetPdfLeftInset,
                rightPageInset: ScoreSheetPdfRightInset);
        }
        finally
        {
            if (File.Exists(tempWorkbookPath))
            {
                File.Delete(tempWorkbookPath);
            }
        }
    }

    public void WriteTeamScoreSheets(
        string outputPath,
        SchedulePlan plan,
        string? dayLabel = null,
        IReadOnlyDictionary<string, MatchRecordResult>? completedResults = null,
        IReadOnlyCollection<string>? carryOverMatchNames = null)
    {
        EnsureCompleteSchedule(plan);

        var matches = SelectMatches(plan, dayLabel, carryOverMatchNames);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("团体记分表");
        WriteTeamMatchBlocks(sheet, plan, matches, dayLabel, completedResults, carryOverMatchNames);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        workbook.SaveAs(outputPath);
    }

    private static void EnsureCompleteSchedule(SchedulePlan plan)
    {
        if (!plan.IsComplete)
        {
            throw new InvalidOperationException(
                $"当前赛程资源不足，仍有 {plan.UnscheduledMatches.Count} 场无法安排；不支持导出不完整赛程。");
        }
    }

    private static IReadOnlyList<ScheduledMatch> SelectMatches(
        SchedulePlan plan,
        string? dayLabel,
        IReadOnlyCollection<string>? carryOverMatchNames)
    {
        var carryOverSet = carryOverMatchNames is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : carryOverMatchNames.ToHashSet(StringComparer.Ordinal);
        return plan.Matches
            .Where(match => string.IsNullOrWhiteSpace(dayLabel)
                || match.DayLabel == dayLabel
                || carryOverSet.Contains(match.MatchName))
            .OrderByDescending(match => !string.IsNullOrWhiteSpace(dayLabel)
                && carryOverSet.Contains(match.MatchName)
                && match.DayLabel != dayLabel)
            .ThenBy(match => match.Order)
            .ToList();
    }

    private static IndividualScoreSheetData BuildIndividualScoreSheetData(
        ScheduledMatch match,
        IReadOnlyDictionary<string, ScheduledMatch> scheduleByName,
        IReadOnlyDictionary<string, MatchRecordResult>? completedResults,
        bool isCarryOver,
        string? carryOverDayLabel,
        string projectName)
    {
        var displayDayLabel = isCarryOver && !string.IsNullOrWhiteSpace(carryOverDayLabel)
            ? carryOverDayLabel
            : match.DayLabel;
        var startTimeLabel = isCarryOver ? "待安排" : $"{match.StartTime:HH:mm}";
        var court = isCarryOver ? "待安排" : match.Court;

        return new IndividualScoreSheetData(
            NormalizeProjectName(projectName),
            string.IsNullOrWhiteSpace(match.Phase) ? match.MatchName : match.Phase,
            FormatDateLabel(displayDayLabel),
            startTimeLabel,
            court,
            match.Order,
            ResolveScoreSheetPlayers(match.SideA, match, scheduleByName, completedResults),
            ResolveScoreSheetPlayers(match.SideB, match, scheduleByName, completedResults));
    }

    private static XLWorkbook LoadIndividualScoreSheetTemplate()
    {
        var resourceStream = typeof(ScoreSheetExcelWriter).GetTypeInfo()
            .Assembly
            .GetManifestResourceStream(IndividualScoreSheetTemplateResourceName);
        if (resourceStream is null)
        {
            throw new InvalidOperationException($"缺少内置单场计分表模板：{IndividualScoreSheetTemplateResourceName}");
        }

        return new XLWorkbook(resourceStream);
    }

    private static void FillIndividualScoreSheetTemplate(IXLWorksheet sheet, IndividualScoreSheetData data)
    {
        sheet.Cell("B8").Value = data.ProjectName;
        sheet.Cell("B14").Value = data.Stage;
        sheet.Cell("B20").Value = data.DateLabel;
        sheet.Cell("B26").Value = data.StartTimeLabel;
        sheet.Cell("AS8").Value = data.Court;
        sheet.Cell("B32").Value = data.RecordNumber;
        sheet.Cell("AV26").Value = "分";
        sheet.Cell("AV26").Style.Alignment.WrapText = false;
        FillCompetitorRows(sheet, data.SideAPlayers, data.SideBPlayers);
    }

    private static void FillCompetitorRows(
        IXLWorksheet sheet,
        IReadOnlyList<string> sideAPlayers,
        IReadOnlyList<string> sideBPlayers)
    {
        foreach (var firstRow in new[] { 39, 44, 49 })
        {
            sheet.Cell(firstRow, 1).Value = GetPlayerLine(sideAPlayers, 0);
            sheet.Cell(firstRow + 1, 1).Value = GetPlayerLine(sideAPlayers, 1);
            sheet.Cell(firstRow + 2, 1).Value = GetPlayerLine(sideBPlayers, 0);
            sheet.Cell(firstRow + 3, 1).Value = GetPlayerLine(sideBPlayers, 1);
        }
    }

    private static string GetPlayerLine(IReadOnlyList<string> players, int index)
    {
        return index < players.Count ? players[index] : "";
    }

    private static IReadOnlyList<string> ResolveScoreSheetPlayers(
        string side,
        ScheduledMatch currentMatch,
        IReadOnlyDictionary<string, ScheduledMatch> scheduleByName,
        IReadOnlyDictionary<string, MatchRecordResult>? completedResults)
    {
        if (ScheduleMatchText.TryParseOutcomeReference(side, out var sourceMatchName, out _)
            && (completedResults is null || !completedResults.ContainsKey(sourceMatchName)))
        {
            return [];
        }

        return SplitCompetitorNames(ScheduleMatchText.ResolveSide(side, currentMatch, scheduleByName, completedResults));
    }

    private static IReadOnlyList<string> SplitCompetitorNames(string side)
    {
        var normalized = ScheduleMatchText.NormalizeCompetitorName(side);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [""];
        }

        if (ScheduleMatchText.TryParseOutcomeReference(normalized, out _, out _))
        {
            return [];
        }

        var parts = Regex.Split(normalized, @"[\s,，、/／]+")
            .Select(part => part.Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Take(2)
            .ToList();
        return parts.Count == 0 ? [normalized] : parts;
    }

    private static string NormalizeProjectName(string projectName)
    {
        return string.IsNullOrWhiteSpace(projectName)
            ? "羽毛球"
            : projectName.Trim();
    }

    private static string FormatDateLabel(string dayLabel)
    {
        return DateOnly.TryParse(dayLabel, out var date)
            ? date.ToString("yyyy-MM-dd")
            : dayLabel;
    }

    private static void WriteTeamMatchBlocks(
        IXLWorksheet sheet,
        SchedulePlan plan,
        IReadOnlyList<ScheduledMatch> matches,
        string? dayLabel,
        IReadOnlyDictionary<string, MatchRecordResult>? completedResults,
        IReadOnlyCollection<string>? carryOverMatchNames)
    {
        PrepareSheet(sheet, 7);
        var scheduleByName = plan.Matches
            .GroupBy(match => match.MatchName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var carryOverSet = carryOverMatchNames is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : carryOverMatchNames.ToHashSet(StringComparer.Ordinal);

        if (matches.Count == 0)
        {
            WriteEmptySheet(sheet, "团体赛记分表", "当前比赛日没有可导出的团体比赛。", lastColumn: 7);
            return;
        }

        for (var index = 0; index < matches.Count; index++)
        {
            var startRow = index * TeamBlockRows + 1;
            WriteTeamBlock(
                sheet,
                startRow,
                matches[index],
                scheduleByName,
                completedResults,
                isCarryOver: !string.IsNullOrWhiteSpace(dayLabel)
                    && carryOverSet.Contains(matches[index].MatchName)
                    && matches[index].DayLabel != dayLabel,
                dayLabel);

            if (index < matches.Count - 1)
            {
                sheet.PageSetup.AddHorizontalPageBreak(startRow + TeamBlockRows - 1);
            }
        }

        sheet.PageSetup.PrintAreas.Add($"A1:G{matches.Count * TeamBlockRows}");
        sheet.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
        sheet.PageSetup.FitToPages(1, 0);
    }

    private static void WriteTeamBlock(
        IXLWorksheet sheet,
        int startRow,
        ScheduledMatch match,
        IReadOnlyDictionary<string, ScheduledMatch> scheduleByName,
        IReadOnlyDictionary<string, MatchRecordResult>? completedResults,
        bool isCarryOver,
        string? carryOverDayLabel)
    {
        var displayDayLabel = isCarryOver && !string.IsNullOrWhiteSpace(carryOverDayLabel)
            ? carryOverDayLabel
            : match.DayLabel;
        var timeRange = isCarryOver ? "待安排" : match.TimeRange;
        var court = isCarryOver ? "待安排" : match.Court;
        var sideA = ScheduleMatchText.NormalizeCompetitorName(
            ScheduleMatchText.ResolveSide(match.SideA, match, scheduleByName, completedResults));
        var sideB = ScheduleMatchText.NormalizeCompetitorName(
            ScheduleMatchText.ResolveSide(match.SideB, match, scheduleByName, completedResults));

        sheet.Range(startRow, 1, startRow, 7).Merge().Value = "（            ）团体赛记分表";
        sheet.Range(startRow + 1, 1, startRow + 1, 7).Merge().Value = $"{sideA} 队  对  {sideB} 队";
        sheet.Range(startRow + 2, 1, startRow + 3, 7).Style.Fill.BackgroundColor = EditableFill;

        WriteRow(sheet, startRow + 3, 1, "阶段", "组别（位置号）", "日期", "时间", "场号");
        sheet.Cell(startRow + 4, 1).Value = match.Phase;
        sheet.Cell(startRow + 4, 2).Value = match.GroupName;
        sheet.Cell(startRow + 4, 3).Value = displayDayLabel;
        sheet.Cell(startRow + 4, 4).Value = timeRange;
        sheet.Cell(startRow + 4, 5).Value = court;

        sheet.Range(startRow + 6, 1, startRow + 6, 7).Merge().Value = "分场记录";
        WriteRow(sheet, startRow + 7, 1, "场序", "项目/单项", "A队出场", "B队出场", "比分", "胜方", "备注");
        for (var i = 0; i < 5; i++)
        {
            sheet.Cell(startRow + 8 + i, 1).Value = $"第{i + 1}场";
        }

        sheet.Cell(startRow + 14, 1).Value = "比赛结果";
        sheet.Range(startRow + 14, 2, startRow + 14, 3).Merge();
        sheet.Cell(startRow + 14, 4).Value = "获胜队";
        sheet.Range(startRow + 14, 5, startRow + 14, 6).Merge();
        sheet.Cell(startRow + 14, 7).Value = "裁判长签名";
        sheet.Cell(startRow + 16, 1).Value = "备注";
        sheet.Range(startRow + 16, 2, startRow + 16, 7).Merge().Value =
            isCarryOver ? $"顺延补赛；{match.Note}".TrimEnd('；') : match.Note;

        ApplyBlockStyle(sheet, startRow, TeamBlockRows, 7);
        sheet.Range(startRow, 1, startRow, 7).Style.Fill.BackgroundColor = TitleFill;
        sheet.Range(startRow, 1, startRow, 7).Style.Font.FontColor = XLColor.White;
        sheet.Range(startRow + 1, 1, startRow + 1, 7).Style.Fill.BackgroundColor = LightHeaderFill;
        sheet.Range(startRow + 3, 1, startRow + 3, 5).Style.Fill.BackgroundColor = HeaderFill;
        sheet.Range(startRow + 3, 1, startRow + 3, 5).Style.Font.FontColor = XLColor.White;
        sheet.Range(startRow + 6, 1, startRow + 7, 7).Style.Fill.BackgroundColor = LightHeaderFill;
        sheet.Range(startRow + 8, 2, startRow + 14, 7).Style.Fill.BackgroundColor = EditableFill;
        sheet.Range(startRow + 16, 2, startRow + 16, 7).Style.Fill.BackgroundColor = EditableFill;

        sheet.Row(startRow).Height = 28;
        sheet.Row(startRow + 1).Height = 30;
        sheet.Rows(startRow + 8, startRow + 12).Height = 28;
        sheet.Row(startRow + 14).Height = 32;
    }

    private static void PrepareSheet(IXLWorksheet sheet, int lastColumn)
    {
        sheet.ShowGridLines = false;
        sheet.Style.Font.FontName = "Microsoft YaHei";
        sheet.Style.Font.FontSize = 10;
        for (var column = 1; column <= lastColumn; column++)
        {
            sheet.Column(column).Width = column == 1 ? 10 : 15;
        }
    }

    private static void WriteEmptySheet(IXLWorksheet sheet, string title, string message, int lastColumn)
    {
        sheet.Range(1, 1, 1, lastColumn).Merge().Value = title;
        sheet.Range(2, 1, 2, lastColumn).Merge().Value = message;
        ApplyBlockStyle(sheet, 1, 4, lastColumn);
        sheet.Range(1, 1, 1, lastColumn).Style.Fill.BackgroundColor = TitleFill;
        sheet.Range(1, 1, 1, lastColumn).Style.Font.FontColor = XLColor.White;
    }

    private static void WriteRow(IXLWorksheet sheet, int row, int firstColumn, params string[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            sheet.Cell(row, firstColumn + index).Value = values[index];
        }
    }

    private static void ApplyBlockStyle(IXLWorksheet sheet, int startRow, int rowCount, int lastColumn)
    {
        var range = sheet.Range(startRow, 1, startRow + rowCount - 1, lastColumn);
        range.Style.Fill.BackgroundColor = FormFill;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = XLColor.FromHtml("#808080");
        range.Style.Border.InsideBorderColor = XLColor.FromHtml("#B7C0D0");
        sheet.Range(startRow, 1, startRow + rowCount - 1, lastColumn).Style.Font.Bold = false;
        sheet.Range(startRow, 1, startRow, lastColumn).Style.Font.Bold = true;
        sheet.Range(startRow + 1, 1, startRow + 1, lastColumn).Style.Font.Bold = true;
        sheet.Range(startRow + rowCount - 2, 1, startRow + rowCount - 1, lastColumn).Style.Fill.BackgroundColor = NoteFill;
    }

    private sealed record IndividualScoreSheetData(
        string ProjectName,
        string Stage,
        string DateLabel,
        string StartTimeLabel,
        string Court,
        int RecordNumber,
        IReadOnlyList<string> SideAPlayers,
        IReadOnlyList<string> SideBPlayers);
}
