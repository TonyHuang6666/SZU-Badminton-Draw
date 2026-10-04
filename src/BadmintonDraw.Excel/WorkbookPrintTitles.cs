using System.Globalization;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BadmintonDraw.Excel;

internal static class WorkbookPrintTitles
{
    internal static void SaveAs(XLWorkbook workbook, string outputPath)
    {
        // ClosedXML 0.104.2 writes relative whole-row/column Print_Titles references.
        // Calc reads those as ordinary defined names and silently drops repeating headers.
        // Normalize only that built-in name, before touching the destination file.
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            var names = document.WorkbookPart!.Workbook.DefinedNames;
            foreach (var name in names?.Elements<DefinedName>() ?? [])
            {
                if (name.Name?.Value != "_xlnm.Print_Titles" || name.LocalSheetId is null) continue;
                var sheet = workbook.Worksheet(checked((int)name.LocalSheetId.Value + 1));
                var prefix = "'" + sheet.Name.Replace("'", "''", StringComparison.Ordinal) + "'!";
                var ranges = new List<string>();
                var setup = sheet.PageSetup;
                if (setup.FirstColumnToRepeatAtLeft > 0)
                    ranges.Add(prefix + "$" + XLHelper.GetColumnLetterFromNumber(setup.FirstColumnToRepeatAtLeft) +
                        ":$" + XLHelper.GetColumnLetterFromNumber(setup.LastColumnToRepeatAtLeft));
                if (setup.FirstRowToRepeatAtTop > 0)
                    ranges.Add(prefix + "$" + setup.FirstRowToRepeatAtTop.ToString(CultureInfo.InvariantCulture) +
                        ":$" + setup.LastRowToRepeatAtTop.ToString(CultureInfo.InvariantCulture));
                if (ranges.Count > 0) name.Text = string.Join(",", ranges);
            }
        }
        stream.Position = 0;
        using var output = File.Create(outputPath);
        stream.CopyTo(output);
    }
}
