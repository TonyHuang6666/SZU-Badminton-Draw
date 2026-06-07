using BadmintonDraw.Core;
using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

public sealed class ScheduleExcelWriter
{
    private const int GridEstimatedCharsPerLine = 14;
    private const double GridLineHeight = 16;
    private const double GridVerticalPadding = 10;
    private const double GridMinBodyRowHeight = 70;

    private static readonly XLColor TitleFill = XLColor.FromHtml("#1F4E78");
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#305496");
    private static readonly XLColor LightHeaderFill = XLColor.FromHtml("#D9EAF7");
    private static readonly XLColor NoteFill = XLColor.FromHtml("#FFF2CC");
    private static readonly XLColor GridFill = XLColor.FromHtml("#F8FAFC");
    private static readonly XLColor PlayInGridFill = XLColor.FromHtml("#FCE4D6");
    private static readonly XLColor Round128GridFill = XLColor.FromHtml("#EAF3FF");
    private static readonly XLColor Round64GridFill = XLColor.FromHtml("#D9EAF7");
    private static readonly XLColor Round32GridFill = XLColor.FromHtml("#E2F0D9");
    private static readonly XLColor Round16GridFill = XLColor.FromHtml("#EDE7F6");
    private static readonly XLColor Round8GridFill = XLColor.FromHtml("#DDEBF7");
    private static readonly XLColor SemiFinalGridFill = XLColor.FromHtml("#E4DFEC");
    private static readonly XLColor FinalGridFill = XLColor.FromHtml("#FFF2CC");
    private static readonly XLColor GrandFinalGridFill = XLColor.FromHtml("#F4CCCC");
    private static readonly XLColor PlacementGridFill = XLColor.FromHtml("#EADCF8");
    private static readonly XLColor[] RoundRobinGridFills =
    [
        XLColor.FromHtml("#EAF3FF"),
        XLColor.FromHtml("#E2F0D9"),
        XLColor.FromHtml("#FFF2CC"),
        XLColor.FromHtml("#FCE4D6"),
        XLColor.FromHtml("#EDE7F6"),
        XLColor.FromHtml("#DDEBF7")
    ];

    public void Write(string outputPath, SchedulePlan plan)
    {
        if (!plan.IsComplete)
        {
            throw new InvalidOperationException(
                $"当前赛程资源不足，仍有 {plan.UnscheduledMatches.Count} 场无法安排；不支持导出不完整赛程。");
        }

        using var workbook = new XLWorkbook();
        WriteDetailSheet(workbook, plan);
        WriteGridSheet(workbook, plan);
        WriteSettingsSheet(workbook, plan);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        workbook.SaveAs(outputPath);
    }

    private static void WriteDetailSheet(XLWorkbook workbook, SchedulePlan plan)
    {
        var sheet = workbook.Worksheets.Add("赛程明细");
        var lastColumn = 10;

        sheet.Range(1, 1, 1, lastColumn).Merge().Value = "比赛赛程明细表";
        sheet.Range(2, 1, 2, lastColumn).Merge().Value =
            $"共 {plan.Matches.Count} 场，{plan.DayCount} 个比赛日，单名选手每日最多 {plan.Settings.MaxMatchesPerEntrantPerDay} 场。";

        var headers = new[] { "序号", "比赛日", "时间", "场地", "组别", "阶段", "场次", "选手/队伍A", "选手/队伍B", "备注" };
        for (var column = 1; column <= headers.Length; column++)
        {
            sheet.Cell(4, column).Value = headers[column - 1];
        }

        for (var i = 0; i < plan.Matches.Count; i++)
        {
            var match = plan.Matches[i];
            var row = i + 5;
            sheet.Cell(row, 1).Value = match.Order;
            sheet.Cell(row, 2).Value = match.DayLabel;
            sheet.Cell(row, 3).Value = match.TimeRange;
            sheet.Cell(row, 4).Value = match.Court;
            sheet.Cell(row, 5).Value = match.GroupName;
            sheet.Cell(row, 6).Value = match.Phase;
            sheet.Cell(row, 7).Value = match.MatchName;
            sheet.Cell(row, 8).Value = match.SideA;
            sheet.Cell(row, 9).Value = match.SideB;
            sheet.Cell(row, 10).Value = match.Note;
        }

        ApplySheetTitleStyle(sheet, lastColumn);
        ApplyTableStyle(sheet.Range(4, 1, Math.Max(5, plan.Matches.Count + 4), lastColumn));
        sheet.Range(4, 1, 4, lastColumn).Style.Fill.BackgroundColor = HeaderFill;
        sheet.Range(4, 1, 4, lastColumn).Style.Font.FontColor = XLColor.White;
        sheet.Range(4, 1, 4, lastColumn).Style.Font.Bold = true;

        foreach (var row in sheet.Rows(5, plan.Matches.Count + 4))
        {
            if (row.Cell(10).GetString().Contains("同单位", StringComparison.Ordinal))
            {
                row.Style.Fill.BackgroundColor = NoteFill;
            }
        }

        sheet.Columns(1, lastColumn).AdjustToContents();
        sheet.Column(7).Width = Math.Max(sheet.Column(7).Width, 18);
        sheet.Column(8).Width = Math.Max(sheet.Column(8).Width, 22);
        sheet.Column(9).Width = Math.Max(sheet.Column(9).Width, 22);
        sheet.SheetView.FreezeRows(4);
    }

    private static void WriteGridSheet(XLWorkbook workbook, SchedulePlan plan)
    {
        var sheet = workbook.Worksheets.Add("时间场地网格");
        var matchByName = plan.Matches
            .GroupBy(match => match.MatchName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var bodyRowHeights = new Dictionary<int, double>();
        var currentRow = 1;

        foreach (var dayGroup in plan.Matches.GroupBy(match => match.DayLabel))
        {
            var dayMatches = dayGroup.ToList();
            var courts = GetCourtsForDay(plan, dayGroup.Key, dayMatches);
            var phaseCells = new List<(IXLCell Cell, string Phase)>();
            var lastColumn = courts.Count + 1;
            sheet.Range(currentRow, 1, currentRow, lastColumn).Merge().Value = $"{dayGroup.Key} 赛程";
            sheet.Range(currentRow, 1, currentRow, lastColumn).Style.Fill.BackgroundColor = TitleFill;
            sheet.Range(currentRow, 1, currentRow, lastColumn).Style.Font.FontColor = XLColor.White;
            sheet.Range(currentRow, 1, currentRow, lastColumn).Style.Font.Bold = true;
            currentRow++;

            sheet.Cell(currentRow, 1).Value = "时间";
            for (var i = 0; i < courts.Count; i++)
            {
                sheet.Cell(currentRow, i + 2).Value = courts[i];
            }

            var headerRow = currentRow;
            currentRow++;

            foreach (var timeGroup in dayMatches.GroupBy(match => match.TimeRange))
            {
                sheet.Cell(currentRow, 1).Value = timeGroup.Key;
                var maxLineCount = 1;
                foreach (var match in timeGroup)
                {
                    var courtIndex = courts
                        .Select((court, index) => (court, index))
                        .First(item => string.Equals(item.court, match.Court, StringComparison.Ordinal))
                        .index;
                    var cell = sheet.Cell(currentRow, courtIndex + 2);
                    var cellText = BuildGridMatchText(match, matchByName);
                    if (!string.IsNullOrWhiteSpace(match.Note))
                    {
                        cellText += $"\n{match.Note}";
                    }

                    cell.Value = cellText;
                    maxLineCount = Math.Max(maxLineCount, EstimateWrappedLineCount(cellText, GridEstimatedCharsPerLine));
                    phaseCells.Add((cell, match.Phase));
                }

                bodyRowHeights[currentRow] = CalculateGridBodyRowHeight(maxLineCount);
                currentRow++;
            }

            ApplyTableStyle(sheet.Range(headerRow, 1, currentRow - 1, lastColumn));
            sheet.Range(headerRow, 1, headerRow, lastColumn).Style.Fill.BackgroundColor = LightHeaderFill;
            sheet.Range(headerRow, 1, headerRow, lastColumn).Style.Font.Bold = true;
            foreach (var (cell, phase) in phaseCells)
            {
                ApplyGridPhaseStyle(cell, phase);
            }

            currentRow += 2;
        }

        sheet.ColumnsUsed().Width = 18;
        sheet.Column(1).Width = 14;
        foreach (var (rowNumber, height) in bodyRowHeights)
        {
            sheet.Row(rowNumber).Height = Math.Max(sheet.Row(rowNumber).Height, height);
        }

        sheet.ShowGridLines = false;
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
    }

    private static string BuildGridMatchText(
        ScheduledMatch match,
        IReadOnlyDictionary<string, ScheduledMatch> matchByName)
    {
        var sideA = BuildGridSideText(match.SideA, match, matchByName);
        var sideB = BuildGridSideText(match.SideB, match, matchByName);
        return $"{match.MatchName}\n{sideA} vs {sideB}";
    }

    private static string BuildGridSideText(
        string side,
        ScheduledMatch currentMatch,
        IReadOnlyDictionary<string, ScheduledMatch> matchByName)
    {
        var outcomeSuffix = side.EndsWith("胜者", StringComparison.Ordinal)
            ? "胜者"
            : side.EndsWith("负者", StringComparison.Ordinal)
                ? "负者"
                : null;
        if (outcomeSuffix is null)
        {
            return side;
        }

        var sourceMatchName = side[..^outcomeSuffix.Length];
        if (!matchByName.TryGetValue(sourceMatchName, out var sourceMatch))
        {
            return side;
        }

        var sourceLabel = sourceMatch.DayLabel == currentMatch.DayLabel
            ? $"{sourceMatch.TimeRange} {sourceMatch.Court}"
            : $"{FormatShortDayLabel(sourceMatch.DayLabel)} {sourceMatch.TimeRange} {sourceMatch.Court}";
        return $"{sourceLabel}{(outcomeSuffix == "胜者" ? "胜" : "负")}";
    }

    private static string FormatShortDayLabel(string dayLabel)
    {
        return DateOnly.TryParse(dayLabel, out var date)
            ? $"{date.Month}/{date.Day}"
            : dayLabel;
    }

    private static int EstimateWrappedLineCount(string text, int estimatedCharsPerLine)
    {
        return text
            .Split('\n')
            .Sum(line => Math.Max(1, (int)Math.Ceiling(MeasureTextWidth(line) / estimatedCharsPerLine)));
    }

    private static double MeasureTextWidth(string text)
    {
        return text.Sum(ch => ch <= 127 ? 0.55 : 1.0);
    }

    private static double CalculateGridBodyRowHeight(int estimatedLineCount)
    {
        return Math.Max(GridMinBodyRowHeight, GridVerticalPadding + estimatedLineCount * GridLineHeight);
    }

    private static void ApplyGridPhaseStyle(IXLCell cell, string phase)
    {
        cell.Style.Fill.BackgroundColor = GetGridPhaseFill(phase);
        cell.Style.Alignment.WrapText = true;

        if (phase.Contains("决赛", StringComparison.Ordinal))
        {
            cell.Style.Font.Bold = true;
        }
    }

    private static XLColor GetGridPhaseFill(string phase)
    {
        if (phase.Contains("首轮", StringComparison.Ordinal))
        {
            return PlayInGridFill;
        }

        if (phase.Contains("总决赛", StringComparison.Ordinal))
        {
            return GrandFinalGridFill;
        }

        if (phase.Contains("名", StringComparison.Ordinal))
        {
            return PlacementGridFill;
        }

        if (phase.Contains("半决赛", StringComparison.Ordinal))
        {
            return SemiFinalGridFill;
        }

        if (phase.Contains("决赛", StringComparison.Ordinal))
        {
            return FinalGridFill;
        }

        if (TryParseRoundFromPhase(phase, out var roundFrom))
        {
            return roundFrom switch
            {
                >= 128 => Round128GridFill,
                >= 64 => Round64GridFill,
                >= 32 => Round32GridFill,
                >= 16 => Round16GridFill,
                >= 8 => Round8GridFill,
                _ => GridFill
            };
        }

        if (TryParseRoundRobinRound(phase, out var round))
        {
            return RoundRobinGridFills[(round - 1) % RoundRobinGridFills.Length];
        }

        return GridFill;
    }

    private static bool TryParseRoundFromPhase(string phase, out int from)
    {
        from = 0;
        var jinIndex = phase.IndexOf('进');
        if (jinIndex <= 0)
        {
            return false;
        }

        var start = jinIndex - 1;
        while (start > 0 && char.IsDigit(phase[start - 1]))
        {
            start--;
        }

        return int.TryParse(phase[start..jinIndex], out from);
    }

    private static bool TryParseRoundRobinRound(string phase, out int round)
    {
        round = 0;
        if (!phase.StartsWith('第') || !phase.EndsWith('轮'))
        {
            return false;
        }

        return int.TryParse(phase[1..^1], out round);
    }

    private static void WriteSettingsSheet(XLWorkbook workbook, SchedulePlan plan)
    {
        var sheet = workbook.Worksheets.Add("赛程参数");
        var rows = new List<(string Key, string Value)>
        {
            ("赛程日数量", plan.Settings.Days.Count.ToString()),
            ("单场比赛耗时", $"{plan.Settings.MatchMinutes} 分钟"),
            ("场次间隔", $"{plan.Settings.BreakMinutes} 分钟"),
            ("单名选手每日最多场次", plan.Settings.MaxMatchesPerEntrantPerDay.ToString()),
            ("生成场次数", plan.Matches.Count.ToString()),
            ("预计比赛日", $"{plan.DayCount} 个")
        };
        rows.AddRange(plan.Settings.Days
            .OrderBy(day => day.Date)
            .Select((day, index) => (
                $"赛程日{index + 1}",
                $"{day.DayLabel} {day.DayStart:HH:mm}-{day.DayEnd:HH:mm}，{day.Courts.Count}片场地：{string.Join("、", day.Courts)}")));

        sheet.Range(1, 1, 1, 2).Merge().Value = "赛程编排参数";
        for (var i = 0; i < rows.Count; i++)
        {
            sheet.Cell(i + 3, 1).Value = rows[i].Key;
            sheet.Cell(i + 3, 2).Value = rows[i].Value;
        }

        ApplySheetTitleStyle(sheet, 2);
        ApplyTableStyle(sheet.Range(3, 1, rows.Count + 2, 2));
        sheet.Column(1).Width = 18;
        sheet.Column(2).Width = 80;
    }

    private static IReadOnlyList<string> GetCourtsForDay(
        SchedulePlan plan,
        string dayLabel,
        IReadOnlyList<ScheduledMatch> dayMatches)
    {
        var configured = plan.Settings.Days.FirstOrDefault(day => day.DayLabel == dayLabel)?.Courts;
        if (configured is { Count: > 0 })
        {
            return configured;
        }

        return dayMatches
            .Select(match => match.Court)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void ApplySheetTitleStyle(IXLWorksheet sheet, int lastColumn)
    {
        sheet.Range(1, 1, 1, lastColumn).Style.Fill.BackgroundColor = TitleFill;
        sheet.Range(1, 1, 1, lastColumn).Style.Font.FontColor = XLColor.White;
        sheet.Range(1, 1, 1, lastColumn).Style.Font.Bold = true;
        sheet.Range(1, 1, 1, lastColumn).Style.Font.FontSize = 16;
        sheet.Range(1, 1, 2, lastColumn).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sheet.Range(1, 1, 2, lastColumn).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Row(1).Height = 30;
        sheet.Row(2).Height = 24;
    }

    private static void ApplyTableStyle(IXLRange range)
    {
        range.Style.Font.FontName = "Microsoft YaHei";
        range.Style.Font.FontSize = 10;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = XLColor.FromHtml("#808080");
        range.Style.Border.InsideBorderColor = XLColor.FromHtml("#808080");
        range.Style.Fill.BackgroundColor = GridFill;
    }
}
