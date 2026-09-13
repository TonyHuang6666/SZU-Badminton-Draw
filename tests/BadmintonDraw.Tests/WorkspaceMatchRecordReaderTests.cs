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

    [Theory]
    [InlineData("number", "not-a-finite-number", true)]
    [InlineData("number", "not-a-finite-number", false)]
    [InlineData("number", "", false)]
    [InlineData("boolean", "2", false)]
    [InlineData("boolean", "", false)]
    public void InvalidTypedLiteralCannotBecomeBlankOrDisappear(string kind, string stored, bool omittedNumberType)
    {
        var bytes = Workbook(sheet =>
        {
            sheet.Cell("I6").Value = 1;
            sheet.Cell("I7").Value = 1;
            sheet.Cell("N7").Value = "existing-row";
        }, part =>
        {
            foreach (var address in new[] { "I6", "I7" })
            {
                var cell = part.Worksheet.Descendants<Cell>().Single(c => c.CellReference?.Value == address);
                cell.DataType = omittedNumberType ? null : kind == "number" ? CellValues.Number : CellValues.Boolean;
                cell.CellValue = new CellValue(stored);
            }
        });
        var before = bytes.ToArray();
        var document = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes);
        Assert.Equal(new[] { 6, 7 }, document.Rows.Select(row => row.Location.RowNumber));
        Assert.All(document.Rows, row =>
        {
            Assert.False(row.Score.HasFormula);
            Assert.Equal(WorkspaceRecordCellKind.Error, row.Score.Kind);
            Assert.Equal(stored, row.Score.Text);
            Assert.NotNull(row.Score.ReadError);
        });
        Assert.Equal(before, bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(before)), document.ContentHash);
    }

    [Fact]
    public void ValidTypedLiteralsKeepZeroBooleanDateAndLegitimateStyledBlanks()
    {
        var day = new DateTime(2026, 9, 13);
        var bytes = Workbook(sheet =>
        {
            sheet.Cell("J6").Value = 0;
            sheet.Cell("U6").Value = false;
            sheet.Cell("V6").Value = day;
            sheet.Cell("I6").Style.Font.Bold = true;
            sheet.Cell("A50000").Style.Font.Bold = true;
        });
        var row = Assert.Single(new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes).Rows);
        Assert.Equal(6, row.Location.RowNumber);
        Assert.Equal(WorkspaceRecordCellKind.Number, row.Duration.Kind); Assert.Equal("0", row.Duration.Text);
        Assert.Equal(WorkspaceRecordCellKind.Boolean, row.ResultKind.Kind); Assert.Equal("FALSE", row.ResultKind.Text);
        Assert.Equal(WorkspaceRecordCellKind.DateTime, row.ActualPlayedDay.Kind); Assert.Equal(day.ToString("O"), row.ActualPlayedDay.Text);
        Assert.Equal(WorkspaceRecordCellKind.Empty, row.Score.Kind);
        Assert.All(new[] { row.Duration, row.ResultKind, row.ActualPlayedDay, row.Score }, cell =>
        { Assert.False(cell.HasFormula); Assert.Null(cell.ReadError); });
    }

    [Theory]
    [InlineData("shared-outside", "999999")]
    [InlineData("number-inline", "physical text")]
    [InlineData("inline-value", "physical text")]
    public void InvalidPhysicalStringReferenceOrPayloadRemainsEvidence(string shape, string text)
    {
        var bytes = Workbook(sheet =>
        {
            sheet.Cell("I6").Value = 1; // No other value: this malformed row must not disappear.
            sheet.Cell("I7").Value = 1; sheet.Cell("N7").Value = "retained-row";
            sheet.Cell("I8").Value = "valid shared string";
        }, part =>
        {
            var table = ((SpreadsheetDocument)part.OpenXmlPackage).WorkbookPart!.SharedStringTablePart!.SharedStringTable!;
            table.Count = 1_000_000U; table.UniqueCount = 1_000_000U; // Declared counts do not create actual entries.
            foreach (var address in new[] { "I6", "I7" })
            {
                var cell = part.Worksheet.Descendants<Cell>().Single(c => c.CellReference?.Value == address);
                cell.DataType = shape == "shared-outside" ? CellValues.SharedString : shape == "number-inline" ? CellValues.Number : CellValues.InlineString;
                cell.CellValue = shape == "number-inline" ? null : new CellValue(text);
                cell.InlineString = shape == "number-inline" ? new InlineString(new Text(text)) : null;
            }
        });
        var before = bytes.ToArray();
        var rows = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes).Rows;
        Assert.Equal(new[] { 6, 7, 8 }, rows.Select(r => r.Location.RowNumber));
        Assert.All(rows.Take(2), row =>
        {
            Assert.Equal(WorkspaceRecordCellKind.Error, row.Score.Kind);
            Assert.Equal(text, row.Score.Text); Assert.NotNull(row.Score.ReadError); Assert.False(row.Score.HasFormula);
        });
        Assert.Equal("valid shared string", rows[2].Score.Text); Assert.Null(rows[2].Score.ReadError);
        Assert.Equal(before, bytes);
    }

    [Fact]
    public void SharedStringFormulaCacheMustResolveAnActualTableItem()
    {
        var bytes = Workbook(sheet =>
        {
            sheet.Cell("I6").FormulaA1 = "1/0";
            sheet.Cell("I7").Value = "genuine shared string";
        }, part => Cache(part, "I6", CellValues.SharedString, "999999"));
        var row = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes).Rows[0];
        Assert.True(row.Score.HasFormula); Assert.Equal("999999", row.Score.Text);
        Assert.Equal(WorkspaceRecordCellKind.Error, row.Score.Kind); Assert.NotNull(row.Score.ReadError);
    }

    [Fact]
    public void ActualSharedStringItemsAndInlinePayloadsRemainReadableIncludingEmptyText()
    {
        var bytes = Workbook(sheet =>
        {
            for (var row = 6; row <= 10; row++) sheet.Cell(row, 14).Value = "row-" + row;
            sheet.Cell("I6").Value = "真实共享\n文本";
            sheet.Cell("I7").Value = 1;
            sheet.Cell("I8").FormulaA1 = "1/0";
            sheet.Cell("I9").Value = 1; sheet.Cell("I10").Value = 1;
        }, part =>
        {
            var cells = part.Worksheet.Descendants<Cell>().ToDictionary(c => c.CellReference!.Value!);
            var table = ((SpreadsheetDocument)part.OpenXmlPackage).WorkbookPart!.SharedStringTablePart!.SharedStringTable!;
            var emptyIndex = table.Elements<SharedStringItem>().Count();
            table.Append(new SharedStringItem(new Text("")));
            table.Count = 0U; table.UniqueCount = 0U; // Actual items, not optional advisory metadata, are authoritative.
            cells["I7"].DataType = CellValues.SharedString; cells["I7"].CellValue = new CellValue(emptyIndex.ToString());
            Cache(part, "I8", CellValues.SharedString, cells["I6"].CellValue!.Text);
            foreach (var (address, content) in new[] { ("I9", "内联控制"), ("I10", "") })
            {
                cells[address].DataType = CellValues.InlineString; cells[address].CellValue = null;
                cells[address].InlineString = new InlineString(new Text(content));
            }
        });
        var rows = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(bytes).Rows;
        Assert.Equal(new[] { "真实共享\n文本", "", "真实共享\n文本", "内联控制", "" }, rows.Select(row => row.Score.Text));
        Assert.All(rows, row => Assert.Null(row.Score.ReadError));
        Assert.True(rows[2].Score.HasFormula); Assert.All(rows.Where((_, i) => i != 2), row => Assert.False(row.Score.HasFormula));
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
