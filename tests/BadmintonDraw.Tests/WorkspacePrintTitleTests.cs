using System.IO.Compression;
using System.Xml.Linq;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspacePrintTitleTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("workspace-print-titles-").FullName;
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Theory]
    [InlineData("quality")]
    [InlineData("daily")]
    [InlineData("records")]
    [InlineData("timed-draw")]
    [InlineData("public-draw")]
    public void ActualMaterialUsesAbsolutePrintTitleReferencesReadableByOffice(string material)
    {
        var path = Path.Combine(directory, material + ".xlsx");
        if (material == "quality")
            new WorkspaceScheduleQualityExcelWriter().Write(path,
                new(WorkspaceScheduleQualityFixture.MultiProject()), WorkspaceScheduleQualityFixture.GeneratedAt);
        else if (material is "daily" or "records")
        {
            var workspace = WorkspaceRecordExportTestData.Create();
            var context = new WorkspaceScheduleExportContext(workspace);
            var rows = context.MatchKeys.Where(k => workspace.Schedule!.Placements[k.MatchId].DayLabel == "2026-09-13")
                .Select(k => new WorkspaceRecordExportRow(k, new(2026, 9, 13))).ToArray();
            if (material == "daily") new ScheduleExcelWriter().WriteDailySchedule(path, context, rows);
            else new WorkspaceMatchRecordWriter().Write(path, context, rows);
        }
        else
        {
            using var fixture = new WorkspaceTimedDrawTestFixture(CompetitionMode.SinglesRoundRobin, 5);
            if (material == "timed-draw")
                new DrawResultExcelWriter().WriteTimed(path, new(fixture.Workspace), fixture.Project.Id, fixture.ExportContext);
            else new DrawResultExcelWriter().Write(path, fixture.Draw, fixture.Project.Roster!.Participants,
                context: fixture.ExportContext);
        }

        // ClosedXML's in-memory PageSetup values round-trip even with relative references,
        // but Calc treats those Print_Titles as an ordinary name and omits continuation headers.
        using var zip = ZipFile.OpenRead(path);
        using var xml = zip.GetEntry("xl/workbook.xml")!.Open();
        var root = XDocument.Load(xml);
        var titles = root.Descendants(Spreadsheet + "definedName")
            .Where(n => (string?)n.Attribute("name") == "_xlnm.Print_Titles").ToArray();
        Assert.NotEmpty(titles);
        Assert.All(titles, n => Assert.Matches(@"^('[^']*(?:''[^']*)*'|[^!,]+)!\$[1-9][0-9]*:\$[1-9][0-9]*$", n.Value));
        Assert.Equal(titles.Length, titles.Select(n => (string?)n.Attribute("localSheetId")).Distinct().Count());
        using var book = new XLWorkbook(path);
        Assert.All(titles, n =>
        {
            var sheet = book.Worksheet(int.Parse(n.Attribute("localSheetId")!.Value) + 1);
            Assert.True(sheet.PageSetup.FirstRowToRepeatAtTop > 0);
            Assert.True(sheet.PageSetup.LastRowToRepeatAtTop >= sheet.PageSetup.FirstRowToRepeatAtTop);
            Assert.NotEmpty(sheet.PageSetup.PrintAreas);
        });
    }

    [Theory]
    [InlineData(true, false, "'裁判, A''s'!$2:$4")]
    [InlineData(false, true, "'裁判, A''s'!$B:$AA")]
    [InlineData(true, true, "'裁判, A''s'!$B:$AA,'裁判, A''s'!$2:$4")]
    [InlineData(false, false, null)]
    public void NormalizationPreservesSheetContentOtherNamesAndQuotedSheetIdentity(bool rows, bool columns, string? expected)
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("裁判, A's");
        sheet.Cell("A1").Value = "=literal";
        sheet.Cell("B2").FormulaA1 = "1+2";
        sheet.Range("C3:D3").Merge().Value = "合并中文";
        sheet.Column("D").Hide();
        sheet.PageSetup.PrintAreas.Add("A1:AA90");
        sheet.PageSetup.AddHorizontalPageBreak(30);
        book.DefinedNames.Add("KeptName", sheet.Range("A1:B2"));
        sheet.DefinedNames.Add("LocalName", sheet.Range("C3:D3"));
        if (rows) sheet.PageSetup.SetRowsToRepeatAtTop(2, 4);
        if (columns) sheet.PageSetup.SetColumnsToRepeatAtLeft(2, 27);
        book.AddWorksheet("无重复表头").Cell("A1").Value = "保持原样";
        var baseline = Path.Combine(directory, "before.xlsx");
        var actual = Path.Combine(directory, "after.xlsx");
        book.SaveAs(baseline);
        WorkbookPrintTitles.SaveAs(book, actual);

        using var before = ZipFile.OpenRead(baseline);
        using var after = ZipFile.OpenRead(actual);
        foreach (var entry in before.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) ||
            e.FullName is "xl/sharedStrings.xml" or "xl/styles.xml"))
        {
            using var originalPart = entry.Open(); using var changedPart = after.GetEntry(entry.FullName)!.Open();
            Assert.True(XNode.DeepEquals(Canonical(XDocument.Load(originalPart).Root!), Canonical(XDocument.Load(changedPart).Root!)),
                "Unchanged XML content required: " + entry.FullName);
        }
        using var left = before.GetEntry("xl/workbook.xml")!.Open();
        using var right = after.GetEntry("xl/workbook.xml")!.Open();
        var beforeXml = XDocument.Load(left); var afterXml = XDocument.Load(right);
        var names = afterXml.Descendants(Spreadsheet + "definedName").Where(n =>
            (string?)n.Attribute("name") == "_xlnm.Print_Titles").ToArray();
        if (expected is null) Assert.Empty(names);
        else Assert.Equal(expected, Assert.Single(names).Value);
        foreach (var title in beforeXml.Descendants(Spreadsheet + "definedName").Where(n =>
            (string?)n.Attribute("name") == "_xlnm.Print_Titles").ToArray()) title.Remove();
        foreach (var title in names) title.Remove();
        Assert.True(XNode.DeepEquals(Canonical(beforeXml.Root!), Canonical(afterXml.Root!)), "Only built-in print title references may change.");
        using var reopened = new XLWorkbook(actual);
        Assert.Equal(rows ? 2 : 0, reopened.Worksheet(1).PageSetup.FirstRowToRepeatAtTop);
        Assert.Equal(columns ? 27 : 0, reopened.Worksheet(1).PageSetup.LastColumnToRepeatAtLeft);
        Assert.Equal("1+2", reopened.Worksheet(1).Cell("B2").FormulaA1);
    }

    private static XElement Canonical(XElement element)
    {
        // Namespace declaration / attribute serialization order has no XML meaning.
        return new XElement(element.Name, element.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString())
            .Select(a => new XAttribute(a)), element.Nodes().Select(n => n is XElement child ? Canonical(child) : n));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
