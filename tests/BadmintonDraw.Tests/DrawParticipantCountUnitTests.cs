using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class DrawParticipantCountUnitTests
{
    [Theory]
    [InlineData(EventKind.Singles, "人")]
    [InlineData(EventKind.Doubles, "对")]
    [InlineData(EventKind.Team, "队")]
    public void PublicKnockoutGroupBandsUseTheSameEntrantUnitAsTheTitle(EventKind kind, string unit)
    {
        using var fixture = new WorkspaceTimedDrawTestFixture(CompetitionMode.SinglesKnockout, 9);
        var participants = Enumerable.Range(1, 9).Select(i => new DrawParticipant(
            kind == EventKind.Doubles ? $"甲{i}/乙{i}" : $"参赛{i}",
            PrimaryName: $"甲{i}", PartnerName: kind == EventKind.Doubles ? $"乙{i}" : null,
            TeamName: kind == EventKind.Team ? $"队伍{i}" : null,
            PrimaryStudentId: kind == EventKind.Team ? null : $"a-{i}",
            PartnerStudentId: kind == EventKind.Doubles ? $"b-{i}" : null)).ToArray();
        var mode = kind == EventKind.Team ? CompetitionMode.TeamKnockout : CompetitionMode.SinglesKnockout;
        var draw = new DrawService().Generate(participants, new(mode, kind, 1, "count-unit"));
        var path = fixture.PathFor("public-count-unit");
        new DrawResultExcelWriter().Write(path, draw, participants);
        AssertUnits(path, unit);
    }

    [Theory]
    [InlineData(CompetitionMode.SinglesKnockout, "人")]
    [InlineData(CompetitionMode.TeamKnockout, "队")]
    public void TimedWorkspaceKnockoutRetainsCorrectEntrantUnit(CompetitionMode mode, string unit)
    {
        using var fixture = new WorkspaceTimedDrawTestFixture(mode, 9);
        AssertUnits(fixture.Export(), unit);
    }

    private static void AssertUnits(string path, string unit)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("对阵表");
        Assert.Contains($"9{unit}对阵表", sheet.Cell(1, 1).GetString());
        Assert.Contains($"共9{unit}", sheet.Cell(2, 1).GetString());
        var bands = sheet.CellsUsed().Select(cell => cell.GetString())
            .Where(text => text.StartsWith("第1组：", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(bands);
        Assert.All(bands, text => Assert.Equal($"第1组：9{unit}，1场首轮赛，首轮赛后8{unit}进入正赛", text));
    }
}
