using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using Xunit;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceScoreSheetWriterTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("workspace-score-").FullName;
    [Fact]
    public void LiteralPlayerAndPartnerAreNotSplitByDisplayPunctuation()
    {
        var workspace = WorkspaceMaterialTestFixture.Create();
        var project = workspace.Projects.Single(p => p.Discipline == EventDiscipline.MenDoubles);
        var node = WorkspaceMaterialTestFixture.LiteralRoot(project);
        using var book = BuildWorkbook(new(workspace), [new(new(project.Id, node.Id), WorkspaceMaterialTestFixture.FirstDay)]);
        var sideA = node.SideA is EntrantSource.Participant a && a.Players.Any(p => p.Name == WorkspaceMaterialTestFixture.LiteralPlayer);
        foreach (var start in new[] { 39, 44, 49 })
        {
            var row = start + (sideA ? 0 : 2);
            Assert.Equal("李  / 明【甲】胜者", book.Worksheet(1).Cell(row, 1).GetString());
            Assert.Equal("搭 档 / 0", book.Worksheet(1).Cell(row + 1, 1).GetString());
        }
    }

    [Fact]
    public void PendingCoverageKeepsOriginalPrecisePlanWithoutPretendingItMoved()
    {
        var workspace = WorkspaceMaterialTestFixture.Create(); var project = workspace.Projects[0];
        var node = project.MatchGraph!.Matches.First(n => workspace.Schedule!.Placements[n.Id].StartTime.Ticks % TimeSpan.TicksPerMinute != 0);
        using var book = BuildWorkbook(new(workspace), [new(new(project.Id, node.Id), WorkspaceMaterialTestFixture.SecondDay)]);
        var sheet = book.Worksheet(1);
        Assert.Equal("待安排", sheet.Cell("B26").GetString()); Assert.Equal("待安排", sheet.Cell("AS8").GetString());
        Assert.Contains("原计划", sheet.Cell("A1").GetString());
        Assert.Contains("2026-09-19", sheet.Cell("A1").GetString());
        Assert.Contains("09:00:00.0001234", sheet.Cell("A1").GetString());
    }

    [Fact]
    public void UnresolvedSidesStayBlankThenBothTypedOutcomesFillTheRealTemplate()
    {
        var workspace = WorkspaceMaterialTestFixture.Create(); var project = workspace.Projects[1];
        var future = project.MatchGraph!.Matches.Where(n => n.Dependencies.Count > 0).ToArray();
        var rows = future.Select(n => new WorkspaceRecordExportRow(new(project.Id, n.Id), WorkspaceMaterialTestFixture.FirstDay)).ToArray();
        using (var pending = BuildWorkbook(new(workspace), rows))
            Assert.All(pending.Worksheets, sheet => Assert.All(new[] { 39, 40, 41, 42 }, row => Assert.True(sheet.Cell(row, 1).IsEmpty())));
        workspace = WorkspaceMaterialTestFixture.CompleteRoots(workspace, WorkspaceMaterialTestFixture.FirstDay);
        using var resolved = BuildWorkbook(new(workspace), rows);
        for (var i = 0; i < future.Length; i++)
        foreach (var (source, line) in new[] { (future[i].SideA, 39), (future[i].SideB, 41) })
        {
            var sourceId = source is EntrantSource.WinnerOf winner ? winner.MatchId : ((EntrantSource.LoserOf)source).MatchId;
            var result = workspace.Results[new(project.Id, sourceId)];
            var actual = source is EntrantSource.WinnerOf ? result.Winner : result.Loser;
            Assert.Equal(actual.Players[0].Name, resolved.Worksheet(i + 1).Cell(line, 1).GetString());
            Assert.Equal(actual.Players[1].Name, resolved.Worksheet(i + 1).Cell(line + 1, 1).GetString());
        }
    }

    [Fact]
    public void SameDaySinglesKeepPreciseTimeAndTemplateGeometryWithBlankPartnerLines()
    {
        var workspace = WorkspaceMaterialTestFixture.Create(); var context = new WorkspaceScheduleExportContext(workspace);
        var key = context.MatchKeys.First(k => k.ProjectId == workspace.Projects[0].Id);
        var before = JsonSerializer.Serialize(workspace);
        using var book = BuildWorkbook(context, [new(key, WorkspaceMaterialTestFixture.FirstDay)]);
        var path = Path.Combine(directory, "template.xlsx"); book.SaveAs(path);
        using var reopened = new XLWorkbook(path); var sheet = reopened.Worksheet(1);
        using var stream = typeof(ScoreSheetExcelWriter).Assembly.GetManifestResourceStream("BadmintonDraw.Excel.Templates.IndividualScoreSheetTemplate.xlsx")!;
        using var template = new XLWorkbook(stream); var original = template.Worksheet(1);
        Assert.Equal(original.MergedRanges.Select(r => r.RangeAddress.ToString()), sheet.MergedRanges.Select(r => r.RangeAddress.ToString()));
        for (var row = 1; row <= 65; row++) Assert.Equal(original.Row(row).Height, sheet.Row(row).Height);
        Assert.Equal("羽毛球比赛记分表", sheet.Cell("A1").GetString());
        Assert.Equal("09:00:00.0001234", sheet.Cell("B26").GetString());
        Assert.Equal(workspace.Projects[0].DisplayName, sheet.Cell("B8").GetString());
        Assert.Equal("2026-09-19", sheet.Cell("B20").GetString()); Assert.Equal(1, sheet.Cell("B32").GetValue<int>());
        Assert.False(sheet.Cell("A39").IsEmpty()); Assert.True(sheet.Cell("A40").IsEmpty());
        Assert.False(sheet.Cell("A41").IsEmpty()); Assert.True(sheet.Cell("A42").IsEmpty());
        Assert.Equal(before, JsonSerializer.Serialize(workspace));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompletedReprintsSeparateOriginalRecordAndActualDays(bool crossDay, bool unknownActualDay)
    {
        var workspace = WorkspaceMaterialTestFixture.CompleteRoots(WorkspaceMaterialTestFixture.Create(),
            unknownActualDay ? null : WorkspaceMaterialTestFixture.SecondDay);
        var project = workspace.Projects[0]; var root = project.MatchGraph!.Matches.First(n => n.Dependencies.Count == 0);
        var recordDay = crossDay ? WorkspaceMaterialTestFixture.SecondDay : WorkspaceMaterialTestFixture.FirstDay;
        using var book = BuildWorkbook(new(workspace), [new(new(project.Id, root.Id), recordDay)]);
        var sheet = book.Worksheet(1); var caption = sheet.Cell("A1").GetString();
        Assert.Contains("记录日期 " + recordDay.ToString("yyyy-MM-dd"), caption);
        Assert.Contains("原计划 2026-09-19", caption);
        Assert.Contains("实际日期 " + (unknownActualDay ? "未知" : "2026-09-20"), caption);
        Assert.DoesNotContain("待安排", sheet.Cell("B26").GetString());
        Assert.Contains(workspace.Schedule!.Placements[root.Id].Court, caption);
    }

    [Fact]
    public void IndividualRejectsEmptyDuplicateForeignDateAndWrongFamilyBeforeWriting()
    {
        var context = new WorkspaceScheduleExportContext(WorkspaceMaterialTestFixture.Create());
        var row = new WorkspaceRecordExportRow(context.MatchKeys[0], WorkspaceMaterialTestFixture.FirstDay);
        var path = Path.Combine(directory, "should-not-exist", "invalid.pdf"); var writer = new ScoreSheetExcelWriter();
        foreach (var rows in new WorkspaceRecordExportRow[][] { [], [row, row], [row with { Key = new(Guid.NewGuid(), row.Key.MatchId) }],
                     [row with { RecordDay = new(2030, 1, 1) }], [null!] })
            Assert.Throws<WorkspaceValidationException>(() => writer.WriteIndividualMatchScorePdf(path, context, rows));
        var team = new WorkspaceScheduleExportContext(WorkspaceMaterialTestFixture.Create(team: true));
        Assert.Equal("export.kind", Assert.Throws<WorkspaceValidationException>(() => writer.WriteIndividualMatchScorePdf(path, team,
            [new(team.MatchKeys[0], WorkspaceMaterialTestFixture.FirstDay)])).Code);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void PublicIndividualPdfContainsOnePagePerSelectedMatchInCallerOrder()
    {
        var context = new WorkspaceScheduleExportContext(WorkspaceMaterialTestFixture.Create());
        var rows = context.MatchKeys.Take(2).Reverse().Select(k => new WorkspaceRecordExportRow(k, WorkspaceMaterialTestFixture.FirstDay)).ToArray();
        var path = Path.Combine(directory, "two-sheets.pdf");
        new ScoreSheetExcelWriter().WriteIndividualMatchScorePdf(path, context, rows);
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 1000); Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.Equal(2, Regex.Matches(System.Text.Encoding.Latin1.GetString(bytes), @"/Type\s*/Page\b").Count);
        using var book = BuildWorkbook(context, rows);
        for (var i = 0; i < rows.Length; i++)
            Assert.Equal(context.Projects[rows[i].Key.ProjectId].DisplayName, book.Worksheet(i + 1).Cell("B8").GetString());
    }

    [Fact]
    public void TeamBlocksPreserveEditableRowsAndExplicitOriginalActualCoverage()
    {
        var workspace = WorkspaceMaterialTestFixture.CompleteRoots(WorkspaceMaterialTestFixture.Create(team: true), null);
        var context = new WorkspaceScheduleExportContext(workspace);
        var rows = context.MatchKeys.Take(2).Select(k => new WorkspaceRecordExportRow(k, WorkspaceMaterialTestFixture.SecondDay)).ToArray();
        var path = Path.Combine(directory, "team.xlsx");
        WriteTeam(path, context, rows);
        using var book = new XLWorkbook(path); var sheet = book.Worksheet("团体记分表");
        Assert.Equal(36, sheet.PageSetup.PrintAreas.Single().LastRow().RowNumber());
        Assert.Equal(new[] { 18 }, sheet.PageSetup.RowBreaks);
        Assert.Equal(XLPageOrientation.Portrait, sheet.PageSetup.PageOrientation);
        Assert.Equal(XLPaperSize.A4Paper, sheet.PageSetup.PaperSize);
        foreach (var start in new[] { 1, 19 })
        {
            Assert.Contains("学院", sheet.Cell(start + 1, 1).GetString());
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal($"第{i + 1}场", sheet.Cell(start + 8 + i, 1).GetString());
                Assert.All(Enumerable.Range(2, 6), column => Assert.True(sheet.Cell(start + 8 + i, column).IsEmpty()));
            }
            Assert.Equal("裁判长签名", sheet.Cell(start + 14, 7).GetString());
            var note = sheet.Cell(start + 16, 2).GetString();
            Assert.Contains("原计划 2026-09-19", note); Assert.Contains("本页记录日期 2026-09-20", note);
            Assert.Contains("实际比赛日期 未知", note);
        }
    }

    [Fact]
    public void EmptyTeamSelectionDoesNotWriteAnExampleWorkbook()
    {
        var context = new WorkspaceScheduleExportContext(WorkspaceMaterialTestFixture.Create(team: true));
        var path = Path.Combine(directory, "empty-team.xlsx");
        Assert.Equal("export.no-matches", Assert.Throws<WorkspaceValidationException>(() => WriteTeam(path, context, [])).Code);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TeamCarryoverAndUnresolvedSourcesUseTypedLabelsWithoutMovingOrGuessing()
    {
        var workspace = WorkspaceMaterialTestFixture.Create(team: true, teamKnockout: true);
        var before = JsonSerializer.Serialize(workspace); var context = new WorkspaceScheduleExportContext(workspace);
        var key = context.MatchKeys.First(k => context.Nodes[k].Dependencies.Count > 0);
        var path = Path.Combine(directory, "team-pending.xlsx");
        WriteTeam(path, context, [new(key, WorkspaceMaterialTestFixture.SecondDay)]);
        using var book = new XLWorkbook(path); var sheet = book.Worksheet(1);
        Assert.Contains("待定：", sheet.Cell("A2").GetString());
        Assert.DoesNotContain("学院", sheet.Cell("A2").GetString());
        Assert.Equal("待安排", sheet.Cell("D5").GetString()); Assert.Equal("待安排", sheet.Cell("E5").GetString());
        Assert.Contains("原计划 2026-09-19", sheet.Cell("B17").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(workspace));
    }

    [Fact]
    public void TeamWriterRejectsAmbiguousSelectionsAndIndividualFamilyBeforeCreatingDirectories()
    {
        var context = new WorkspaceScheduleExportContext(WorkspaceMaterialTestFixture.Create(team: true));
        var row = new WorkspaceRecordExportRow(context.MatchKeys[0], WorkspaceMaterialTestFixture.FirstDay);
        var path = Path.Combine(directory, "should-not-exist", "team.xlsx");
        foreach (var rows in new WorkspaceRecordExportRow[][] { [row, row], [row with { Key = new(Guid.NewGuid(), row.Key.MatchId) }],
                     [row with { RecordDay = new(2030, 1, 1) }], [null!] })
            Assert.Throws<WorkspaceValidationException>(() => WriteTeam(path, context, rows));
        var individual = new WorkspaceScheduleExportContext(WorkspaceMaterialTestFixture.Create());
        Assert.Equal("export.kind", Assert.Throws<WorkspaceValidationException>(() => WriteTeam(path, individual,
            [new(individual.MatchKeys[0], WorkspaceMaterialTestFixture.FirstDay)])).Code);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void SelectionIsSnapshottedOnceBeforeValidationAndRendering()
    {
        var context = new WorkspaceScheduleExportContext(WorkspaceMaterialTestFixture.Create());
        var rows = new SingleEnumerationRows(context.MatchKeys.Take(2)
            .Select(k => new WorkspaceRecordExportRow(k, WorkspaceMaterialTestFixture.FirstDay)).ToArray());
        using var book = BuildWorkbook(context, rows);
        Assert.Equal(2, book.Worksheets.Count);
        Assert.Equal(context.Projects[rows[0].Key.ProjectId].DisplayName, book.Worksheet(1).Cell("B8").GetString());
        Assert.Equal(context.Projects[rows[1].Key.ProjectId].DisplayName, book.Worksheet(2).Cell("B8").GetString());
    }

    [Fact]
    public void OverlongCaptionFailsBeforeSavingInsteadOfShrinkingAwayOriginalPlan()
    {
        var workspace = WorkspaceMaterialTestFixture.WithLongCourt(WorkspaceMaterialTestFixture.Create());
        var context = new WorkspaceScheduleExportContext(workspace);
        var key = context.MatchKeys.First(k => context.Placements[k].Court.Length > 100);
        var validKey = context.MatchKeys.First(k => context.Placements[k].Court == "B2");
        WorkspaceRecordExportRow[] rows = [new(validKey, WorkspaceMaterialTestFixture.SecondDay), new(key, WorkspaceMaterialTestFixture.SecondDay)];
        var path = Path.Combine(directory, "not-created", "unreadable.pdf");
        var before = JsonSerializer.Serialize(workspace);
        var error = Assert.Throws<WorkspaceValidationException>(() => new ScoreSheetExcelWriter().WriteIndividualMatchScorePdf(path,
            context, rows));
        Assert.Equal("export.layout", error.Code);
        Assert.Contains(context.Projects[key.ProjectId].DisplayName, error.Message);
        Assert.Contains(context.Nodes[key].DisplayName, error.Message);
        Assert.Contains("场地", error.Message); Assert.Contains("缩短", error.Message);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "existing output must survive failed layout validation");
        Assert.Throws<WorkspaceValidationException>(() => new ScoreSheetExcelWriter().WriteIndividualMatchScorePdf(path, context, rows));
        Assert.Equal("existing output must survive failed layout validation", File.ReadAllText(path));
        Assert.Equal(before, JsonSerializer.Serialize(workspace));
    }

    private sealed class SingleEnumerationRows(WorkspaceRecordExportRow[] rows) : IReadOnlyList<WorkspaceRecordExportRow>
    {
        private int enumerations;
        public int Count => rows.Length;
        public WorkspaceRecordExportRow this[int index] => rows[index];
        public IEnumerator<WorkspaceRecordExportRow> GetEnumerator() => ++enumerations == 1
            ? ((IEnumerable<WorkspaceRecordExportRow>)rows).GetEnumerator()
            : throw new InvalidOperationException("Caller selection must not be re-enumerated during rendering.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void WriteTeam(string path, WorkspaceScheduleExportContext context, IReadOnlyList<WorkspaceRecordExportRow> rows)
        => new ScoreSheetExcelWriter().WriteTeamScoreSheets(path, context, rows);

    private static XLWorkbook BuildWorkbook(WorkspaceScheduleExportContext context, IReadOnlyList<WorkspaceRecordExportRow> rows)
        => ScoreSheetExcelWriter.BuildIndividualScoreSheetWorkbook(context, rows);

    public void Dispose() => Directory.Delete(directory, true);
}
