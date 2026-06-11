using BadmintonDraw.Core;
using ClosedXML.Excel;
using System.Text.RegularExpressions;

namespace BadmintonDraw.Excel;

public sealed class ScheduleExcelWriter
{
    private const int GridEstimatedCharsPerLine = 14;
    private const double GridLineHeight = 16;
    private const double GridVerticalPadding = 10;
    private const double GridMinBodyRowHeight = 70;
    private const int RecordHeaderRow = 4;
    private const int RecordExampleRow = 5;
    private const int RecordFirstDataRow = 6;
    private const int RecordSideAColumn = 6;
    private const int RecordVsColumn = 7;
    private const int RecordSideBColumn = 8;
    private const int RecordScoreColumn = 9;
    private const int RecordDurationColumn = 10;
    private const int RecordCourtColumn = 11;
    private const int RecordWinnerColumn = 12;
    private const int RecordNoteColumn = 13;
    private const int RecordMatchIdColumn = 14;
    private const int RecordWinnerOptionAColumn = 15;
    private const int RecordWinnerOptionBColumn = 16;
    private const int RecordTournamentIdColumn = 17;

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
        EnsureCompleteSchedule(plan);

        using var workbook = new XLWorkbook();
        WriteDetailSheet(workbook, plan);
        WriteGridSheet(workbook, plan);
        WriteMatchRecordSheet(workbook, plan);
        WriteSettingsSheet(workbook, plan);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        workbook.SaveAs(outputPath);
    }

    public void WriteMatchRecord(
        string outputPath,
        SchedulePlan plan,
        string? dayLabel = null,
        IReadOnlyDictionary<string, MatchRecordResult>? completedResults = null,
        IReadOnlyCollection<string>? carryOverMatchNames = null,
        string? tournamentId = null)
    {
        EnsureCompleteSchedule(plan);

        using var workbook = new XLWorkbook();
        WriteMatchRecordSheet(workbook, plan, dayLabel, completedResults, carryOverMatchNames, tournamentId);

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

    private static void WriteMatchRecordSheet(
        XLWorkbook workbook,
        SchedulePlan plan,
        string? dayLabel = null,
        IReadOnlyDictionary<string, MatchRecordResult>? completedResults = null,
        IReadOnlyCollection<string>? carryOverMatchNames = null,
        string? tournamentId = null)
    {
        var sheet = workbook.Worksheets.Add("对阵记录表");
        completedResults ??= new Dictionary<string, MatchRecordResult>(StringComparer.Ordinal);
        var carryOverSet = carryOverMatchNames is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : carryOverMatchNames.ToHashSet(StringComparer.Ordinal);
        var recordMatches = plan.Matches
            .Where(match => string.IsNullOrWhiteSpace(dayLabel)
                || match.DayLabel == dayLabel
                || carryOverSet.Contains(match.MatchName))
            .OrderByDescending(match => carryOverSet.Contains(match.MatchName) && match.DayLabel != dayLabel)
            .ThenBy(match => match.Order)
            .ToList();
        var rowByMatchName = recordMatches
            .Select((match, index) => (match.MatchName, Row: RecordFirstDataRow + index))
            .GroupBy(item => item.MatchName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Row, StringComparer.Ordinal);
        var lastVisibleColumn = RecordMatchIdColumn;
        var lastRow = Math.Max(RecordExampleRow, RecordFirstDataRow + recordMatches.Count - 1);

        sheet.Range(1, 1, 1, lastVisibleColumn).Merge().Value = BuildRecordTitle(dayLabel);
        sheet.Range(2, 1, 2, lastVisibleColumn).Merge().Value =
            "第5行为填写示例；比分、用时首次导出时留空。胜方可点击下拉选择，后续占空对阵会随前序胜负自动更新。";

        WriteRecordHeaders(sheet);
        WriteRecordExampleRow(sheet);

        for (var i = 0; i < recordMatches.Count; i++)
        {
            var isCarryOver = !string.IsNullOrWhiteSpace(dayLabel)
                && carryOverSet.Contains(recordMatches[i].MatchName)
                && recordMatches[i].DayLabel != dayLabel;
            WriteRecordMatchRow(
                sheet,
                RecordFirstDataRow + i,
                recordMatches[i],
                rowByMatchName,
                completedResults,
                isCarryOver,
                dayLabel,
                tournamentId);
        }

        ApplySheetTitleStyle(sheet, lastVisibleColumn);
        ApplyTableStyle(sheet.Range(RecordHeaderRow, 1, lastRow, lastVisibleColumn));
        sheet.Range(RecordHeaderRow, 1, RecordHeaderRow, lastVisibleColumn).Style.Fill.BackgroundColor = HeaderFill;
        sheet.Range(RecordHeaderRow, 1, RecordHeaderRow, lastVisibleColumn).Style.Font.FontColor = XLColor.White;
        sheet.Range(RecordHeaderRow, 1, RecordHeaderRow, lastVisibleColumn).Style.Font.Bold = true;
        sheet.Range(RecordExampleRow, 1, RecordExampleRow, lastVisibleColumn).Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F2F2");
        sheet.Range(RecordExampleRow, 1, RecordExampleRow, lastVisibleColumn).Style.Font.Italic = true;
        sheet.Range(RecordExampleRow, 1, RecordExampleRow, lastVisibleColumn).Style.Font.FontColor = XLColor.FromHtml("#5B677A");
        sheet.Range(RecordExampleRow, RecordScoreColumn, lastRow, RecordWinnerColumn).Style.Fill.BackgroundColor = XLColor.White;

        if (recordMatches.Count > 0)
        {
            foreach (var row in sheet.Rows(RecordFirstDataRow, lastRow))
            {
                var phase = row.Cell(4).GetString();
                row.Cell(4).Style.Fill.BackgroundColor = GetGridPhaseFill(phase);
            }
        }

        sheet.Columns(RecordSideAColumn, RecordSideBColumn).Style.Font.Bold = true;
        sheet.Columns(RecordSideAColumn, RecordSideBColumn).Style.Alignment.WrapText = true;
        sheet.Columns(RecordSideAColumn, RecordSideBColumn).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sheet.Column(RecordVsColumn).Style.Font.Bold = true;
        sheet.Column(RecordVsColumn).Style.Font.FontSize = 12;
        sheet.Column(RecordVsColumn).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        sheet.Column(1).Width = 7;
        sheet.Column(2).Width = 12;
        sheet.Column(3).Width = 15;
        sheet.Column(4).Width = 12;
        sheet.Column(5).Width = 10;
        sheet.Column(RecordSideAColumn).Width = 26;
        sheet.Column(RecordVsColumn).Width = 6;
        sheet.Column(RecordSideBColumn).Width = 26;
        sheet.Column(RecordScoreColumn).Width = 18;
        sheet.Column(RecordDurationColumn).Width = 10;
        sheet.Column(RecordCourtColumn).Width = 10;
        sheet.Column(RecordWinnerColumn).Width = 24;
        sheet.Column(RecordNoteColumn).Width = 24;
        sheet.Column(RecordMatchIdColumn).Width = 26;
        sheet.Column(RecordMatchIdColumn).Hide();
        sheet.Column(RecordWinnerOptionAColumn).Hide();
        sheet.Column(RecordWinnerOptionBColumn).Hide();
        sheet.Column(RecordTournamentIdColumn).Hide();
        sheet.Rows(RecordExampleRow, lastRow).Height = 42;
        sheet.SheetView.FreezeRows(RecordHeaderRow);
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
    }

    private static void WriteRecordHeaders(IXLWorksheet sheet)
    {
        sheet.Cell(RecordHeaderRow, 1).Value = "序号";
        sheet.Cell(RecordHeaderRow, 2).Value = "日期";
        sheet.Cell(RecordHeaderRow, 3).Value = "时间";
        sheet.Cell(RecordHeaderRow, 4).Value = "进度";
        sheet.Cell(RecordHeaderRow, 5).Value = "组别";
        sheet.Range(RecordHeaderRow, RecordSideAColumn, RecordHeaderRow, RecordSideBColumn).Merge().Value = "对阵数据";
        sheet.Cell(RecordHeaderRow, RecordScoreColumn).Value = "比分";
        sheet.Cell(RecordHeaderRow, RecordDurationColumn).Value = "用时";
        sheet.Cell(RecordHeaderRow, RecordCourtColumn).Value = "场地";
        sheet.Cell(RecordHeaderRow, RecordWinnerColumn).Value = "胜方";
        sheet.Cell(RecordHeaderRow, RecordNoteColumn).Value = "备注";
        sheet.Cell(RecordHeaderRow, RecordMatchIdColumn).Value = "场次标识";
        sheet.Cell(RecordHeaderRow, RecordTournamentIdColumn).Value = "赛事标识";
    }

    private static void WriteRecordExampleRow(IXLWorksheet sheet)
    {
        sheet.Cell(RecordExampleRow, 1).Value = "示例";
        sheet.Cell(RecordExampleRow, 2).Value = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
        sheet.Cell(RecordExampleRow, 3).Value = "14:00-14:20";
        sheet.Cell(RecordExampleRow, 4).Value = "首轮赛";
        sheet.Cell(RecordExampleRow, 5).Value = "A组";
        sheet.Cell(RecordExampleRow, RecordSideAColumn).Value = "A【张三\n李四】";
        sheet.Cell(RecordExampleRow, RecordVsColumn).Value = "vs";
        sheet.Cell(RecordExampleRow, RecordSideBColumn).Value = "B【王五\n赵六】";
        sheet.Cell(RecordExampleRow, RecordScoreColumn).Value = "15-10, 15-12";
        sheet.Cell(RecordExampleRow, RecordDurationColumn).Value = "18m";
        sheet.Cell(RecordExampleRow, RecordCourtColumn).Value = "B1";
        sheet.Cell(RecordExampleRow, RecordWinnerColumn).Value = "A【张三 李四】";
        sheet.Cell(RecordExampleRow, RecordNoteColumn).Value = "胜者进入下一轮";
        sheet.Cell(RecordExampleRow, RecordWinnerOptionAColumn).Value = "A【张三 李四】";
        sheet.Cell(RecordExampleRow, RecordWinnerOptionBColumn).Value = "B【王五 赵六】";
        ApplyRecordWinnerValidation(sheet, RecordExampleRow);
    }

    private static void WriteRecordMatchRow(
        IXLWorksheet sheet,
        int row,
        ScheduledMatch match,
        IReadOnlyDictionary<string, int> rowByMatchName,
        IReadOnlyDictionary<string, MatchRecordResult> completedResults,
        bool isCarryOver = false,
        string? carryOverDayLabel = null,
        string? tournamentId = null)
    {
        sheet.Cell(row, 1).Value = match.Order;
        sheet.Cell(row, 2).Value = isCarryOver && !string.IsNullOrWhiteSpace(carryOverDayLabel)
            ? carryOverDayLabel
            : match.DayLabel;
        sheet.Cell(row, 3).Value = isCarryOver ? "待安排" : match.TimeRange;
        sheet.Cell(row, 4).Value = match.Phase;
        sheet.Cell(row, 5).Value = match.GroupName;
        sheet.Cell(row, RecordVsColumn).Value = "vs";
        sheet.Cell(row, RecordCourtColumn).Value = isCarryOver ? "待安排" : match.Court;
        sheet.Cell(row, RecordNoteColumn).Value = isCarryOver
            ? $"顺延补赛；{match.Note}".TrimEnd('；')
            : match.Note;
        sheet.Cell(row, RecordMatchIdColumn).Value = match.MatchName;
        sheet.Cell(row, RecordTournamentIdColumn).Value = tournamentId ?? "";
        WriteRecordSide(sheet, row, RecordSideAColumn, RecordWinnerOptionAColumn, "A", match.SideA, rowByMatchName, completedResults);
        WriteRecordSide(sheet, row, RecordSideBColumn, RecordWinnerOptionBColumn, "B", match.SideB, rowByMatchName, completedResults);
        ApplyRecordWinnerValidation(sheet, row);
    }

    private static void WriteRecordSide(
        IXLWorksheet sheet,
        int row,
        int displayColumn,
        int optionColumn,
        string prefix,
        string side,
        IReadOnlyDictionary<string, int> rowByMatchName,
        IReadOnlyDictionary<string, MatchRecordResult> completedResults)
    {
        if (TryParseOutcomeReference(side, out var sourceMatchName, out var outcome))
        {
            if (completedResults.TryGetValue(sourceMatchName, out var result))
            {
                var resolvedSide = BuildRecordSideText(prefix, outcome == "胜者" ? result.Winner : result.Loser);
                sheet.Cell(row, displayColumn).Value = resolvedSide.DisplayText;
                sheet.Cell(row, optionColumn).Value = resolvedSide.OptionText;
                return;
            }

            if (rowByMatchName.TryGetValue(sourceMatchName, out var sourceRow))
            {
                sheet.Cell(row, optionColumn).FormulaA1 = BuildRecordResolvedOptionFormula(prefix, side, sourceRow, outcome);
                sheet.Cell(row, displayColumn).FormulaA1 =
                    $"SUBSTITUTE({RecordCellAddress(row, optionColumn)},\" \",CHAR(10))";
                return;
            }
        }

        var recordSide = BuildRecordSideText(prefix, side);
        sheet.Cell(row, displayColumn).Value = recordSide.DisplayText;
        sheet.Cell(row, optionColumn).Value = recordSide.OptionText;
    }

    private static void ApplyRecordWinnerValidation(IXLWorksheet sheet, int row)
    {
        var validation = sheet.Cell(row, RecordWinnerColumn).CreateDataValidation();
        validation.List($"=$O${row}:$P${row}", inCellDropdown: true);
        validation.IgnoreBlanks = true;
        validation.ShowInputMessage = true;
        validation.InputTitle = "选择胜方";
        validation.InputMessage = "请选择本场获胜的一方。";
        validation.ShowErrorMessage = true;
        validation.ErrorTitle = "胜方不在候选列表中";
        validation.ErrorMessage = "请从下拉列表选择左侧或右侧选手/组合。";
    }

    private static string BuildRecordResolvedOptionFormula(
        string prefix,
        string unresolvedSide,
        int sourceRow,
        string outcome)
    {
        var unresolvedOption = ExcelStringLiteral(BuildRecordSideText(prefix, unresolvedSide).OptionText);
        var sourceWinner = RecordCellAddress(sourceRow, RecordWinnerColumn);
        var sourceOptionA = RecordCellAddress(sourceRow, RecordWinnerOptionAColumn);
        var sourceOptionB = RecordCellAddress(sourceRow, RecordWinnerOptionBColumn);

        if (outcome == "胜者")
        {
            return $"IF({sourceWinner}=\"\",{unresolvedOption},{BuildRecordRePrefixFormula(prefix, sourceWinner)})";
        }

        return
            $"IF({sourceWinner}=\"\",{unresolvedOption}," +
            $"IF({sourceWinner}={sourceOptionA},{BuildRecordRePrefixFormula(prefix, sourceOptionB)}," +
            $"IF({sourceWinner}={sourceOptionB},{BuildRecordRePrefixFormula(prefix, sourceOptionA)},{unresolvedOption})))";
    }

    private static string BuildRecordRePrefixFormula(string prefix, string sourceCellReference)
    {
        return
            $"{ExcelStringLiteral($"{prefix}【")}&" +
            $"MID({sourceCellReference},FIND(\"【\",{sourceCellReference})+1,FIND(\"】\",{sourceCellReference})-FIND(\"【\",{sourceCellReference})-1)&" +
            $"{ExcelStringLiteral("】")}";
    }

    private static bool TryParseOutcomeReference(string side, out string sourceMatchName, out string outcome)
    {
        if (side.EndsWith("胜者", StringComparison.Ordinal))
        {
            sourceMatchName = side[..^"胜者".Length];
            outcome = "胜者";
            return true;
        }

        if (side.EndsWith("负者", StringComparison.Ordinal))
        {
            sourceMatchName = side[..^"负者".Length];
            outcome = "负者";
            return true;
        }

        sourceMatchName = "";
        outcome = "";
        return false;
    }

    private static string ExcelStringLiteral(string value)
    {
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string RecordCellAddress(int row, int column)
    {
        return $"${GetColumnLetter(column)}${row}";
    }

    private static string GetColumnLetter(int column)
    {
        var value = column;
        var chars = new Stack<char>();
        while (value > 0)
        {
            value--;
            chars.Push((char)('A' + value % 26));
            value /= 26;
        }

        return new string(chars.ToArray());
    }

    private static string BuildRecordTitle(string? dayLabel)
    {
        return DateOnly.TryParse(dayLabel, out var date)
            ? $"{date.Month}月{date.Day}日赛程记录表"
            : "深大羽协比赛对阵记录表";
    }

    private static (string DisplayText, string OptionText) BuildRecordSideText(string prefix, string side)
    {
        var (normalized, isBracketedPair) = NormalizeRecordSideText(side);
        var display = isBracketedPair ? normalized.Replace(" ", "\n", StringComparison.Ordinal) : normalized;
        return ($"{prefix}【{display}】", $"{prefix}【{normalized}】");
    }

    private static (string Text, bool IsBracketedPair) NormalizeRecordSideText(string side)
    {
        var trimmed = side.Trim();
        var isBracketedPair = false;
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            trimmed = trimmed[1..^1].Trim();
            isBracketedPair = true;
        }

        return (Regex.Replace(trimmed, @"\s+", " "), isBracketedPair);
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
            ("生成场次数", plan.Matches.Count.ToString()),
            ("预计比赛日", $"{plan.DayCount} 个")
        };
        if (plan.Settings.HasKnockoutTimingSplit)
        {
            rows.InsertRange(1, new[]
            {
                ("赛程分界线", $"{plan.Settings.KnockoutTimingBoundaryEntrants}强"),
                ("分界线前单场耗时", $"{plan.Settings.BeforeBoundaryTiming!.MatchMinutes} 分钟"),
                ("分界线前每日最多场次", plan.Settings.BeforeBoundaryTiming.MaxMatchesPerEntrantPerDay.ToString()),
                ("分界线后单场耗时", $"{plan.Settings.MatchMinutes} 分钟"),
                ("分界线后每日最多场次", plan.Settings.MaxMatchesPerEntrantPerDay.ToString())
            });
        }
        else
        {
            rows.InsertRange(1, new[]
            {
                ("单场比赛耗时", $"{plan.Settings.MatchMinutes} 分钟"),
                ("单名选手每日最多场次", plan.Settings.MaxMatchesPerEntrantPerDay.ToString())
            });
        }
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
