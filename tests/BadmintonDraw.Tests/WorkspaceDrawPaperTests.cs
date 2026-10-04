using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceDrawPaperTests
{
    [Theory]
    [InlineData(CompetitionMode.SinglesKnockout, false)]
    [InlineData(CompetitionMode.SinglesRoundRobin, false)]
    [InlineData(CompetitionMode.SinglesKnockout, true)]
    [InlineData(CompetitionMode.SinglesRoundRobin, true)]
    public void WorkspaceDrawSheetsDeclareA4InsteadOfDependingOnHostDefaultPaper(CompetitionMode mode, bool timed)
    {
        using var fixture = new WorkspaceTimedDrawTestFixture(mode, 5);
        var path = fixture.PathFor("paper");
        var writer = new DrawResultExcelWriter();
        if (timed) writer.WriteTimed(path, new(fixture.Workspace), fixture.Project.Id, fixture.ExportContext);
        else writer.Write(path, fixture.Draw, fixture.Project.Roster!.Participants, context: fixture.ExportContext);
        using var workbook = new XLWorkbook(path);
        Assert.All(workbook.Worksheets, sheet => Assert.Equal(XLPaperSize.A4Paper, sheet.PageSetup.PaperSize));
        var draw = workbook.Worksheet("对阵表");
        Assert.Equal(XLPageOrientation.Landscape, draw.PageSetup.PageOrientation);
        Assert.Equal(1, draw.PageSetup.PagesWide);
        Assert.Equal(0, draw.PageSetup.PagesTall);
        Assert.NotEmpty(draw.PageSetup.PrintAreas);
    }
}
