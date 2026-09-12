using System.Text.Json;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceMatchRecordWriterTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("workspace-record-export-").FullName;
    private string PathFor(string name) => Path.Combine(directory, name + ".xlsx");

    [Fact]
    public void EveryRowCarriesLiteralQualifiedProvenanceAndPendingFactsStayEmpty()
    {
        var workspace = WorkspaceRecordExportTestData.Create(); var before = JsonSerializer.Serialize(workspace);
        var context = new WorkspaceScheduleExportContext(workspace);
        var rows = context.MatchKeys.Reverse().Select(key => new WorkspaceRecordExportRow(key,
            DateOnly.ParseExact(workspace.Schedule!.Placements[key.MatchId].DayLabel, "yyyy-MM-dd"))).ToArray();
        var path = PathFor("qualified"); new WorkspaceMatchRecordWriter().Write(path, context, rows);
        using var book = new XLWorkbook(path); var sheet = book.Worksheet("对阵记录表");
        for (var i = 0; i < rows.Length; i++)
        {
            var row = 6 + i; var key = rows[i].Key; var project = workspace.Projects.Single(p => p.Id == key.ProjectId);
            Assert.Equal(key.MatchId.ToString("D"), sheet.Cell(row, 14).GetString());
            Assert.Equal(workspace.Id.ToString("D"), sheet.Cell(row, 17).GetString());
            Assert.Equal(key.ProjectId.ToString("D"), sheet.Cell(row, 18).GetString());
            Assert.Equal(project.MatchGraph!.Revision, sheet.Cell(row, 19).GetString());
            Assert.Equal(project.Draw!.ConfirmedAt!.Value.ToString("O"), sheet.Cell(row, 20).GetString());
            Assert.All(new[] { 14, 17, 18, 19, 20 }, column => Assert.False(sheet.Cell(row, column).HasFormula));
            Assert.Equal(rows[i].RecordDay.ToString("yyyy-MM-dd"), sheet.Cell(row, 2).GetString());
            Assert.All(new[] { 9, 10, 12, 22 }, column => Assert.True(sheet.Cell(row, column).IsEmpty()));
        }
        Assert.Contains("记录", sheet.Cell("B4").GetString());
        Assert.Contains("实际", sheet.Cell("V4").GetString());
        Assert.All(Enumerable.Range(14, 7), column => Assert.True(sheet.Column(column).IsHidden));
        Assert.False(sheet.Column(21).IsHidden); Assert.False(sheet.Column(22).IsHidden);
        var print = sheet.PageSetup.PrintAreas.Single().RangeAddress;
        Assert.Equal(1, print.FirstAddress.RowNumber); Assert.Equal(1, print.FirstAddress.ColumnNumber);
        Assert.Equal(13, print.LastAddress.RowNumber); Assert.Equal(22, print.LastAddress.ColumnNumber);
        var preciseRow = 6 + Array.FindIndex(rows, r => r.Key.MatchId == workspace.Projects[0].MatchGraph!.Matches[0].Id);
        Assert.Contains("09:00:00.0001234", sheet.Cell(preciseRow, 3).GetString());
        Assert.Equal(before, JsonSerializer.Serialize(workspace));
        Assert.Equal(rows.Select(r => r.Key.MatchId.ToString("D")), new WorkspaceMatchRecordReader().ReadWorkspaceRecord(File.ReadAllBytes(path)).Rows.Select(r => r.MatchId.Text));
    }

    [Fact]
    public void TypedGuidFormulasResolveBothOutcomesAcrossDatesAndReverseDisplayOrder()
    {
        var workspace = WorkspaceRecordExportTestData.Create(); var context = new WorkspaceScheduleExportContext(workspace);
        var path = PathFor("guid-formulas");
        new WorkspaceMatchRecordWriter().Write(path, context, context.MatchKeys.Reverse().Select(key => new WorkspaceRecordExportRow(key, new(2026, 9, 14))).ToArray());
        using var book = new XLWorkbook(path); var sheet = book.Worksheet("对阵记录表");
        foreach (var project in workspace.Projects)
        {
            var first = Row(sheet, project.MatchGraph!.Matches[0].Id);
            var second = Row(sheet, project.MatchGraph.Matches[1].Id);
            sheet.Cell(first, 12).Value = project.SortOrder == 0 ? " a " : sheet.Cell(first, 16).GetString();
            sheet.Cell(second, 12).Value = project.SortOrder == 0 ? " b " : sheet.Cell(second, 16).GetString();
        }
        book.RecalculateAllFormulas();
        var ms = workspace.Projects[0].MatchGraph!.Matches;
        Assert.Equal("A【王【小明】 \"双引号\" / 空 格】", sheet.Cell(Row(sheet, ms[2].Id), 15).GetString());
        Assert.Equal("B【=1+1】", sheet.Cell(Row(sheet, ms[2].Id), 16).GetString());
        Assert.Equal("A【某某胜者】", sheet.Cell(Row(sheet, ms[3].Id), 15).GetString());
        Assert.Equal("B【第四位 / 选手】", sheet.Cell(Row(sheet, ms[3].Id), 16).GetString());
        var ws = workspace.Projects[1].MatchGraph!.Matches;
        Assert.Equal("A【某某胜者】", sheet.Cell(Row(sheet, ws[2].Id), 15).GetString());
        Assert.Equal("A【王【小明】 \"双引号\" / 空 格】", sheet.Cell(Row(sheet, ws[3].Id), 15).GetString());
        Assert.False(sheet.Cell(Row(sheet, ms[1].Id), 15).HasFormula); // Literal =1+1 and 某某胜者 are participants, never formulas/edges.
        Assert.Equal("A【=1+1】", sheet.Cell(Row(sheet, ms[1].Id), 15).GetString());

        sheet.Cell(Row(sheet, ms[0].Id), 12).Value = "A【冒名】";
        book.RecalculateAllFormulas();
        Assert.Equal("A【第1场胜者】", sheet.Cell(Row(sheet, ms[2].Id), 15).GetString());
        Assert.Equal("A【第1场负者】", sheet.Cell(Row(sheet, ms[3].Id), 15).GetString());
    }

    [Fact]
    public void FormulaChainsPreserveQuotedPlaceholdersAndUpdateWhenPrerequisiteWinnersChange()
    {
        var workspace = WorkspaceRecordExportTestData.Create(); var project = workspace.Projects[0];
        var nodes = project.MatchGraph!.Matches.ToArray();
        nodes[0] = nodes[0] with { DisplayName = "第【一】场 \"甲\"" };
        nodes[3] = nodes[3] with { SideA = new EntrantSource.WinnerOf(nodes[2].Id),
            SideB = new EntrantSource.LoserOf(nodes[2].Id), Dependencies = [nodes[2].Id] };
        project = project with { MatchGraph = project.MatchGraph with { Matches = nodes } };
        workspace = workspace with { Projects = [project, workspace.Projects[1]] };
        var context = new WorkspaceScheduleExportContext(workspace); var path = PathFor("chain");
        new WorkspaceMatchRecordWriter().Write(path, context, context.MatchKeys.Reverse()
            .Select(key => new WorkspaceRecordExportRow(key, new(2026, 9, 14))).ToArray());
        using var book = new XLWorkbook(path); var sheet = book.Worksheet("对阵记录表");
        book.RecalculateAllFormulas();
        Assert.Equal("A【第【一】场 \"甲\"胜者】", sheet.Cell(Row(sheet, nodes[2].Id), 15).GetString());
        sheet.Cell(Row(sheet, nodes[0].Id), 12).Value = "A";
        sheet.Cell(Row(sheet, nodes[1].Id), 12).Value = "B";
        sheet.Cell(Row(sheet, nodes[2].Id), 12).Value = "A";
        book.RecalculateAllFormulas();
        Assert.Equal("A【" + WorkspaceRecordExportTestData.ComplexName + "】", sheet.Cell(Row(sheet, nodes[3].Id), 15).GetString());
        Assert.Equal("B【=1+1】", sheet.Cell(Row(sheet, nodes[3].Id), 16).GetString());
        sheet.Cell(Row(sheet, nodes[2].Id), 12).Value = "B";
        book.RecalculateAllFormulas();
        Assert.Equal("A【=1+1】", sheet.Cell(Row(sheet, nodes[3].Id), 15).GetString());
        Assert.Equal("B【" + WorkspaceRecordExportTestData.ComplexName + "】", sheet.Cell(Row(sheet, nodes[3].Id), 16).GetString());
        Assert.All(sheet.CellsUsed().Where(c => c.HasFormula), c => Assert.NotEqual(XLDataType.Error, c.Value.Type));
    }

    [Fact]
    public void CompletedSourcesOutsideSelectionResolveAndRealResultMetadataIsReprinted()
    {
        var workspace = WorkspaceRecordExportTestData.Create(); var project = workspace.Projects[0]; var source = project.MatchGraph!.Matches[0];
        var key = new WorkspaceMatchKey(project.Id, source.Id);
        var result = new TournamentMatchResult(key, (EntrantSource.Participant)source.SideA, (EntrantSource.Participant)source.SideB, " 场外弃权备注 ", 0, DateTimeOffset.UtcNow)
        { Kind = TournamentResultKind.Walkover, ActualPlayedDay = new(2026, 9, 14) };
        workspace = workspace with { Stage = TournamentStage.InProgress, Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [key] = result } };
        var context = new WorkspaceScheduleExportContext(workspace);
        var target = new WorkspaceMatchKey(project.Id, project.MatchGraph.Matches[2].Id);
        var path = PathFor("resolved-external");
        new WorkspaceMatchRecordWriter().Write(path, context, [new(target, new(2026, 9, 14))]);
        using (var book = new XLWorkbook(path))
        {
            var sheet = book.Worksheet("对阵记录表");
            Assert.False(sheet.Cell("O6").HasFormula);
            Assert.Equal("A【王【小明】 \"双引号\" / 空 格】", sheet.Cell("O6").GetString());
            Assert.Equal("B【第2场负者】", sheet.Cell("P6").GetString());
        }
        new WorkspaceMatchRecordWriter().Write(path, context, [new(key, new(2026, 9, 13))]);
        var row = Assert.Single(new WorkspaceMatchRecordReader().ReadWorkspaceRecord(File.ReadAllBytes(path)).Rows);
        Assert.Equal("弃权", row.ResultKind.Text); Assert.Equal("0", row.Duration.Text);
        Assert.Equal("2026-09-14", row.ActualPlayedDay.Text); Assert.Equal(" 场外弃权备注 ", row.Score.Text);
        Assert.Equal("A【王【小明】 \"双引号\" / 空 格】", row.Winner.Text);

        workspace = workspace with { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [key] = result with { Kind = TournamentResultKind.Played, Score = "21-10", DurationMinutes = 18, ActualPlayedDay = null } } };
        new WorkspaceMatchRecordWriter().Write(path, new(workspace), [new(key, new(2026, 9, 13))]);
        row = Assert.Single(new WorkspaceMatchRecordReader().ReadWorkspaceRecord(File.ReadAllBytes(path)).Rows);
        Assert.Equal("正常", row.ResultKind.Text); Assert.Equal("18", row.Duration.Text);
        Assert.Equal(WorkspaceRecordCellKind.Empty, row.ActualPlayedDay.Kind);
    }

    [Fact]
    public void CoverageDayDoesNotPresentUnscheduledCarryoverAsANewPlacement()
    {
        var workspace = WorkspaceRecordExportTestData.Create(); var before = JsonSerializer.Serialize(workspace);
        var project = workspace.Projects[0]; var source = project.MatchGraph!.Matches[0];
        var key = new WorkspaceMatchKey(project.Id, source.Id); var path = PathFor("carryover");
        new WorkspaceMatchRecordWriter().Write(path, new(workspace), [new(key, new(2026, 9, 14))]);
        using (var book = new XLWorkbook(path))
        {
            var sheet = book.Worksheet("对阵记录表");
            Assert.Equal("2026-09-14", sheet.Cell("B6").GetString());
            Assert.Equal("待安排", sheet.Cell("C6").GetString()); Assert.Equal("待安排", sheet.Cell("K6").GetString());
            Assert.Contains("2026-09-13", sheet.Cell("M6").GetString());
            Assert.Contains("09:00:00.0001234", sheet.Cell("M6").GetString()); Assert.Contains("B1", sheet.Cell("M6").GetString());
            Assert.True(sheet.Cell("V6").IsEmpty());
        }
        Assert.Equal(before, JsonSerializer.Serialize(workspace));
        workspace = workspace with { Stage = TournamentStage.InProgress,
            Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [key] = new(key,
                (EntrantSource.Participant)source.SideA, (EntrantSource.Participant)source.SideB, "21-10", 18, DateTimeOffset.UtcNow)
                { ActualPlayedDay = new(2026, 9, 13) } } };
        new WorkspaceMatchRecordWriter().Write(path, new(workspace), [new(key, new(2026, 9, 14))]);
        using var completed = new XLWorkbook(path); var record = completed.Worksheet("对阵记录表");
        Assert.Contains("原计划 2026-09-13", record.Cell("C6").GetString());
        Assert.Equal("B1", record.Cell("K6").GetString()); Assert.Equal("2026-09-13", record.Cell("V6").GetString());
    }

    [Fact]
    public void EmptyDuplicateForeignOrUnknownDaySelectionsCannotPublishAnExampleOnlyOrAmbiguousWorkbook()
    {
        var context = new WorkspaceScheduleExportContext(WorkspaceRecordExportTestData.Create()); var key = context.MatchKeys[0];
        var path = PathFor("invalid"); var writer = new WorkspaceMatchRecordWriter();
        Assert.Equal("export.no-matches", Assert.Throws<WorkspaceValidationException>(() => writer.Write(path, context, [])).Code);
        Assert.Throws<WorkspaceValidationException>(() => writer.Write(path, context, [new(key, new(2026, 9, 13)), new(key, new(2026, 9, 14))]));
        Assert.Throws<WorkspaceValidationException>(() => writer.Write(path, context, [new(new(Guid.NewGuid(), key.MatchId), new(2026, 9, 13))]));
        Assert.Throws<WorkspaceValidationException>(() => writer.Write(path, context, [new(key, new(2030, 1, 1))]));
        Assert.False(File.Exists(path));
    }

    private static int Row(IXLWorksheet sheet, Guid id) => sheet.Column(14).CellsUsed().Single(c => c.GetString() == id.ToString("D")).Address.RowNumber;
    public void Dispose() => Directory.Delete(directory, true);
}
