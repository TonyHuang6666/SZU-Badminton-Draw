using System.Security.Cryptography;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceMatchRecordReaderTests
{
    [Fact]
    public void CapturedBytesKeepRepeatedPendingAndIdlessRowsButIgnoreExampleAndStyleOnlyRows()
    {
        var bytes = Workbook(sheet =>
        {
            sheet.Cell("N5").Value = "example";
            sheet.Cell("N6").Value = "same-match"; sheet.Cell("L6").Value = " A ";
            sheet.Cell("N7").Value = "same-match";
            sheet.Cell("A9").Value = "有内容但缺少隐藏标识";
            sheet.Cell("V10").Value = "2026-09-13";
            sheet.Cell("W11").Value = "schema外备注";
            sheet.Cell("A50000").Style.Font.Bold = true;
        });
        var before = bytes.ToArray();

        var document = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes);

        Assert.Equal(new[] { 6, 7, 9, 10 }, document.Rows.Select(row => row.Location.RowNumber));
        Assert.Equal(new[] { "same-match", "same-match", "", "" }, document.Rows.Select(row => row.MatchId.Text));
        Assert.Equal(" A ", document.Rows[0].Winner.Text);
        Assert.Equal(WorkspaceRecordCellKind.Empty, document.Rows[1].Winner.Kind);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(before)), document.ContentHash);
        Assert.Equal(before, bytes);
    }

    [Fact]
    public void FormulaCellsReadOnlyTheirStoredCachesAndPreserveMissingOrErrorEvidence()
    {
        var bytes = Workbook(sheet =>
        {
            sheet.Cell("L6").FormulaA1 = "1/0";
            sheet.Cell("Q6").FormulaA1 = "1/0";
            sheet.Cell("L7").FormulaA1 = "CHAR(65)";
            sheet.Cell("L8").FormulaA1 = "CHAR(65)";
            sheet.Cell("L9").FormulaA1 = "1/0";
            sheet.Cell("O7").FormulaA1 = "CHAR(65)";
        }, part =>
        {
            Cache(part, "L6", CellValues.String, " b ");
            Cache(part, "Q6", CellValues.String, "workspace-cache");
            Cache(part, "L7", CellValues.String, null);
            Cache(part, "L8", CellValues.String, "");
            Cache(part, "L9", CellValues.Error, "#DIV/0!");
            Cache(part, "O7", CellValues.String, null);
        });

        var document = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes);

        Assert.Equal(" b ", document.Rows[0].Winner.Text); // Evaluating 1/0 would replace this valid cache with an error.
        Assert.True(document.Rows[0].Winner.HasFormula);
        Assert.Null(document.Rows[0].Winner.ReadError);
        Assert.True(document.Rows[0].WorkspaceId.HasFormula);
        Assert.NotNull(document.Rows[1].Winner.ReadError);
        Assert.NotNull(document.Rows[1].OptionA.ReadError);
        Assert.Equal("", document.Rows[2].Winner.Text);
        Assert.Null(document.Rows[2].Winner.ReadError); // An explicitly cached empty string is not a missing cache.
        Assert.Equal(WorkspaceRecordCellKind.Error, document.Rows[3].Winner.Kind);
        Assert.NotNull(document.Rows[3].Winner.ReadError);
    }

    [Fact]
    public void RawKindsDistinguishTypedDatesFromDateLookingTextAndNumericSerials()
    {
        var bytes = Workbook(sheet =>
        {
            sheet.Cell("B6").Value = "2026-09-13T00:00:00.0000000";
            sheet.Cell("V6").Value = new DateTime(2026, 9, 13, 12, 34, 56);
            sheet.Cell("J6").Value = 18.75;
            sheet.Cell("U6").Value = true;
            sheet.Cell("I6").Value = " 21–10， 21-12 ";
            sheet.Cell("V7").Value = 46278;
            sheet.Cell("J7").Value = TimeSpan.FromMinutes(18);
        });

        var rows = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes).Rows;

        Assert.Equal(WorkspaceRecordCellKind.Text, rows[0].RecordDay.Kind);
        Assert.Equal(WorkspaceRecordCellKind.DateTime, rows[0].ActualPlayedDay.Kind);
        Assert.Equal("2026-09-13T12:34:56.0000000", rows[0].ActualPlayedDay.Text);
        Assert.Equal("18.75", rows[0].Duration.Text);
        Assert.Equal(WorkspaceRecordCellKind.Boolean, rows[0].ResultKind.Kind);
        Assert.Equal(" 21–10， 21-12 ", rows[0].Score.Text);
        Assert.Equal(WorkspaceRecordCellKind.Number, rows[1].ActualPlayedDay.Kind);
        Assert.NotNull(rows[1].Duration.ReadError);
    }

    [Fact]
    public void ReadingCapturedSnapshotNeverReopensAReplacedSourceFile()
    {
        var directory = Directory.CreateTempSubdirectory("workspace-record-capture-").FullName;
        try
        {
            var path = Path.Combine(directory, "记录.xlsx");
            File.WriteAllBytes(path, Workbook(sheet => sheet.Cell("N6").Value = "captured"));
            var captured = File.ReadAllBytes(path);
            File.WriteAllBytes(path, Workbook(sheet => sheet.Cell("N6").Value = "replacement"));
            var document = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(captured);
            Assert.Equal("captured", Assert.Single(document.Rows).MatchId.Text);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(captured)), document.ContentHash);
            Assert.NotEqual(document.ContentHash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnreadableWorkbookOrMissingRecordSheetIsADocumentFailure()
    {
        var reader = new WorkspaceMatchRecordReader();
        Assert.Throws<ExcelImportException>(() => { reader.ReadWorkspaceRecord("not an xlsx"u8.ToArray()); });
        using var book = new XLWorkbook(); book.AddWorksheet("不是记录表");
        using var stream = new MemoryStream(); book.SaveAs(stream);
        Assert.Throws<ExcelImportException>(() => { reader.ReadWorkspaceRecord(stream.ToArray()); });
    }

    internal static byte[] Workbook(Action<IXLWorksheet> fill, Action<WorksheetPart>? patch = null)
    {
        using var book = new XLWorkbook(); fill(book.AddWorksheet("对阵记录表"));
        using var stream = new MemoryStream(); book.SaveAs(stream);
        if (patch is not null)
        {
            stream.Position = 0;
            using var document = SpreadsheetDocument.Open(stream, true);
            patch(document.WorkbookPart!.WorksheetParts.Single());
        }
        return stream.ToArray();
    }

    private static void Cache(WorksheetPart part, string address, CellValues kind, string? value)
    {
        var cell = part.Worksheet.Descendants<Cell>().Single(cell => cell.CellReference?.Value == address);
        cell.DataType = kind;
        cell.CellValue = value is null ? null : new CellValue(value);
    }
}
