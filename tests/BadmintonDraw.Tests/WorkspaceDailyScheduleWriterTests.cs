using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceDailyScheduleWriterTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("workspace-daily-").FullName;
    private string PathFor(string name) => Path.Combine(directory, name + ".xlsx");

    [Fact]
    public void GlobalProjectionKeepsSameNamedProjectsQualifiedAndPreservesLiteralNamesAndPreciseTimes()
    {
        var workspace = WorkspaceRecordExportTestData.Create();
        workspace = workspace with { Projects = workspace.Projects.Select(p => p with { DisplayName = "相同项目" }).ToArray() };
        var before = JsonSerializer.Serialize(workspace); var context = new WorkspaceScheduleExportContext(workspace);
        var rows = context.MatchKeys.Where(k => workspace.Schedule!.Placements[k.MatchId].DayLabel == "2026-09-13")
            .Reverse().Select(k => new WorkspaceRecordExportRow(k, new(2026, 9, 13))).ToArray();
        var path = PathFor("global"); new ScheduleExcelWriter().WriteDailySchedule(path, context, rows);
        using var book = new XLWorkbook(path); var detail = book.Worksheet("赛程明细");
        Assert.Equal(new[] { "赛程明细", "时间场地网格", "赛程参数" }, book.Worksheets.Select(s => s.Name));
        Assert.Equal(rows.Select(r => r.Key.MatchId).Order(), detail.Column(14).CellsUsed().Skip(1).Select(c => Guid.Parse(c.GetString())).Order());
        foreach (var row in rows)
        {
            var r = DetailRow(detail, row.Key);
            Assert.Equal(row.Key.ProjectId.ToString("D"), detail.Cell(r, 13).GetString());
            Assert.Equal(row.Key.ProjectId == workspace.Projects[0].Id ? "相同项目（男单）" : "相同项目（女单）", detail.Cell(r, 2).GetString());
            Assert.Equal("2026-09-13", detail.Cell(r, 3).GetString());
            Assert.Equal("待比赛", detail.Cell(r, 10).GetString());
            Assert.True(detail.Cell(r, 11).IsEmpty());
        }
        var first = workspace.Projects[0].MatchGraph!.Matches[0];
        Assert.Contains("09:00:00.0001234", detail.Cell(DetailRow(detail, new(first.ProjectId, first.Id)), 4).GetString());
        Assert.Contains("=1+1", Text(detail)); Assert.Contains(WorkspaceRecordExportTestData.ComplexName, Text(detail));
        Assert.All(book.Worksheets.SelectMany(s => s.CellsUsed()), c => Assert.False(c.HasFormula));
        Assert.Contains(workspace.Id.ToString("D"), Text(book.Worksheet("赛程参数")));
        Assert.Equal(before, JsonSerializer.Serialize(workspace));
    }

    [Fact]
    public void DifferentDurationsAndConfiguredCourtOrderDoNotOverwriteGridAndDependenciesUseGlobalSources()
    {
        var workspace = WorkspaceRecordExportTestData.Create(); var placement = workspace.Schedule!.Placements.ToDictionary();
        var a = workspace.Projects[0].MatchGraph!.Matches[2]; var b = workspace.Projects[1].MatchGraph!.Matches[2];
        placement[a.Id] = placement[a.Id] with { StartTime = new(13, 0, 12), EndTime = new(13, 25, 12) };
        placement[b.Id] = placement[b.Id] with { StartTime = new(13, 0, 12), EndTime = new(13, 35, 12) };
        var resources = workspace.Resources! with { Days = workspace.Resources!.Days.Select(d => d with { Courts = new[] { "B2", "B1" } }).ToArray() };
        workspace = workspace with { Resources = resources, Schedule = workspace.Schedule with { Placements = placement, Resources = resources } };
        var path = PathFor("durations"); new ScheduleExcelWriter().WriteDailySchedule(path, new(workspace),
            [new(new(a.ProjectId, a.Id), new(2026, 9, 14)), new(new(b.ProjectId, b.Id), new(2026, 9, 14))]);
        using var book = new XLWorkbook(path); var grid = book.Worksheet("时间场地网格");
        Assert.Equal("B2", grid.Cell("B4").GetString()); Assert.Equal("B1", grid.Cell("C4").GetString());
        var shortRow = grid.Column(1).CellsUsed().Single(c => c.GetString() == "13:00:12-13:25:12").Address.RowNumber;
        var longRow = grid.Column(1).CellsUsed().Single(c => c.GetString() == "13:00:12-13:35:12").Address.RowNumber;
        Assert.NotEqual(shortRow, longRow); Assert.Contains(workspace.Projects[0].DisplayName, grid.Cell(shortRow, 3).GetString());
        Assert.Contains(workspace.Projects[1].DisplayName, grid.Cell(longRow, 2).GetString());
        Assert.Contains("2026-09-13", grid.Cell(shortRow, 3).GetString());
        Assert.Contains("09:00:00.0001234", grid.Cell(shortRow, 3).GetString());
        Assert.Contains("胜者", grid.Cell(shortRow, 3).GetString()); Assert.Contains("负者", grid.Cell(shortRow, 3).GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CarryoversAndCompletedOffDayRecordsStayOutOfGridAndDoNotInventActualDates(bool actualDayKnown)
    {
        var workspace = WorkspaceRecordExportTestData.Create(); var first = workspace.Projects[0].MatchGraph!.Matches[0];
        var second = workspace.Projects[1].MatchGraph!.Matches[0]; var key = new WorkspaceMatchKey(first.ProjectId, first.Id);
        workspace = workspace with { Stage = TournamentStage.InProgress,
            Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [key] = new(key,
                (EntrantSource.Participant)first.SideA, (EntrantSource.Participant)first.SideB, "21-10", 18, workspace.UpdatedAt)
                { ActualPlayedDay = actualDayKnown ? new(2026, 9, 13) : null } } };
        var path = PathFor("carry"); new ScheduleExcelWriter().WriteDailySchedule(path, new(workspace),
            [new(key, new(2026, 9, 14)), new(new(second.ProjectId, second.Id), new(2026, 9, 14))]);
        using var book = new XLWorkbook(path); var detail = book.Worksheet("赛程明细");
        var completed = DetailRow(detail, key); var pending = DetailRow(detail, new(second.ProjectId, second.Id));
        Assert.Contains("原计划 2026-09-13", detail.Cell(completed, 4).GetString());
        Assert.Equal("已录入", detail.Cell(completed, 10).GetString());
        Assert.Equal(actualDayKnown ? "2026-09-13" : "未知", detail.Cell(completed, 11).GetString());
        Assert.Equal("待安排", detail.Cell(pending, 4).GetString()); Assert.Equal("待安排", detail.Cell(pending, 5).GetString());
        Assert.Contains("2026-09-13", detail.Cell(pending, 12).GetString()); Assert.True(detail.Cell(pending, 11).IsEmpty());
        var grid = Text(book.Worksheet("时间场地网格"));
        Assert.Contains("无本日已安排场次", grid); Assert.DoesNotContain("第1场", grid);
        Assert.Contains("不代表补打已排入本日场地", grid);
    }

    [Fact]
    public void RecordedTypedWinnerAndLoserOutsideSelectionResolveWithoutParsingDisplayNames()
    {
        var workspace = WorkspaceRecordExportTestData.Create(); var project = workspace.Projects[0];
        var first = project.MatchGraph!.Matches[0]; var second = project.MatchGraph.Matches[1];
        var firstKey = new WorkspaceMatchKey(project.Id, first.Id); var secondKey = new WorkspaceMatchKey(project.Id, second.Id);
        workspace = workspace with { Stage = TournamentStage.InProgress,
            Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>
            {
                [firstKey] = new(firstKey, (EntrantSource.Participant)first.SideB, (EntrantSource.Participant)first.SideA, "21-10", 18, workspace.UpdatedAt)
                    { ActualPlayedDay = new(2026, 9, 13) },
                [secondKey] = new(secondKey, (EntrantSource.Participant)second.SideB, (EntrantSource.Participant)second.SideA, "21-10", 18, workspace.UpdatedAt)
            } };
        var key = new WorkspaceMatchKey(project.Id, project.MatchGraph.Matches[2].Id);
        var path = PathFor("resolved"); new ScheduleExcelWriter().WriteDailySchedule(path, new(workspace), [new(key, new(2026, 9, 14))]);
        using var book = new XLWorkbook(path); var detail = book.Worksheet("赛程明细"); var row = DetailRow(detail, key);
        Assert.Equal("某某胜者", detail.Cell(row, 8).GetString()); Assert.Equal("=1+1", detail.Cell(row, 9).GetString());
        Assert.Contains("第1场胜者", detail.Cell(row, 12).GetString()); Assert.Contains("第2场负者", detail.Cell(row, 12).GetString());
        Assert.Contains("某某胜者 vs =1+1", Text(book.Worksheet("时间场地网格")));
        Assert.All(book.Worksheets.SelectMany(s => s.CellsUsed()), c => Assert.False(c.HasFormula));
    }

    [Fact]
    public void GlobalParametersAreActualResourcesAndProjectTimingsNotSyntheticSingleProjectSettings()
    {
        var workspace = WorkspaceRecordExportTestData.Create(); var a = workspace.Projects[0]; var b = workspace.Projects[1];
        var resources = workspace.Resources! with { RefereeCount = null,
            Days = workspace.Resources!.Days.Select(d => d with { RefereeCapacityWindows = [new(new(10, 0, 8), new(11, 0, 8), 1)],
                UnavailableCourtWindows = [new(new(15, 0), new(16, 0), ["B1"])] }).ToArray() };
        var policy = new TournamentSchedulingPolicy(ScheduleAutoSchedulingStrategy.Custom,
            [new("2026-09-13", .65, .85)], true, [new("2026-09-14", .75)],
            [new(a.Id, TournamentFinalDayMatchCategory.Final, TournamentFinalDayPreference.PreferFinalDay)])
        { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [a.Id] = new(35, 8, 25) } };
        workspace = workspace with { Resources = resources, Schedule = workspace.Schedule! with { Resources = resources, Policy = policy } };
        var path = PathFor("parameters"); var key = new WorkspaceMatchKey(a.Id, a.MatchGraph!.Matches[0].Id);
        new ScheduleExcelWriter().WriteDailySchedule(path, new(workspace), [new(key, new(2026, 9, 13))]);
        using var book = new XLWorkbook(path); var text = Text(book.Worksheet("赛程参数"));
        Assert.Contains("按可用场地数", text); Assert.Contains("10:00:08-11:00:08", text); Assert.Contains("15:00-16:00", text);
        Assert.Contains("30 分钟", text); Assert.Contains("6 场", text); Assert.Contains("自定义", text);
        Assert.Contains("35 分钟", text); Assert.Contains("25 分钟", text); Assert.Contains("8 强", text);
        Assert.Contains("65%", text); Assert.Contains("85%", text); Assert.Contains("75%", text); Assert.Contains("偏好决赛日", text);
        Assert.Contains("未设置覆盖", text); Assert.Contains(b.Id.ToString("D"), text);
        Assert.Contains("2026-09-14", text); // All global dates, even for a selected one-day/one-project output.
        Assert.Contains(a.MatchGraph!.Revision, text); Assert.Contains(a.Draw!.ConfirmedAt!.Value.ToString("O"), text);
        foreach (var sheet in book.Worksheets)
        {
            Assert.Single(sheet.PageSetup.PrintAreas); Assert.Equal(XLPaperSize.A4Paper, sheet.PageSetup.PaperSize);
            Assert.Equal(1, sheet.PageSetup.PagesWide); Assert.Equal(0, sheet.PageSetup.PagesTall);
            Assert.True(sheet.Row(4).Height > 0);
        }
    }

    [Fact]
    public void SixteenCourtsPrintInFourLegibleGroupsWithoutLosingOrDuplicatingMatches()
    {
        var workspace = WorkspaceRecordExportTestData.Create();
        var courts = new[] { "B1", "B2" }.Concat(Enumerable.Range(1, 14).Select(i => "C" + i)).ToArray();
        var resources = workspace.Resources! with { Days = workspace.Resources!.Days.Select(d => d with { Courts = courts }).ToArray() };
        var placements = workspace.Schedule!.Placements.ToDictionary();
        var selected = workspace.Projects.SelectMany(p => p.MatchGraph!.Matches.Take(2))
            .Select(n => new WorkspaceMatchKey(n.ProjectId, n.Id)).ToArray();
        for (var i = 0; i < selected.Length; i++)
            placements[selected[i].MatchId] = placements[selected[i].MatchId] with { Court = courts[i * 4] };
        workspace = workspace with { Resources = resources, Schedule = workspace.Schedule with { Resources = resources, Placements = placements } };
        var path = PathFor("sixteen-courts");
        new ScheduleExcelWriter().WriteDailySchedule(path, new(workspace), selected.Select(k => new WorkspaceRecordExportRow(k, new(2026, 9, 13))).ToArray());
        using var book = new XLWorkbook(path);
        var grids = book.Worksheets.Where(s => s.Name.StartsWith("时间场地网格", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, grids.Length);
        Assert.Equal(courts, grids.SelectMany(s => s.Row(4).CellsUsed().Skip(1).Select(c => c.GetString())));
        for (var i = 0; i < grids.Length; i++)
        {
            Assert.Equal(5, grids[i].LastColumnUsed()!.ColumnNumber());
            Assert.Contains($"第 {i + 1}/4 组场地", grids[i].Cell("A1").GetString());
            Assert.Single(grids[i].PageSetup.PrintAreas);
            Assert.Equal(1, grids[i].PageSetup.PagesWide); Assert.Equal(0, grids[i].PageSetup.PagesTall);
            Assert.Equal(XLPaperSize.A4Paper, grids[i].PageSetup.PaperSize);
            var cards = grids[i].CellsUsed().Where(c => c.Address.RowNumber >= 5 && c.Address.ColumnNumber >= 2 && !c.IsEmpty()).ToArray();
            var card = Assert.Single(cards);
            var expected = workspace.Projects.SelectMany(p => p.MatchGraph!.Matches).Single(n => n.Id == selected[i].MatchId);
            Assert.Contains(expected.DisplayName, card.GetString());
            Assert.Equal(courts[i * 4], grids[i].Cell(4, card.Address.ColumnNumber).GetString());
        }
        // A section with no selected placements must describe only its own courts, not the whole day.
        path = PathFor("empty-court-groups");
        new ScheduleExcelWriter().WriteDailySchedule(path, new(workspace), [new(selected[0], new(2026, 9, 13))]);
        using var sparse = new XLWorkbook(path);
        Assert.Contains("本组场地无已安排比赛", Text(sparse.Worksheet("时间场地网格 2")));
        Assert.DoesNotContain("无本日已安排场次", Text(sparse.Worksheet("时间场地网格 2")));
    }

    [Fact]
    public void InvalidSelectionsDoNotWriteFilesAndRepeatedGridCellNeverSilentlyLosesAMatch()
    {
        var workspace = WorkspaceRecordExportTestData.Create(); var context = new WorkspaceScheduleExportContext(workspace);
        var writer = new ScheduleExcelWriter(); var key = context.MatchKeys[0]; var second = context.MatchKeys[1]; var path = PathFor("invalid");
        Assert.Equal("export.no-matches", Assert.Throws<WorkspaceValidationException>(() => writer.WriteDailySchedule(path, context, [])).Code);
        Assert.Throws<WorkspaceValidationException>(() => writer.WriteDailySchedule(path, context, [new(key, new(2026, 9, 13)), new(second, new(2026, 9, 14))]));
        Assert.Throws<WorkspaceValidationException>(() => writer.WriteDailySchedule(path, context, [new(key, new(2026, 9, 13)), new(key, new(2026, 9, 13))]));
        Assert.Throws<WorkspaceValidationException>(() => writer.WriteDailySchedule(path, context, [new(new(Guid.NewGuid(), key.MatchId), new(2026, 9, 13))]));
        Assert.Throws<WorkspaceValidationException>(() => writer.WriteDailySchedule(path, context, [new(key, new(2030, 1, 1))]));
        var places = workspace.Schedule!.Placements.ToDictionary(); places[second.MatchId] = places[key.MatchId] with { MatchId = second.MatchId };
        workspace = workspace with { Schedule = workspace.Schedule with { Placements = places } };
        Assert.Throws<WorkspaceValidationException>(() => writer.WriteDailySchedule(path, new(workspace),
            [new(key, new(2026, 9, 13)), new(second, new(2026, 9, 13))]));
        Assert.False(File.Exists(path));
    }

    private static string Text(IXLWorksheet sheet) => string.Join("\n", sheet.CellsUsed().Select(c => c.GetString()));
    private static int DetailRow(IXLWorksheet sheet, WorkspaceMatchKey key) => sheet.Column(14).CellsUsed()
        .Single(c => c.GetString() == key.MatchId.ToString("D")).Address.RowNumber;
    public void Dispose() => Directory.Delete(directory, true);
}
