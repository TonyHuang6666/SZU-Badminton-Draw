using System.Text;
using System.Text.RegularExpressions;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceMaterialRegressionTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("v5-material-regression-").FullName;

    [Fact]
    public void DailyGridPdfKeepsOneReadablePagePerCompetitionDayForSmallFixture()
    {
        var workspace = WorkspaceRecordExportTestData.Create();
        var context = new WorkspaceScheduleExportContext(workspace);
        var pages = 0;
        foreach (var day in workspace.Resources!.Days.OrderBy(item => item.Date))
        {
            var rows = context.MatchKeys
                .Where(key => workspace.Schedule!.Placements[key.MatchId].DayLabel == day.DayLabel)
                .Select(key => new WorkspaceRecordExportRow(key, day.Date)).ToArray();
            var workbook = Path.Combine(directory, $"{day.DayLabel}.xlsx");
            var pdf = Path.Combine(directory, $"{day.DayLabel}.pdf");
            new ScheduleExcelWriter().WriteDailySchedule(workbook, context, rows);
            new DrawResultVisualWriter().Write(pdf, workbook, "时间场地网格", DrawResultVisualFormat.A4Pdf);

            Assert.Equal("%PDF", Encoding.ASCII.GetString(File.ReadAllBytes(pdf), 0, 4));
            var source = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
            Assert.Contains("/ToUnicode", source);
            Assert.DoesNotContain("/Subtype /Image", source);
            Assert.InRange(new FileInfo(pdf).Length, 1, 80L * 1024L * 1024L);
            var count = Regex.Matches(source, @"/Type\s*/Page(?!s)").Count;
            Assert.Equal(1, count);
            pages += count;
        }
        Assert.Equal(2, pages);
    }

    [Fact]
    public void TimedBracketUsesActualGraphPlacementsAcrossDaysAndKeepsWinnerRowReadable()
    {
        using var fixture = new WorkspaceTimedDrawTestFixture(CompetitionMode.SinglesKnockout, 8,
            goal: KnockoutGoal.Champion);
        var workspace = fixture.Workspace;
        var final = workspace.Projects[0].MatchGraph!.Matches.Single(node => node.IsChampionshipFinal);
        var secondDay = workspace.Resources!.Days[0] with { Date = workspace.Resources.Days[0].Date.AddDays(1) };
        var resources = workspace.Resources with { Days = [workspace.Resources.Days[0], secondDay] };
        var placements = workspace.Schedule!.Placements.ToDictionary();
        placements[final.Id] = placements[final.Id] with
        {
            DayLabel = secondDay.DayLabel,
            StartTime = new TimeOnly(14, 0),
            EndTime = new TimeOnly(14, 30)
        };
        workspace = workspace with
        {
            Resources = resources,
            Schedule = workspace.Schedule with { Resources = resources, Placements = placements }
        };
        TournamentWorkspaceRules.Validate(workspace);
        var output = Path.Combine(directory, "timed.xlsx");
        new DrawResultExcelWriter().WriteTimed(output, new(workspace), fixture.Project.Id,
            WorkspaceTimedDrawTestFixture.Source(workspace, workspace.Projects[0]));

        using var workbook = new XLWorkbook(output);
        var sheet = workbook.Worksheet("对阵表");
        var texts = sheet.CellsUsed().Select(cell => cell.GetString()).ToArray();
        Assert.Contains(texts, text => text.Contains("2026-09-14", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("2026-09-15 14:00-14:30", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("B1", StringComparison.Ordinal));
        var winnerRow = sheet.CellsUsed().First(cell => cell.GetString().StartsWith("胜者", StringComparison.Ordinal)
            && cell.GetString().Contains("2026-09-14", StringComparison.Ordinal)).WorksheetRow();
        Assert.True(winnerRow.Height >= 40);
    }

    public void Dispose() => Directory.Delete(directory, true);
}
