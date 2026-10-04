using System.Globalization;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using ClosedXML.Excel;
using static BadmintonDraw.Excel.WorkspaceRecordSchema;
using static BadmintonDraw.Excel.WorkspaceMaterialPresentation;

namespace BadmintonDraw.Excel;

/// <summary>v5 record sheet. Identities and dependency edges come only from the qualified match graph.</summary>
public sealed class WorkspaceMatchRecordWriter
{
    public void Write(string outputPath, WorkspaceScheduleExportContext context, IReadOnlyList<WorkspaceRecordExportRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var selected = SelectRows(context, rows, "记录表");
        var rowNumbers = selected.Select((row, i) => (row.Key, Row: FirstDataRow + i)).ToDictionary(p => p.Key, p => p.Row);
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(SheetName);
        WriteHeading(sheet, selected.Select(r => r.RecordDay).Distinct().Order().ToArray());
        for (var i = 0; i < selected.Length; i++) WriteRow(sheet, context, selected[i], FirstDataRow + i, i + 1, rowNumbers);
        ApplyLayout(sheet, FirstDataRow + selected.Length - 1);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        WorkbookPrintTitles.SaveAs(workbook, outputPath);
    }

    private static void WriteHeading(IXLWorksheet sheet, IReadOnlyList<DateOnly> recordDays)
    {
        // Merge only the contiguous visible region; hidden provenance columns must not clip the heading.
        var title = recordDays.Count == 1
            ? recordDays[0].ToString("M月d日", CultureInfo.InvariantCulture) + "赛程记录表"
            : "对阵记录表";
        sheet.Range(1, 1, 1, Note).Merge().Value = title;
        sheet.Range(2, 1, 2, Note).Merge().Value =
            "第5行为填写示例；比分、用时首次导出时留空。胜方可点击下拉选择，后续占空对阵会随前序胜负自动更新。";
        // Keep v5's required editable facts available, outside the unchanged v4.6 printed form.
        sheet.Range(1, ResultKind, 3, ActualPlayedDay).Merge().Value =
            "导入赛果前，请填写实际比赛日期。弃权请选择“弃权”，用时填 0。";
        string[] headers = ["序号", "日期", "时间", "进度", "组别", "对阵数据", "", "", "比分", "用时", "场地", "胜方", "备注",
            "MatchId", "A 选项", "B 选项", "WorkspaceId", "ProjectId", "GraphRevision", "DrawConfirmedAt", "结果类型", "实际比赛日期"];
        for (var i = 0; i < headers.Length; i++) sheet.Cell(HeaderRow, i + 1).Value = headers[i];
        sheet.Range(HeaderRow, SideA, HeaderRow, SideB).Merge();
        sheet.Cell(ExampleRow, Order).Value = "示例";
        sheet.Cell(ExampleRow, RecordDay).Value = recordDays[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sheet.Cell(ExampleRow, Time).Value = "14:00-14:20"; sheet.Cell(ExampleRow, Phase).Value = "首轮赛";
        sheet.Cell(ExampleRow, Group).Value = "A组";
        sheet.Cell(ExampleRow, SideA).Value = "A【张三\n李四】"; sheet.Cell(ExampleRow, SideB).Value = "B【王五\n赵六】";
        sheet.Cell(ExampleRow, Versus).Value = "vs"; sheet.Cell(ExampleRow, Score).Value = "15-10, 15-12";
        sheet.Cell(ExampleRow, Duration).Value = "18m"; sheet.Cell(ExampleRow, Court).Value = "B1";
        sheet.Cell(ExampleRow, Winner).Value = "A【张三 李四】";
        sheet.Cell(ExampleRow, OptionA).Value = "A【张三 李四】";
        sheet.Cell(ExampleRow, OptionB).Value = "B【王五 赵六】";
        sheet.Cell(ExampleRow, Winner).CreateDataValidation().List(sheet.Range(ExampleRow, OptionA, ExampleRow, OptionB), true);
        sheet.Cell(ExampleRow, ResultKind).Value = "正常";
        sheet.Cell(ExampleRow, ActualPlayedDay).Value = recordDays[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sheet.Cell(ExampleRow, Note).Value = "胜者进入下一轮";
    }

    private static void WriteRow(IXLWorksheet sheet, WorkspaceScheduleExportContext context, WorkspaceRecordExportRow selection,
        int row, int order, IReadOnlyDictionary<WorkspaceMatchKey, int> rowNumbers)
    {
        var node = context.Nodes[selection.Key]; var project = context.Projects[selection.Key.ProjectId];
        var placement = context.Placements[selection.Key];
        context.Workspace.Results.TryGetValue(selection.Key, out var result);
        var recordDay = selection.RecordDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var differentDay = recordDay != placement.DayLabel;
        var time = TimeText(placement.StartTime) + "-" + TimeText(placement.EndTime);
        sheet.Cell(row, Order).Value = order; sheet.Cell(row, RecordDay).Value = recordDay;
        sheet.Cell(row, Time).Value = differentDay ? result is null ? "待安排" : $"原计划 {placement.DayLabel}\n{time}" : time;
        sheet.Cell(row, Phase).Value = node.Phase;
        sheet.Cell(row, Phase).Style.Fill.BackgroundColor = PhaseFill(node.Phase);
        sheet.Cell(row, Group).Value = project.DisplayName + (string.IsNullOrWhiteSpace(node.GroupName) ? "" : "\n" + node.GroupName);
        sheet.Cell(row, Versus).Value = "vs";
        sheet.Cell(row, Court).Value = differentDay && result is null ? "待安排" : placement.Court;
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(node.Note)) notes.Add(node.Note);
        if (differentDay) notes.Add($"原计划 {placement.DayLabel} {time} {placement.Court}；记录日期不改变赛程。");
        WriteSide(sheet, context, selection.Key.ProjectId, node.SideA, ScheduleMatchSide.SideA, row, rowNumbers, notes);
        WriteSide(sheet, context, selection.Key.ProjectId, node.SideB, ScheduleMatchSide.SideB, row, rowNumbers, notes);
        sheet.Cell(row, Note).Value = string.Join("\n", notes.Distinct());
        // Never use formulas for provenance, even when the same value repeats on every row.
        sheet.Cell(row, MatchId).Value = node.Id.ToString("D");
        sheet.Cell(row, WorkspaceId).Value = context.Workspace.Id.ToString("D");
        sheet.Cell(row, ProjectId).Value = node.ProjectId.ToString("D");
        sheet.Cell(row, GraphRevision).Value = project.MatchGraph!.Revision;
        sheet.Cell(row, DrawConfirmedAt).Value = project.Draw!.ConfirmedAt!.Value.ToString("O", CultureInfo.InvariantCulture);
        if (result is not null)
        {
            sheet.Cell(row, Score).Value = result.Score;
            sheet.Cell(row, Duration).Value = result.DurationMinutes;
            sheet.Cell(row, ResultKind).Value = result.Kind == TournamentResultKind.Walkover ? "弃权" : "正常";
            if (result.ActualPlayedDay is { } actual) sheet.Cell(row, ActualPlayedDay).Value = actual.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var a = context.ResolveParticipant(node.ProjectId, node.SideA)!;
            var side = result.Winner.IdentityKey == a.IdentityKey ? ScheduleMatchSide.SideA : ScheduleMatchSide.SideB;
            sheet.Cell(row, Winner).Value = WorkspaceWinnerOptionText.Format(side, result.Winner.DisplayName);
        }
        var winnerValidation = sheet.Cell(row, Winner).CreateDataValidation();
        winnerValidation.List(sheet.Range(row, OptionA, row, OptionB), true);
        winnerValidation.InputTitle = "选择胜者"; winnerValidation.InputMessage = "使用下拉完整选项，或手动填写 A/B；导入时按比赛图核验。";
        // Keep a dropdown without rejecting the explicitly supported bare A/B input.
        winnerValidation.ShowErrorMessage = false;
        sheet.Cell(row, ResultKind).CreateDataValidation().List("\"正常,弃权\"", true);
    }

    private static void WriteSide(IXLWorksheet sheet, WorkspaceScheduleExportContext context, Guid projectId,
        EntrantSource source, ScheduleMatchSide side, int row, IReadOnlyDictionary<WorkspaceMatchKey, int> rowNumbers, List<string> notes)
    {
        var optionColumn = side == ScheduleMatchSide.SideA ? OptionA : OptionB;
        var displayColumn = side == ScheduleMatchSide.SideA ? SideA : SideB;
        if (context.ResolveParticipant(projectId, source) is { } participant)
        {
            sheet.Cell(row, optionColumn).Value = WorkspaceWinnerOptionText.Format(side, participant.DisplayName);
            // Only actual typed participant player lines, never splitting a display label or printing candidate unions.
            var display = participant.Players.Count > 1 ? string.Join("\n", participant.Players.Select(p => p.Name)) : participant.DisplayName;
            sheet.Cell(row, displayColumn).Value = WorkspaceWinnerOptionText.Format(side, display);
            return;
        }
        var sourceId = source switch { EntrantSource.WinnerOf w => w.MatchId, EntrantSource.LoserOf l => l.MatchId,
            _ => throw new WorkspaceValidationException("export.source", "记录表包含不可比赛的来源。") };
        var sourceKey = new WorkspaceMatchKey(projectId, sourceId);
        var placeholder = WorkspaceWinnerOptionText.Format(side, context.Nodes[sourceKey].DisplayName + (source is EntrantSource.WinnerOf ? "胜者" : "负者"));
        if (rowNumbers.TryGetValue(sourceKey, out var sourceRow))
        {
            sheet.Cell(row, optionColumn).FormulaA1 = OutcomeFormula(sourceRow, source is EntrantSource.WinnerOf, side, placeholder);
            // Propagate the source's typed player lines without treating spaces inside a name as separators.
            sheet.Cell(row, displayColumn).FormulaA1 = OutcomeFormula(sourceRow, source is EntrantSource.WinnerOf, side, placeholder, display: true);
        }
        else
        {
            sheet.Cell(row, optionColumn).Value = placeholder; sheet.Cell(row, displayColumn).Value = placeholder;
            notes.Add("前置比赛未在本表且尚无赛果；请导入前置赛果后重新导出。");
        }
    }

    private static string OutcomeFormula(int sourceRow, bool winner, ScheduleMatchSide target, string placeholder, bool display = false)
    {
        var selected = Address(sourceRow, Winner); var a = Address(sourceRow, OptionA); var b = Address(sourceRow, OptionB);
        var isA = $"OR(UPPER(TRIM({selected}))=\"A\",{selected}={a})";
        var isB = $"OR(UPPER(TRIM({selected}))=\"B\",{selected}={b})";
        var valueA = display ? Address(sourceRow, SideA) : a;
        var valueB = display ? Address(sourceRow, SideB) : b;
        var outcome = $"IF({isA},{(winner ? valueA : valueB)},IF({isB},{(winner ? valueB : valueA)},\"\"))";
        var prefix = target == ScheduleMatchSide.SideA ? "A【" : "B【";
        return $"IF({outcome}=\"\",{Literal(placeholder)},{Literal(prefix)}&MID({outcome},3,LEN({outcome})-3)&\"】\")";
    }

    private static string Literal(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
    private static void ApplyLayout(IXLWorksheet sheet, int lastRow)
    {
        var body = sheet.Range(HeaderRow, 1, lastRow, LastColumn);
        body.Style.Font.FontName = "Microsoft YaHei";
        body.Style.Font.FontSize = 10; body.Style.Alignment.WrapText = true;
        body.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        body.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        body.Style.Border.InsideBorder = XLBorderStyleValues.Thin; body.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        body.Style.Border.InsideBorderColor = XLColor.FromHtml("#808080");
        body.Style.Border.OutsideBorderColor = XLColor.FromHtml("#808080");
        sheet.Range(1, 1, 2, Note).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sheet.Range(1, 1, 2, Note).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Range(1, 1, 1, Note).Style.Font.SetBold().Font.FontSize = 16;
        sheet.Range(1, 1, 1, Note).Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
        sheet.Range(1, 1, 1, Note).Style.Font.FontColor = XLColor.White;
        sheet.Range(1, ResultKind, 3, ActualPlayedDay).Style.Alignment.WrapText = true;
        sheet.Range(1, ResultKind, 3, ActualPlayedDay).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Range(1, ResultKind, 3, ActualPlayedDay).Style.Font.FontSize = 10;
        sheet.Range(HeaderRow, 1, HeaderRow, LastColumn).Style.Fill.BackgroundColor = XLColor.FromHtml("#305496");
        sheet.Range(HeaderRow, 1, HeaderRow, LastColumn).Style.Font.SetBold().Font.FontColor = XLColor.White;
        for (var row = FirstDataRow; row <= lastRow; row++)
        {
            var phaseFill = sheet.Cell(row, Phase).Style.Fill.BackgroundColor;
            sheet.Range(row, 1, row, LastColumn).Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
            sheet.Cell(row, Phase).Style.Fill.BackgroundColor = phaseFill;
        }
        var example = sheet.Range(ExampleRow, 1, ExampleRow, LastColumn);
        example.Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F2F2");
        example.Style.Font.Italic = true; example.Style.Font.FontColor = XLColor.FromHtml("#5B677A");
        sheet.Range(ExampleRow, Score, lastRow, Winner).Style.Fill.BackgroundColor = XLColor.White;
        sheet.Range(ExampleRow, ResultKind, lastRow, ActualPlayedDay).Style.Fill.BackgroundColor = XLColor.White;
        sheet.Range(ExampleRow, SideA, lastRow, SideB).Style.Font.Bold = true;
        sheet.Range(ExampleRow, Versus, lastRow, Versus).Style.Font.FontSize = 12;
        sheet.Rows(ExampleRow, lastRow).Height = 42;
        sheet.Row(1).Height = 30; sheet.Row(2).Height = 24;
        double[] widths = [7, 12, 15, 12, 10, 26, 6, 26, 18, 10, 10, 24, 24];
        for (var i = 0; i < widths.Length; i++) sheet.Column(i + 1).Width = widths[i];
        sheet.Column(ResultKind).Width = 10; sheet.Column(ActualPlayedDay).Width = 15;
        sheet.Columns(MatchId, DrawConfirmedAt).Hide();
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
        sheet.PageSetup.PagesWide = 1; sheet.PageSetup.PagesTall = 0;
        sheet.PageSetup.SetRowsToRepeatAtTop(1, HeaderRow);
        sheet.PageSetup.PrintAreas.Add(1, 1, lastRow, Note);
        sheet.SheetView.FreezeRows(HeaderRow);
        sheet.ShowGridLines = false;
    }
}
