using ClosedXML.Excel;

namespace BadmintonDraw.Excel;

public sealed partial class ScoreSheetExcelWriter
{
    public void WriteBlankIndividualExcel(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var workbook = BuildBlankIndividualWorkbook();
        SaveBlankWorkbook(path, workbook);
    }

    public void WriteBlankIndividualPdf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var workbook = BuildBlankIndividualWorkbook();
        WriteIndividualPdf(path, workbook);
    }

    public void WriteBlankTeamExcel(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var workbook = BuildBlankTeamWorkbook();
        SaveBlankWorkbook(path, workbook);
    }

    public void WriteBlankTeamPdf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var workbook = BuildBlankTeamWorkbook();
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"badminton-blank-team-{Guid.NewGuid():N}.xlsx");
        try
        {
            workbook.SaveAs(temporaryPath);
            new DrawResultVisualWriter().WriteSheetsA4Pdf(path, temporaryPath,
                [workbook.Worksheet(1).Name], portrait: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static XLWorkbook BuildBlankIndividualWorkbook()
    {
        using var template = LoadIndividualScoreSheetTemplate();
        var workbook = new XLWorkbook();
        var sheet = template.Worksheet(1).CopyTo(workbook, "单场计分表");
        sheet.ShowGridLines = false;
        sheet.Cell("AV26").Style.Alignment.WrapText = false;
        sheet.PageSetup.PrintAreas.Clear();
        sheet.PageSetup.PrintAreas.Add("A1:AX60");
        sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 1);
        return workbook;
    }

    private static XLWorkbook BuildBlankTeamWorkbook()
    {
        var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("团体计分表");
        WriteTeamBlocks(sheet, [new TeamScoreSheetData("", "", "", "", "", "", "", "")]);
        sheet.Range("A2:G2").Unmerge();
        sheet.Cell("A2").Value = "A队";
        sheet.Range("B2:C2").Merge();
        sheet.Cell("D2").Value = "B队";
        sheet.Range("E2:G2").Merge();
        sheet.Range("B2:C2").Style.Fill.BackgroundColor = EditableFill;
        sheet.Range("E2:G2").Style.Fill.BackgroundColor = EditableFill;
        sheet.PageSetup.FitToPages(1, 1);
        return workbook;
    }

    private static void SaveBlankWorkbook(string path, XLWorkbook workbook)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        workbook.SaveAs(path);
    }
}
