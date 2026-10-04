using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceRecordInputContractsTests
{
    [Fact]
    public void DocumentSnapshotsCallerRowsWithoutCollapsingRepeatedMatchEvidence()
    {
        var first = Row(6, " A ");
        var second = Row(7, " B ");
        var rows = new List<WorkspaceRecordRawRow> { first, second };
        var file = new WorkspaceRecordImportFile("记录.xlsx", "/synthetic/记录.xlsx", new(new string('a', 64), rows));

        rows[0] = first with { Winner = new("changed") };
        rows.Clear();

        Assert.Equal(new[] { 6, 7 }, file.Document.Rows.Select(row => row.Location.RowNumber));
        Assert.Equal(new[] { " A ", " B " }, file.Document.Rows.Select(row => row.Winner.Text));
        Assert.Throws<NotSupportedException>(() => ((IList<WorkspaceRecordRawRow>)file.Document.Rows)[0] = second);
    }

    [Fact]
    public void WithReplacementRowsTakesANewSnapshotAndLeavesOriginalEvidenceIntact()
    {
        var original = new WorkspaceRecordDocument(new string('a', 64), [Row(6, " A ")]);
        var replacement = new List<WorkspaceRecordRawRow> { Row(9, " B ") };
        var changed = original with { Rows = replacement };

        replacement.Clear();

        Assert.Equal(6, Assert.Single(original.Rows).Location.RowNumber);
        Assert.Equal(9, Assert.Single(changed.Rows).Location.RowNumber);
        Assert.Throws<NotSupportedException>(() => ((IList<WorkspaceRecordRawRow>)changed.Rows).Clear());
    }

    [Theory]
    [InlineData(ScheduleMatchSide.SideA, "  王【小明】 / 李\"四\"\t队  ", "A【  王【小明】 / 李\"四\"\t队  】")]
    [InlineData(ScheduleMatchSide.SideB, "陈 某胜者\r\n赵 六", "B【陈 某胜者\r\n赵 六】")]
    public void FormatAddsSideWithoutParsingOrChangingTheCompleteLabel(ScheduleMatchSide side, string label, string expected)
        => Assert.Equal(expected, WorkspaceWinnerOptionText.Format(side, label));

    [Theory]
    [InlineData(" \tA【王\u3000 小明 /\r\n李\u00a0四】\n", "A【王 小明 / 李 四】")]
    [InlineData("B【张【三】/\"李四\"胜者】", "B【张【三】/\"李四\"胜者】")]
    [InlineData("\t\r\n\u3000\u00a0", "")]
    public void NormalizeChangesOnlyWhitespace(string input, string expected)
        => Assert.Equal(expected, WorkspaceWinnerOptionText.Normalize(input));

    [Fact]
    public void UndefinedSideCannotSilentlyBecomeAnAOrBOption()
        => Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceWinnerOptionText.Format((ScheduleMatchSide)99, "张三"));

    private static WorkspaceRecordRawRow Row(int number, string winner)
    {
        var empty = new WorkspaceRecordCell("", WorkspaceRecordCellKind.Empty);
        return new(new("对阵记录表", number), new("workspace"), new("project"), new("same-match"), new("graph"),
            new("2026-09-13T10:00:00.0000000+08:00"), new("2026-09-13"), empty, empty, new(winner),
            new(" 21–10 "), empty, empty, empty, empty, empty);
    }
}
