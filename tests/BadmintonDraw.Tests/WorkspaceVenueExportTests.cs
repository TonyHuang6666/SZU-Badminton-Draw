using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceVenueExportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "szbd-venue-export-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DailyScheduleExplainsOnlyConfiguredBuiltInVenuesAndPreservesCustomCourtNames()
    {
        // Omitting the venue legend or inferring a built-in venue from a custom name breaks this export contract.
        using var book = Export(["粤海东馆 · A1", "自定义馆 · 1号场", "丽湖至快 · 99号场"]);
        var settings = SettingsText(book);
        Assert.Contains("粤海东馆", settings);
        Assert.Equal("粤海校区·运动广场东馆羽毛球场", VenueExplanation(book, "粤海东馆"));
        Assert.Contains("自定义馆 · 1号场", settings);
        Assert.Contains("丽湖至快 · 99号场", settings);
        Assert.DoesNotContain("丽湖校区·至快体育馆", settings);
        Assert.DoesNotContain("丽湖校区·至畅体育馆", settings);
        Assert.Equal(new[] { "粤海东馆 · A1", "自定义馆 · 1号场", "丽湖至快 · 99号场" }, GridHeaders(book));
        Assert.Contains("自定义馆 · 1号场", DetailCourts(book));
    }

    [Fact]
    public void DailyScheduleKeepsAllThirtyTwoEastHallCourtsInEightNaturallyOrderedGridSheets()
    {
        // Dropping unused courts, sorting lexically, or shortening qualified labels loses printed resources.
        string[] courts = ["粤海东馆 · A1", "粤海东馆 · A2", "粤海东馆 · A3", "粤海东馆 · A4",
            "粤海东馆 · A5", "粤海东馆 · A6", "粤海东馆 · A7", "粤海东馆 · A8",
            "粤海东馆 · B1", "粤海东馆 · B2", "粤海东馆 · B3", "粤海东馆 · B4",
            "粤海东馆 · B5", "粤海东馆 · B6", "粤海东馆 · B7", "粤海东馆 · B8",
            "粤海东馆 · C1", "粤海东馆 · C2", "粤海东馆 · C3", "粤海东馆 · C4",
            "粤海东馆 · C5", "粤海东馆 · C6", "粤海东馆 · C7", "粤海东馆 · C8",
            "粤海东馆 · D1", "粤海东馆 · D2", "粤海东馆 · D3", "粤海东馆 · D4",
            "粤海东馆 · D5", "粤海东馆 · D6", "粤海东馆 · D7", "粤海东馆 · D8"];
        using var book = Export(courts);
        var grids = GridSheets(book).ToArray();
        Assert.Equal(8, grids.Length);
        Assert.All(grids, sheet => Assert.Equal(5, sheet.LastColumnUsed()!.ColumnNumber()));
        Assert.Equal(courts, GridHeaders(book));
        Assert.Contains("粤海东馆 · A1", DetailCourts(book));
        Assert.Equal("粤海校区·运动广场东馆羽毛球场", VenueExplanation(book, "粤海东馆"));
    }

    [Fact]
    public void DailyScheduleDistinguishesTheTwoLihuNumberOneCourtsInDetailGridAndLegend()
    {
        // Removing the venue qualifier makes two independent resources indistinguishable to the reader.
        using var book = Export(["丽湖至快 · 1号场", "丽湖至畅 · 1号场"]);
        Assert.Equal(new[] { "丽湖至快 · 1号场", "丽湖至畅 · 1号场" }, GridHeaders(book));
        var courts = DetailCourts(book);
        Assert.Contains("丽湖至快 · 1号场", courts);
        Assert.Contains("丽湖至畅 · 1号场", courts);
        var grid = book.Worksheet("时间场地网格");
        Assert.True(grid.Cell(4, 2).Style.Alignment.WrapText);
        Assert.True(grid.Row(4).Height > 30, "Qualified venue names need a wrapped header tall enough to remain visible.");
        Assert.False(grid.Cell(5, 2).IsEmpty());
        Assert.False(grid.Cell(5, 3).IsEmpty());
        Assert.Equal("丽湖校区·至快体育馆", VenueExplanation(book, "丽湖至快"));
        Assert.Equal("丽湖校区·至畅体育馆", VenueExplanation(book, "丽湖至畅"));
        Assert.DoesNotContain("粤海校区·运动广场东馆羽毛球场", SettingsText(book));
    }

    private XLWorkbook Export(string[] courts)
    {
        var workspace = WorkspaceMaterialTestFixture.Create();
        var resources = new TournamentResourcePlan([new(WorkspaceMaterialTestFixture.FirstDay,
            new(9, 0), new(18, 0), courts)], null, 15, 6);
        var generated = new TournamentScheduler().Generate(new TournamentSchedulingRequest(
            workspace.Projects.Select(p => p.MatchGraph!).ToArray(), resources,
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])));
        var schedule = Assert.IsType<TournamentSchedulingResult.Success>(generated).Schedule;
        workspace = workspace with { Resources = resources, Schedule = schedule };
        var context = new WorkspaceScheduleExportContext(workspace);
        var rows = context.MatchKeys.Select(k => new WorkspaceRecordExportRow(k, WorkspaceMaterialTestFixture.FirstDay)).ToArray();
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".xlsx");
        new ScheduleExcelWriter().WriteDailySchedule(path, context, rows);
        return new XLWorkbook(path);
    }

    private static IEnumerable<IXLWorksheet> GridSheets(XLWorkbook book) =>
        book.Worksheets.Where(s => s.Name.StartsWith("时间场地网格", StringComparison.Ordinal));

    private static string[] GridHeaders(XLWorkbook book) => GridSheets(book)
        .SelectMany(s => s.Row(4).CellsUsed().Skip(1).Select(c => c.GetString())).ToArray();

    private static string[] DetailCourts(XLWorkbook book) => book.Worksheet("赛程明细")
        .RowsUsed().Where(r => r.RowNumber() >= 5).Select(r => r.Cell(5).GetString()).ToArray();

    private static string SettingsText(XLWorkbook book) => string.Join("\n", book.Worksheet("赛程参数")
        .CellsUsed().Select(c => c.GetString()));

    private static string VenueExplanation(XLWorkbook book, string shortName) => Assert.Single(
        book.Worksheet("赛程参数").RowsUsed(), r => r.Cell(1).GetString() == "场馆说明 " + shortName).Cell(2).GetString();

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
