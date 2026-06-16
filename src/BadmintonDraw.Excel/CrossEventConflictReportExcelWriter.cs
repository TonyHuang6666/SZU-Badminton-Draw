using BadmintonDraw.Core;
using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

public sealed class CrossEventConflictReportExcelWriter
{
    public void Write(string outputPath, CrossEventConflictReport report)
    {
        WriteReport(outputPath, report, board: null);
    }

    public void WriteScheduleAudit(string outputPath, CrossEventScheduleBoard board)
    {
        WriteReport(outputPath, board.Report, board);
    }

    private static void WriteReport(
        string outputPath,
        CrossEventConflictReport report,
        CrossEventScheduleBoard? board)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var workbook = new XLWorkbook();
        WriteSummarySheet(workbook, report, board);
        if (board is not null)
        {
            WriteBoardItemsSheet(workbook, board);
        }

        WriteIssuesSheet(workbook, "严重冲突", report.Issues.Where(issue => issue.Severity == CrossEventConflictSeverity.Severe));
        WriteIssuesSheet(workbook, "间隔过短", report.Issues.Where(issue => issue.Severity == CrossEventConflictSeverity.Warning));
        WriteIssuesSheet(workbook, "同日提醒", report.Issues.Where(issue => issue.Severity == CrossEventConflictSeverity.Notice));
        WriteIssuesSheet(workbook, "全部检查明细", report.Issues);
        WriteSourcesSheet(workbook, report);
        workbook.SaveAs(outputPath);
    }

    private static void WriteSummarySheet(
        XLWorkbook workbook,
        CrossEventConflictReport report,
        CrossEventScheduleBoard? board)
    {
        var sheet = workbook.Worksheets.Add("检查总览");
        sheet.Cell(1, 1).Value = "多项目排程检查报告";
        sheet.Range(1, 1, 1, 4).Merge();
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 18;
        sheet.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#2B145F");

        var totalMatches = board?.Items.Count ?? report.Sources.Sum(source => source.MatchCount);
        var completedMatches = board?.Items.Count(item => item.IsCompleted) ?? 0;
        var pendingMatches = board?.Items.Count(item => !item.IsCompleted) ?? Math.Max(0, totalMatches - completedMatches);
        var dayCount = board?.Days.Count ?? 0;
        var courtCount = board?.Days.SelectMany(day => day.Courts).Distinct(StringComparer.Ordinal).Count() ?? 0;
        var blockingCards = board?.BlockingConflictItemCount ?? 0;
        var multiEventPlayers = board?.MultiEventPlayerCount ?? 0;
        var exportReadiness = report.SevereCount == 0
            ? "严重冲突为 0，可作为合并材料包导出前的留档依据。"
            : "仍有严重冲突，建议先在多项目工作台调整后再导出合并材料包。";

        var rows = new List<(string Label, string Value)>
        {
            ("生成时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
            ("报告定位", "赛前确认、现场交接和赛后复盘留档；不替代裁判长现场判断。"),
            ("最小休息间隔", $"{report.MinimumRestMinutes} 分钟"),
            ("导入赛事数", report.Sources.Count.ToString()),
            ("总场次数", totalMatches.ToString()),
            ("已完成场次", completedMatches.ToString()),
            ("未完成场次", pendingMatches.ToString()),
            ("比赛日数", dayCount == 0 ? "未加载工作台" : dayCount.ToString()),
            ("场地数", courtCount == 0 ? "未加载工作台" : courtCount.ToString()),
            ("兼项选手", board is null ? "未加载工作台" : multiEventPlayers.ToString()),
            ("冲突卡片", board is null ? "未加载工作台" : blockingCards.ToString()),
            ("严重冲突", report.SevereCount.ToString()),
            ("间隔过短", report.WarningCount.ToString()),
            ("同日提醒", report.NoticeCount.ToString()),
            ("导出判断", exportReadiness),
            ("调整状态", board is null
                ? "由赛事存档直接检查。"
                : board.HasUnsavedChanges
                    ? "当前工作台有未保存调整，建议确认后保存到各项目赛事存档。"
                    : "当前工作台无未保存调整。")
        };

        for (var index = 0; index < rows.Count; index++)
        {
            var row = index + 3;
            sheet.Cell(row, 1).Value = rows[index].Label;
            sheet.Cell(row, 2).Value = rows[index].Value;
            sheet.Cell(row, 1).Style.Font.Bold = true;
        }

        var noteRow = rows.Count + 5;
        sheet.Cell(noteRow, 1).Value = report.HasIssues
            ? "说明：严重冲突表示同一选手同时间段跨项目参赛；间隔过短表示两场之间少于设定休息时间；同日提醒用于裁判长人工确认体能和现场调度。"
            : "未发现跨项目选手冲突。";
        sheet.Range(noteRow, 1, noteRow, 4).Merge();
        sheet.Cell(noteRow, 1).Style.Alignment.WrapText = true;

        sheet.Columns().AdjustToContents();
        sheet.Column(1).Width = Math.Max(sheet.Column(1).Width, 18);
        sheet.Column(2).Width = Math.Max(sheet.Column(2).Width, 24);
    }

    private static void WriteBoardItemsSheet(XLWorkbook workbook, CrossEventScheduleBoard board)
    {
        var sheet = workbook.Worksheets.Add("当前赛程卡片");
        WriteHeaders(sheet, [
            "状态",
            "冲突级别",
            "日期",
            "时间",
            "场地",
            "项目",
            "项目类型",
            "组别",
            "阶段/场次",
            "选手/队伍A",
            "选手/队伍B",
            "冲突说明",
            "来源文件"
        ]);

        if (board.Items.Count == 0)
        {
            sheet.Cell(2, 1).Value = "无";
            ApplyTableStyle(sheet, 2, 13);
            return;
        }

        var row = 2;
        foreach (var item in board.Items
                     .OrderBy(item => item.DayLabel, StringComparer.Ordinal)
                     .ThenBy(item => item.StartTime)
                     .ThenBy(item => item.Court, StringComparer.Ordinal)
                     .ThenBy(item => item.EventName, StringComparer.Ordinal)
                     .ThenBy(item => item.Order))
        {
            sheet.Cell(row, 1).Value = item.Status;
            sheet.Cell(row, 2).Value = item.ConflictSeverity is null ? "" : FormatSeverity(item.ConflictSeverity.Value);
            sheet.Cell(row, 3).Value = item.DayLabel;
            sheet.Cell(row, 4).Value = item.TimeRange;
            sheet.Cell(row, 5).Value = item.Court;
            sheet.Cell(row, 6).Value = item.EventName;
            sheet.Cell(row, 7).Value = FormatEventKind(item.EventKind);
            sheet.Cell(row, 8).Value = item.GroupName;
            sheet.Cell(row, 9).Value = $"{item.Phase} / {item.MatchName}";
            sheet.Cell(row, 10).Value = item.SideA;
            sheet.Cell(row, 11).Value = item.SideB;
            sheet.Cell(row, 12).Value = item.ConflictSummary;
            sheet.Cell(row, 13).Value = item.SourcePath;
            row++;
        }

        ApplyTableStyle(sheet, row - 1, 13);
        sheet.SheetView.FreezeRows(1);
    }

    private static void WriteSourcesSheet(XLWorkbook workbook, CrossEventConflictReport report)
    {
        var sheet = workbook.Worksheets.Add("输入赛事");
        WriteHeaders(sheet, [
            "项目",
            "项目类型",
            "场次数",
            "已知选手出现次数",
            "未决占位边数",
            "来源文件"
        ]);

        var row = 2;
        foreach (var source in report.Sources.OrderBy(source => source.EventName, StringComparer.Ordinal))
        {
            sheet.Cell(row, 1).Value = source.EventName;
            sheet.Cell(row, 2).Value = FormatEventKind(source.EventKind);
            sheet.Cell(row, 3).Value = source.MatchCount;
            sheet.Cell(row, 4).Value = source.KnownPlayerAppearanceCount;
            sheet.Cell(row, 5).Value = source.UnresolvedSideCount;
            sheet.Cell(row, 6).Value = source.SourcePath;
            row++;
        }

        ApplyTableStyle(sheet, row - 1, 6);
    }

    private static void WriteIssuesSheet(
        XLWorkbook workbook,
        string sheetName,
        IEnumerable<CrossEventConflictIssue> issues)
    {
        var issueList = issues.ToList();
        var sheet = workbook.Worksheets.Add(sheetName);
        WriteHeaders(sheet, [
            "级别",
            "选手",
            "日期",
            "间隔(分钟)",
            "说明",
            "项目1",
            "时间1",
            "场地1",
            "阶段/场次1",
            "本方1",
            "对手1",
            "来源1",
            "项目2",
            "时间2",
            "场地2",
            "阶段/场次2",
            "本方2",
            "对手2",
            "来源2"
        ]);

        if (issueList.Count == 0)
        {
            sheet.Cell(2, 1).Value = "无";
            ApplyTableStyle(sheet, 2, 19);
            return;
        }

        var row = 2;
        foreach (var issue in issueList)
        {
            sheet.Cell(row, 1).Value = FormatSeverity(issue.Severity);
            sheet.Cell(row, 2).Value = issue.PlayerName;
            sheet.Cell(row, 3).Value = issue.DayLabel;
            sheet.Cell(row, 4).Value = issue.RestMinutes.HasValue ? issue.RestMinutes.Value.ToString() : "重叠";
            sheet.Cell(row, 5).Value = issue.Detail;
            WriteMatchCells(sheet, row, 6, issue.FirstMatch);
            WriteMatchCells(sheet, row, 13, issue.SecondMatch);
            row++;
        }

        ApplyTableStyle(sheet, row - 1, 19);
        sheet.SheetView.FreezeRows(1);
    }

    private static void WriteHeaders(IXLWorksheet sheet, IReadOnlyList<string> headers)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            sheet.Cell(1, index + 1).Value = headers[index];
        }

        var headerRange = sheet.Range(1, 1, 1, headers.Count);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#EDE1FC");
        headerRange.Style.Font.FontColor = XLColor.FromHtml("#32116D");
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static void WriteMatchCells(
        IXLWorksheet sheet,
        int row,
        int startColumn,
        CrossEventPlayerAppearance match)
    {
        sheet.Cell(row, startColumn).Value = match.EventName;
        sheet.Cell(row, startColumn + 1).Value = match.TimeRange;
        sheet.Cell(row, startColumn + 2).Value = match.Court;
        sheet.Cell(row, startColumn + 3).Value = $"{match.Phase} / {match.MatchName}";
        sheet.Cell(row, startColumn + 4).Value = match.SideText;
        sheet.Cell(row, startColumn + 5).Value = match.OpponentText;
        sheet.Cell(row, startColumn + 6).Value = match.SourcePath;
    }

    private static void ApplyTableStyle(IXLWorksheet sheet, int lastRow, int lastColumn)
    {
        if (lastRow < 1)
        {
            lastRow = 1;
        }

        var range = sheet.Range(1, 1, lastRow, lastColumn);
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = XLColor.FromHtml("#D8C7F4");
        range.Style.Border.InsideBorderColor = XLColor.FromHtml("#E8DDF7");
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
        sheet.Columns().AdjustToContents(8, 42);
    }

    private static string FormatSeverity(CrossEventConflictSeverity severity)
    {
        return severity switch
        {
            CrossEventConflictSeverity.Severe => "严重冲突",
            CrossEventConflictSeverity.Warning => "间隔过短",
            _ => "同日提醒"
        };
    }

    private static string FormatEventKind(EventKind eventKind)
    {
        return eventKind switch
        {
            EventKind.Singles => "单打",
            EventKind.Team => "团体",
            _ => "双打"
        };
    }
}
