using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class BlankTemplateWriterTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("blank-template-tests-").FullName;

    [Fact]
    public void ParticipantTemplateKeepsEveryImportRowBlankAndExamplesOutsideTheRoster()
    {
        var path = Path.Combine(directory, "roster", "blank.xlsx");
        new ParticipantTemplateWriter().WriteBlank(path);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(1);
        Assert.Equal("参赛名单", sheet.Name);
        Assert.Equal(["姓名", "学号", "学院/学部", "搭档姓名", "搭档学号", "搭档学院/学部", "是否种子", "种子序号", "备注"],
            sheet.Range("A1:I1").Cells().Select(cell => cell.GetString()));
        Assert.All(sheet.CellsUsed(XLCellsUsedOptions.Contents), cell => Assert.Equal(1, cell.Address.RowNumber));
        Assert.True(sheet.Row(2).Height >= 40);
        Assert.False(sheet.Protection.IsProtected);
        Assert.Equal("@", sheet.Column(2).Style.NumberFormat.Format);
        Assert.Equal("@", sheet.Column(5).Style.NumberFormat.Format);
        Assert.Equal(2, workbook.Worksheets.Count);
        Assert.NotEmpty(workbook.Worksheet("填写说明").CellsUsed(XLCellsUsedOptions.Contents));
        foreach (var kind in new[] { EventKind.Singles, EventKind.Doubles, EventKind.Team })
            Assert.Empty(new ParticipantExcelReader().ReadParticipants(path, kind));
    }

    [Theory]
    [InlineData(EventKind.Singles, "李明", "001234", "理学院", "", "", "李明")]
    [InlineData(EventKind.Doubles, "李明", "001234", "理学院", "陈华", "005678", "[李明 陈华]")]
    [InlineData(EventKind.Team, "", "", "理学院", "", "", "理学院")]
    public void FilledGenericRosterImportsWithExistingReader(EventKind kind, string name, string studentId,
        string college, string partner, string partnerStudentId, string displayName)
    {
        var path = Path.Combine(directory, "filled.xlsx");
        new ParticipantTemplateWriter().WriteBlank(path);
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet(1);
            sheet.Cell("A2").Value = name;
            sheet.Cell("B2").Value = studentId;
            sheet.Cell("C2").Value = college;
            sheet.Cell("D2").Value = partner;
            sheet.Cell("E2").Value = partnerStudentId;
            workbook.Save();
        }

        var reader = new ParticipantExcelReader();
        Assert.Equal(kind, reader.DetectEventKind(path));
        var participant = Assert.Single(reader.ReadParticipants(path, kind));
        Assert.Equal(displayName, participant.DisplayName);
        if (kind != EventKind.Team) Assert.Equal("001234", participant.PrimaryStudentId);
        if (kind == EventKind.Doubles) Assert.Equal("005678", participant.PartnerStudentId);
    }

    [Fact]
    public void IndividualExcelPreservesOfficialFormGeometryWithoutInventedMatchFields()
    {
        var path = Path.Combine(directory, "individual", "blank.xlsx");
        new ScoreSheetExcelWriter().WriteBlankIndividualExcel(path);

        using var workbook = new XLWorkbook(path);
        var sheet = Assert.Single(workbook.Worksheets);
        using var stream = typeof(ScoreSheetExcelWriter).Assembly
            .GetManifestResourceStream("BadmintonDraw.Excel.Templates.IndividualScoreSheetTemplate.xlsx")!;
        using var original = new XLWorkbook(stream);
        Assert.Equal(original.Worksheet(1).MergedRanges.Select(range => range.RangeAddress.ToString()),
            sheet.MergedRanges.Select(range => range.RangeAddress.ToString()));
        for (var row = 1; row <= 65; row++) Assert.Equal(original.Worksheet(1).Row(row).Height, sheet.Row(row).Height);
        Assert.Equal("羽毛球比赛记分表", sheet.Cell("A1").GetString());
        foreach (var address in new[] { "B8", "B14", "B20", "B26", "B32", "AS8", "AS14", "AS20", "AS26",
                     "M8", "AA8", "M16", "AA16", "M24", "AA24", "AG32", "E59", "W59", "AP59" })
            Assert.True(sheet.Cell(address).IsEmpty(), $"Field {address} must be blank.");
        Assert.All(sheet.Range("A39:AX57").Cells(), cell => Assert.True(cell.IsEmpty()));
        Assert.False(sheet.Protection.IsProtected);
        Assert.Equal(XLPaperSize.A4Paper, sheet.PageSetup.PaperSize);
        Assert.Equal(XLPageOrientation.Landscape, sheet.PageSetup.PageOrientation);
        Assert.Single(sheet.PageSetup.PrintAreas);
        Assert.Equal(1, sheet.PageSetup.PagesWide);
        Assert.Equal(1, sheet.PageSetup.PagesTall);
        AssertNoIdentityMetadata(sheet);
    }

    [Fact]
    public void TeamExcelProvidesBlankMetadataCompetitorsScoresAndSignatureSpaces()
    {
        var path = Path.Combine(directory, "team", "blank.xlsx");
        new ScoreSheetExcelWriter().WriteBlankTeamExcel(path);

        using var workbook = new XLWorkbook(path);
        var sheet = Assert.Single(workbook.Worksheets);
        Assert.Contains("团体赛记分表", sheet.Cell("A1").GetString());
        Assert.Equal("A队", sheet.Cell("A2").GetString());
        Assert.Equal("B队", sheet.Cell("D2").GetString());
        foreach (var address in new[] { "B2", "E2", "A5", "B5", "C5", "D5", "E5", "B15", "E15", "G16", "B17" })
            Assert.True(sheet.Cell(address).IsEmpty(), $"Field {address} must be blank.");
        for (var row = 9; row <= 13; row++)
        {
            Assert.Equal($"第{row - 8}场", sheet.Cell(row, 1).GetString());
            Assert.All(sheet.Range(row, 2, row, 7).Cells(), cell => Assert.True(cell.IsEmpty()));
        }
        Assert.Equal("裁判长签名", sheet.Cell("G15").GetString());
        Assert.False(sheet.Protection.IsProtected);
        Assert.Equal(XLPageOrientation.Portrait, sheet.PageSetup.PageOrientation);
        Assert.Equal(XLPaperSize.A4Paper, sheet.PageSetup.PaperSize);
        Assert.Equal("A1:G18", sheet.PageSetup.PrintAreas.Single().RangeAddress.ToStringRelative());
        Assert.Equal(1, sheet.PageSetup.PagesWide);
        Assert.Equal(1, sheet.PageSetup.PagesTall);
        AssertNoIdentityMetadata(sheet);
    }

    [Theory]
    [InlineData(false, 841.89, 595.28)]
    [InlineData(true, 595.28, 841.89)]
    public void BlankScorePdfContainsOneA4PageWithVisibleFormContent(bool team, double width, double height)
    {
        var path = Path.Combine(directory, "pdf", team ? "team.pdf" : "individual.pdf");
        var writer = new ScoreSheetExcelWriter();
        if (team) writer.WriteBlankTeamPdf(path); else writer.WriteBlankIndividualPdf(path);

        var bytes = File.ReadAllBytes(path);
        var pdf = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-", pdf);
        Assert.True(bytes.Length > 1000);
        Assert.Single(Regex.Matches(pdf, @"/Type\s*/Page\b"));
        var bounds = Regex.Match(pdf, @"/MediaBox\s*\[0 0 ([\d.]+) ([\d.]+)\]");
        Assert.True(bounds.Success);
        Assert.InRange(Math.Abs(width - double.Parse(bounds.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)), 0, 0.5);
        Assert.InRange(Math.Abs(height - double.Parse(bounds.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)), 0, 0.5);
        var contentStreams = ReadPdfStreams(bytes).ToArray();
        Assert.Contains(contentStreams, content => Regex.IsMatch(content, @"\bBT\b"));
        Assert.Contains(contentStreams, content => Regex.Matches(content, @"\bre\b").Count > 20);
        Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", pdf);
    }

    [Theory]
    [InlineData(false, "842", "595")]
    [InlineData(true, "595", "842")]
    public void SheetPdfRendererKeepsLandscapeByDefaultAndSupportsExplicitPortrait(bool portrait, string width, string height)
    {
        var workbookPath = Path.Combine(directory, "form.xlsx");
        var pdfPath = Path.Combine(directory, "form.pdf");
        new ScoreSheetExcelWriter().WriteBlankTeamExcel(workbookPath);
        var renderer = new DrawResultVisualWriter();
        if (portrait) renderer.WriteSheetsA4Pdf(pdfPath, workbookPath, ["团体计分表"], portrait: true);
        else renderer.WriteSheetsA4Pdf(pdfPath, workbookPath, ["团体计分表"]);

        var pdf = Encoding.Latin1.GetString(File.ReadAllBytes(pdfPath));
        Assert.Matches($@"/MediaBox\s*\[0 0 {width} {height}\]", pdf);
        Assert.Single(Regex.Matches(pdf, @"/Type\s*/Page\b"));
    }

    private static void AssertNoIdentityMetadata(IXLWorksheet sheet)
    {
        var content = string.Join("\n", sheet.CellsUsed(XLCellsUsedOptions.Contents).Select(cell => cell.GetString()));
        foreach (var marker in new[] { "张三", "李四", "王五", "20260001", "待安排", "待定", "原计划", "SHA-256" })
            Assert.DoesNotContain(marker, content);
        Assert.DoesNotMatch(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", content);
    }

    private static IEnumerable<string> ReadPdfStreams(byte[] bytes)
    {
        var pdf = Encoding.Latin1.GetString(bytes);
        foreach (Match match in Regex.Matches(pdf, @"<<(.*?)>>\s*stream\r?\n", RegexOptions.Singleline))
        {
            var start = match.Index + match.Length;
            var end = pdf.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) continue;
            using var source = new MemoryStream(bytes, start, end - start);
            if (match.Groups[1].Value.Contains("/FlateDecode", StringComparison.Ordinal))
            {
                using var compressed = new ZLibStream(source, CompressionMode.Decompress);
                using var output = new MemoryStream();
                compressed.CopyTo(output);
                yield return Encoding.Latin1.GetString(output.ToArray());
            }
            else yield return pdf[start..end];
        }
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
