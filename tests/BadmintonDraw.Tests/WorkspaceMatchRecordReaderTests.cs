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
    public void ArrayMembersRemainFormulaEvidenceWhenAnchorIsAnExampleAndCachesOrCellsAreMissing()
    {
        var bytes = Workbook(sheet =>
        {
            sheet.Range("Q5:Q9").FormulaArrayA1 = "\"cached-id\"";
            sheet.Cell("N6").Value = "actual-row";
            sheet.Cell("A50000").Style.Font.Bold = true;
        }, part =>
        {
            Cache(part, "Q5", CellValues.String, "cached-id");
            Cache(part, "Q6", CellValues.String, "cached-id");
            Cache(part, "Q7", CellValues.String, "");
            Cache(part, "Q8", CellValues.String, null);
            part.Worksheet.Descendants<Cell>().Single(c => c.CellReference?.Value == "Q9").Remove();
        });
        var rows = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes).Rows;
        Assert.Equal(new[] { 6, 7, 8, 9 }, rows.Select(r => r.Location.RowNumber));
        Assert.True(rows[0].WorkspaceId.HasFormula);
        Assert.Equal("cached-id", rows[0].WorkspaceId.Text); Assert.Null(rows[0].WorkspaceId.ReadError);
        Assert.True(rows[1].WorkspaceId.HasFormula); Assert.Equal("", rows[1].WorkspaceId.Text); Assert.Null(rows[1].WorkspaceId.ReadError);
        Assert.All(rows.Skip(2), row =>
        {
            Assert.True(row.WorkspaceId.HasFormula);
            Assert.Equal(WorkspaceRecordCellKind.Error, row.WorkspaceId.Kind);
            Assert.NotNull(row.WorkspaceId.ReadError);
        });
    }

    [Fact]
    public void SharedFormulaRangeRetainsMissingMemberRowsWithoutEvaluatingTheAnchor()
    {
        var bytes = Workbook(sheet =>
        {
            sheet.Cell("L5").FormulaA1 = "1/0";
            sheet.Cell("L6").Value = "A"; sheet.Cell("L7").Value = "B"; sheet.Cell("L8").Value = "A";
        }, part =>
        {
            var cells = part.Worksheet.Descendants<Cell>().ToDictionary(c => c.CellReference!.Value!);
            cells["L5"].CellFormula = new CellFormula("1/0") { FormulaType = CellFormulaValues.Shared, Reference = "L5:L8", SharedIndex = 0U };
            foreach (var address in new[] { "L6", "L7" })
                cells[address].CellFormula = new CellFormula { FormulaType = CellFormulaValues.Shared, SharedIndex = 0U };
            Cache(part, "L5", CellValues.String, "A"); Cache(part, "L6", CellValues.String, "A");
            Cache(part, "L7", CellValues.String, null); cells["L8"].Remove();
        });
        var rows = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes).Rows;
        Assert.Equal(new[] { 6, 7, 8 }, rows.Select(r => r.Location.RowNumber));
        Assert.True(rows[0].Winner.HasFormula); Assert.Equal("A", rows[0].Winner.Text);
        Assert.All(rows.Skip(1), row => { Assert.True(row.Winner.HasFormula); Assert.NotNull(row.Winner.ReadError); });
    }

    [Fact]
    public void EmptyStringFormulaCacheIsValidButEmptyNumericCacheIsUnreadableInput()
    {
        var bytes = Workbook(sheet =>
        {
            sheet.Cell("L6").FormulaA1 = "CHAR(65)"; sheet.Cell("L7").FormulaA1 = "CHAR(65)";
        }, part =>
        {
            Cache(part, "L6", CellValues.String, ""); Cache(part, "L7", CellValues.Number, "");
        });
        var rows = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes).Rows;
        Assert.Equal("", rows[0].Winner.Text); Assert.True(rows[0].Winner.HasFormula); Assert.Null(rows[0].Winner.ReadError);
        Assert.Equal(WorkspaceRecordCellKind.Text, rows[0].Winner.Kind);
        Assert.True(rows[1].Winner.HasFormula);
        Assert.Equal(WorkspaceRecordCellKind.Error, rows[1].Winner.Kind);
        Assert.NotNull(rows[1].Winner.ReadError);
    }

    [Theory]
    [InlineData("number", "not-a-number")]
    [InlineData("boolean", "")]
    [InlineData("boolean", "2")]
    [InlineData("error", "")]
    public void InvalidTypedFormulaCachesRemainLocalReadErrors(string kind, string cached)
    {
        var bytes = Workbook(sheet =>
        {
            sheet.Cell("L6").FormulaA1 = "CHAR(65)"; sheet.Cell("L7").FormulaA1 = "CHAR(66)";
        }, part =>
        {
            var type = kind switch { "number" => CellValues.Number, "boolean" => CellValues.Boolean, _ => CellValues.Error };
            Cache(part, "L6", type, cached); Cache(part, "L7", CellValues.String, "B");
        });
        var rows = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes).Rows;
        Assert.True(rows[0].Winner.HasFormula); Assert.Equal(WorkspaceRecordCellKind.Error, rows[0].Winner.Kind);
        Assert.NotNull(rows[0].Winner.ReadError); Assert.Equal("B", rows[1].Winner.Text);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void NonFiniteCachesRejectedByWorkbookLoaderCannotBecomePendingRows(string cached)
    {
        var bytes = Workbook(sheet => sheet.Cell("L6").FormulaA1 = "1", part => Cache(part, "L6", CellValues.Number, cached));
        Assert.Throws<ExcelImportException>(() => { new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes); });
    }

    [Fact]
    public void ValidZeroBooleanAndStyledDateCachesRetainTheirKindsWithoutCalculation()
    {
        var midnight = new DateTime(2026, 9, 13);
        var bytes = Workbook(sheet =>
        {
            sheet.Cell("J6").FormulaA1 = "1/0"; sheet.Cell("U6").FormulaA1 = "1/0";
            sheet.Cell("U7").FormulaA1 = "1/0";
            sheet.Cell("V6").Style.DateFormat.Format = "yyyy-mm-dd"; sheet.Cell("V6").FormulaA1 = "1/0";
            sheet.Range("W5:W9").FormulaArrayA1 = "1/0"; // Effective formulas entirely outside the record schema do not add rows.
        }, part =>
        {
            Cache(part, "J6", CellValues.Number, "0"); Cache(part, "U6", CellValues.Boolean, "0");
            Cache(part, "U7", CellValues.Boolean, "true");
            Cache(part, "V6", CellValues.Number, midnight.ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        var before = bytes.ToArray(); var rows = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes).Rows;
        Assert.Equal(new[] { 6, 7 }, rows.Select(r => r.Location.RowNumber));
        Assert.Equal(WorkspaceRecordCellKind.Number, rows[0].Duration.Kind); Assert.Equal("0", rows[0].Duration.Text);
        Assert.Equal(WorkspaceRecordCellKind.Boolean, rows[0].ResultKind.Kind); Assert.Equal("FALSE", rows[0].ResultKind.Text);
        Assert.Equal("TRUE", rows[1].ResultKind.Text);
        Assert.Equal(WorkspaceRecordCellKind.DateTime, rows[0].ActualPlayedDay.Kind);
        Assert.Equal(midnight.ToString("O"), rows[0].ActualPlayedDay.Text);
        Assert.All(new[] { rows[0].Duration, rows[0].ResultKind, rows[0].ActualPlayedDay, rows[1].ResultKind },
            cell => { Assert.True(cell.HasFormula); Assert.Null(cell.ReadError); });
        Assert.Equal(before, bytes);
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
