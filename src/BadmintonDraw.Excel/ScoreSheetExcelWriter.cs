using System.Reflection;
using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

public sealed partial class ScoreSheetExcelWriter
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

    private static XLWorkbook BuildIndividualWorkbook(IReadOnlyList<IndividualScoreSheetData> data)
    {
        using var templateWorkbook = LoadIndividualScoreSheetTemplate();
        var workbook = new XLWorkbook();
        try
        {
            for (var index = 0; index < data.Count; index++)
            {
                var sheet = templateWorkbook.Worksheet(1).CopyTo(workbook, $"计分表{index + 1}");
                FillIndividualScoreSheetTemplate(sheet, data[index]);
            }
            return workbook;
        }
        catch { workbook.Dispose(); throw; }
    }

    private static void WriteIndividualPdf(string outputPath, XLWorkbook workbook)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        var tempWorkbookPath = Path.Combine(Path.GetTempPath(), $"badminton-score-sheets-{Guid.NewGuid():N}.xlsx");
        try
        {
            workbook.SaveAs(tempWorkbookPath);
            new DrawResultVisualWriter().WriteSheetsA4Pdf(outputPath, tempWorkbookPath,
                workbook.Worksheets.Select(s => s.Name).ToArray(), stretchToPrintableArea: true,
                horizontalSafetyInset: 0f, leftPageInset: ScoreSheetPdfLeftInset,
                rightPageInset: ScoreSheetPdfRightInset);
        }
        finally
        {
            if (File.Exists(tempWorkbookPath)) File.Delete(tempWorkbookPath);
        }
    }

    private static XLWorkbook LoadIndividualScoreSheetTemplate()
    {
        var stream = typeof(ScoreSheetExcelWriter).GetTypeInfo().Assembly
            .GetManifestResourceStream(IndividualScoreSheetTemplateResourceName)
            ?? throw new InvalidOperationException($"缺少内置单场计分表模板：{IndividualScoreSheetTemplateResourceName}");
        return new XLWorkbook(stream);
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
        if (data.Caption is not null)
        {
            sheet.Cell("A1").Value = data.Caption;
            sheet.Cell("A1").Style.Font.FontSize = 9;
            sheet.Cell("A1").Style.Alignment.WrapText = true;
        }
        FillCompetitorRows(sheet, data.SideAPlayers, data.SideBPlayers);
    }

    private static void FillCompetitorRows(IXLWorksheet sheet, IReadOnlyList<string> sideA, IReadOnlyList<string> sideB)
    {
        foreach (var row in new[] { 39, 44, 49 })
        {
            sheet.Cell(row, 1).Value = PlayerLine(sideA, 0);
            sheet.Cell(row + 1, 1).Value = PlayerLine(sideA, 1);
            sheet.Cell(row + 2, 1).Value = PlayerLine(sideB, 0);
            sheet.Cell(row + 3, 1).Value = PlayerLine(sideB, 1);
        }
    }

    private static string PlayerLine(IReadOnlyList<string> players, int index) =>
        index < players.Count ? players[index] : "";

    private static void WriteTeamBlocks(IXLWorksheet sheet, IReadOnlyList<TeamScoreSheetData> data)
    {
        PrepareSheet(sheet, 7);
        if (data.Count == 0)
        {
            WriteEmptySheet(sheet, "团体赛记分表", "当前比赛日没有可导出的团体比赛。", 7);
            return;
        }
        for (var index = 0; index < data.Count; index++)
        {
            var startRow = index * TeamBlockRows + 1;
            WriteTeamBlock(sheet, startRow, data[index]);
            if (index < data.Count - 1) sheet.PageSetup.AddHorizontalPageBreak(startRow + TeamBlockRows - 1);
        }
        sheet.PageSetup.PrintAreas.Add($"A1:G{data.Count * TeamBlockRows}");
        sheet.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
        sheet.PageSetup.FitToPages(1, 0);
    }

    private static void WriteTeamBlock(IXLWorksheet sheet, int startRow, TeamScoreSheetData data)
    {
        sheet.Range(startRow, 1, startRow, 7).Merge().Value = data.Title;
        sheet.Range(startRow + 1, 1, startRow + 1, 7).Merge().Value = $"{data.SideA} 队  对  {data.SideB} 队";
        sheet.Range(startRow + 2, 1, startRow + 3, 7).Style.Fill.BackgroundColor = EditableFill;
        WriteRow(sheet, startRow + 3, 1, "阶段", "组别（位置号）", "日期", "时间", "场号");
        sheet.Cell(startRow + 4, 1).Value = data.Phase;
        sheet.Cell(startRow + 4, 2).Value = data.GroupName;
        sheet.Cell(startRow + 4, 3).Value = data.Date;
        sheet.Cell(startRow + 4, 4).Value = data.Time;
        sheet.Cell(startRow + 4, 5).Value = data.Court;
        sheet.Range(startRow + 6, 1, startRow + 6, 7).Merge().Value = "分场记录";
        WriteRow(sheet, startRow + 7, 1, "场序", "项目/单项", "A队出场", "B队出场", "比分", "胜方", "备注");
        for (var i = 0; i < 5; i++) sheet.Cell(startRow + 8 + i, 1).Value = $"第{i + 1}场";
        sheet.Cell(startRow + 14, 1).Value = "比赛结果";
        sheet.Range(startRow + 14, 2, startRow + 14, 3).Merge();
        sheet.Cell(startRow + 14, 4).Value = "获胜队";
        sheet.Range(startRow + 14, 5, startRow + 14, 6).Merge();
        sheet.Cell(startRow + 14, 7).Value = "裁判长签名";
        sheet.Cell(startRow + 16, 1).Value = "备注";
        sheet.Range(startRow + 16, 2, startRow + 16, 7).Merge().Value = data.Note;
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
        for (var column = 1; column <= lastColumn; column++) sheet.Column(column).Width = column == 1 ? 10 : 15;
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
        for (var index = 0; index < values.Length; index++) sheet.Cell(row, firstColumn + index).Value = values[index];
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

    private sealed record IndividualScoreSheetData(string ProjectName, string Stage, string DateLabel,
        string StartTimeLabel, string Court, int RecordNumber, IReadOnlyList<string> SideAPlayers,
        IReadOnlyList<string> SideBPlayers, string? Caption = null);

    private sealed record TeamScoreSheetData(string Phase, string GroupName, string Date, string Time, string Court,
        string SideA, string SideB, string Note, string Title = "（            ）团体赛记分表");
}
