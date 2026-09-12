using System.Globalization;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using ClosedXML.Excel;
using static BadmintonDraw.Excel.WorkspaceRecordSchema;

namespace BadmintonDraw.Excel;

/// <summary>v5 record sheet. Identities and dependency edges come only from the qualified match graph.</summary>
public sealed class WorkspaceMatchRecordWriter
{
    public void Write(string outputPath, WorkspaceScheduleExportContext context, IReadOnlyList<WorkspaceRecordExportRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        var selected = rows.ToArray();
        if (selected.Length == 0) throw new WorkspaceValidationException("export.no-matches", "所选范围没有比赛，未生成记录表。");
        if (selected.Any(r => r is null) || selected.Select(r => r.Key).Distinct().Count() != selected.Length)
            throw new WorkspaceValidationException("export.selection", "记录表不能重复选择同一场比赛。");
        var knownDays = context.Workspace.Schedule!.Resources.Days.Select(d => d.Date).ToHashSet();
        if (selected.Any(r => !context.Nodes.ContainsKey(r.Key) || !knownDays.Contains(r.RecordDay)))
            throw new WorkspaceValidationException("export.selection", "记录表选择包含未知比赛或记录日期。");
        var rowNumbers = selected.Select((row, i) => (row.Key, Row: FirstDataRow + i)).ToDictionary(p => p.Key, p => p.Row);
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(SheetName);
        WriteHeading(sheet, context.Workspace.Name);
        for (var i = 0; i < selected.Length; i++) WriteRow(sheet, context, selected[i], FirstDataRow + i, i + 1, rowNumbers);
        ApplyLayout(sheet, FirstDataRow + selected.Length - 1);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        workbook.SaveAs(outputPath);
    }

    private static void WriteHeading(IXLWorksheet sheet, string name)
    {
        // Merge only the contiguous visible region; hidden provenance columns must not clip the heading.
        sheet.Range(1, 1, 1, Note).Merge().Value = name + " · 对阵记录表";
        sheet.Range(2, 1, 2, Note).Merge().Value =
            "比分按 A–B 填写。单项：一局 x-y，或已决胜的两/三局（如 21-15, 18-21, 21-19）；团体：总比分 x-y。均为非负整数，不限 21 分。";
        sheet.Range(3, 1, 3, Note).Merge().Value =
            "胜者用下拉或 A/B；正常比赛时长填正整数（可加 m/分钟）；弃权必须选“弃权”、时长 0，可写备注。实际日期填 yyyy-MM-dd；记录日期仅供汇总，不代表已调整赛程。";
        string[] headers = ["序号", "记录日期", "计划时间", "赛程阶段", "项目 / 组别", "A 方", "VS", "B 方", "比分（A-B）", "时长（分钟）", "场地", "胜者（A/B）", "备注",
            "MatchId", "A 选项", "B 选项", "WorkspaceId", "ProjectId", "GraphRevision", "DrawConfirmedAt", "结果类型", "实际比赛日期"];
        for (var i = 0; i < headers.Length; i++) sheet.Cell(HeaderRow, i + 1).Value = headers[i];
        sheet.Cell(ExampleRow, Order).Value = "示例";
        sheet.Cell(ExampleRow, SideA).Value = "A【示例选手】"; sheet.Cell(ExampleRow, SideB).Value = "B【示例选手】";
        sheet.Cell(ExampleRow, Versus).Value = "VS"; sheet.Cell(ExampleRow, Score).Value = "21-15";
        sheet.Cell(ExampleRow, Duration).Value = 18; sheet.Cell(ExampleRow, Winner).Value = "A";
        sheet.Cell(ExampleRow, ResultKind).Value = "正常"; sheet.Cell(ExampleRow, ActualPlayedDay).Value = "yyyy-MM-dd";
        sheet.Cell(ExampleRow, Note).Value = "此行仅示例，不参与导入。下方填写实际赛果和日期；未赛可留空。";
    }

    private static void WriteRow(IXLWorksheet sheet, WorkspaceScheduleExportContext context, WorkspaceRecordExportRow selection,
        int row, int order, IReadOnlyDictionary<WorkspaceMatchKey, int> rowNumbers)
    {
        var node = context.Nodes[selection.Key]; var project = context.Projects[selection.Key.ProjectId];
        var placement = context.Placements[selection.Key];
        context.Workspace.Results.TryGetValue(selection.Key, out var result);
        var recordDay = selection.RecordDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var differentDay = recordDay != placement.DayLabel;
        var time = TimeText(placement.StartTime) + "–" + TimeText(placement.EndTime);
        sheet.Cell(row, Order).Value = order; sheet.Cell(row, RecordDay).Value = recordDay;
        sheet.Cell(row, Time).Value = differentDay ? result is null ? "待安排" : $"原计划 {placement.DayLabel}\n{time}" : time;
        sheet.Cell(row, Phase).Value = node.Phase + "\n" + node.DisplayName;
        sheet.Cell(row, Group).Value = project.DisplayName + (string.IsNullOrWhiteSpace(node.GroupName) ? "" : "\n" + node.GroupName);
        sheet.Cell(row, Versus).Value = "VS";
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
            sheet.Cell(row, displayColumn).FormulaA1 = Address(row, optionColumn);
        }
        else
        {
            sheet.Cell(row, optionColumn).Value = placeholder; sheet.Cell(row, displayColumn).Value = placeholder;
            notes.Add("前置比赛未在本表且尚无赛果；请导入前置赛果后重新导出。");
        }
    }

    private static string OutcomeFormula(int sourceRow, bool winner, ScheduleMatchSide target, string placeholder)
    {
        var selected = Address(sourceRow, Winner); var a = Address(sourceRow, OptionA); var b = Address(sourceRow, OptionB);
        var isA = $"OR(UPPER(TRIM({selected}))=\"A\",{selected}={a})";
        var isB = $"OR(UPPER(TRIM({selected}))=\"B\",{selected}={b})";
        var outcome = $"IF({isA},{(winner ? a : b)},IF({isB},{(winner ? b : a)},\"\"))";
        var prefix = target == ScheduleMatchSide.SideA ? "A【" : "B【";
        return $"IF({outcome}=\"\",{Literal(placeholder)},{Literal(prefix)}&MID({outcome},3,LEN({outcome})-3)&\"】\")";
    }

    private static string Literal(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
    private static string TimeText(TimeOnly time) => time.Ticks % TimeSpan.TicksPerMinute == 0
        ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
        : time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');

    private static void ApplyLayout(IXLWorksheet sheet, int lastRow)
    {
        sheet.Range(1, 1, lastRow, LastColumn).Style.Font.FontName = "Microsoft YaHei";
        var body = sheet.Range(HeaderRow, 1, lastRow, LastColumn);
        body.Style.Font.FontSize = 10; body.Style.Alignment.WrapText = true;
        body.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        body.Style.Border.InsideBorder = XLBorderStyleValues.Thin; body.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        sheet.Range(1, 1, 1, Note).Style.Font.SetBold().Font.FontSize = 18;
        sheet.Range(1, 1, 1, Note).Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
        sheet.Range(1, 1, 1, Note).Style.Font.FontColor = XLColor.White;
        sheet.Range(2, 1, 3, Note).Style.Alignment.WrapText = true;
        sheet.Range(2, 1, 3, Note).Style.Font.FontSize = 10;
        sheet.Range(HeaderRow, 1, HeaderRow, LastColumn).Style.Fill.BackgroundColor = XLColor.FromHtml("#305496");
        sheet.Range(HeaderRow, 1, HeaderRow, LastColumn).Style.Font.SetBold().Font.FontColor = XLColor.White;
        sheet.Range(ExampleRow, 1, ExampleRow, LastColumn).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");
        sheet.Rows(FirstDataRow, lastRow).Height = 54;
        sheet.Row(1).Height = 30; sheet.Rows(2, 3).Height = 30; sheet.Row(HeaderRow).Height = 32; sheet.Row(ExampleRow).Height = 46;
        double[] widths = [6, 13, 21, 16, 16, 25, 4, 25, 19, 10, 10, 25, 33];
        for (var i = 0; i < widths.Length; i++) sheet.Column(i + 1).Width = widths[i];
        sheet.Column(ResultKind).Width = 10; sheet.Column(ActualPlayedDay).Width = 15;
        sheet.Columns(MatchId, DrawConfirmedAt).Hide();
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
        sheet.PageSetup.PagesWide = 1; sheet.PageSetup.PagesTall = 0;
        sheet.PageSetup.SetRowsToRepeatAtTop(HeaderRow, HeaderRow);
        sheet.PageSetup.PrintAreas.Add(1, 1, lastRow, LastColumn);
        sheet.SheetView.FreezeRows(HeaderRow);
    }
}
