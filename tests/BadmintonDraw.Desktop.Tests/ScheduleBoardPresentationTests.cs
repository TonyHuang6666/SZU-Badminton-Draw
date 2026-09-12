using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Scheduling;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class ScheduleBoardPresentationTests
{
    [Fact]
    public void AllProjectCardsUseCompositeIdentityAndExactPlacedTimes()
    {
        using var fixture = new ScheduleUiFixture(3);
        fixture.Workflow.GenerateSchedule(new([new(new(2026, 10, 2), new(9, 0, 30), new(18, 0), ["B1", "B2"]),
            new(new(2026, 9, 30), new(9, 0, 30), new(18, 0), ["B1", "B2"])], 2, 30, 4), new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), fixture.Workflow.CurrentSession!.Workspace.Revision);
        var snapshot = WorkspaceScheduleBoard.Build(fixture.Workflow.CurrentSession!);
        Assert.Equal(3, snapshot.Cards.Count); Assert.Equal(3, snapshot.Cards.Select(c => c.Key.ProjectId).Distinct().Count());
        Assert.Equal("2026-09-30", snapshot.Days[0].DayLabel);
        Assert.All(snapshot.Cards, c => { Assert.Equal(c.Key.MatchId, c.Placement.MatchId); Assert.Contains(c.Placement.StartTime, snapshot.Days.Single(d => d.DayLabel == c.Placement.DayLabel).TimeSlots); });
        Assert.Contains(snapshot.Cards, c => c.Placement.StartTime == new TimeOnly(9, 0, 30));
    }
    [Fact]
    public void DragPayloadRejectsAnotherWorkspaceStaleRevisionAndUnknownMatch()
    {
        using var fixture = new ScheduleUiFixture();
        fixture.Workflow.GenerateSchedule(new([new(new(2026, 9, 13), new(9, 0), new(18, 0), ["B1"])], 1, 30, 4), new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), fixture.Workflow.CurrentSession!.Workspace.Revision);
        var board = WorkspaceScheduleBoard.Build(fixture.Workflow.CurrentSession!); var key = board.Cards.Single().Key;
        var payload = WorkspaceBoardDrag.Encode(board, key);
        Assert.True(WorkspaceBoardDrag.TryParse(payload, board, out var parsed)); Assert.Equal(key, parsed);
        Assert.False(WorkspaceBoardDrag.TryParse(payload.Replace(board.WorkspaceId.ToString("D"), Guid.NewGuid().ToString("D")), board, out _));
        Assert.False(WorkspaceBoardDrag.TryParse(payload.Replace(key.MatchId.ToString("D"), Guid.NewGuid().ToString("D")), board, out _));
        Assert.False(WorkspaceBoardDrag.TryParse("schedule:首轮1", board, out _));
        fixture.Workflow.ExportRosterTemplate(key.ProjectId, Path.Combine(fixture.DirectoryPath, "template.xlsx"), fixture.Workflow.CurrentSession!.Workspace.Revision);
        Assert.False(WorkspaceBoardDrag.TryParse(payload, WorkspaceScheduleBoard.Build(fixture.Workflow.CurrentSession!), out _));
    }
    [Theory]
    [InlineData(0, 500, -26)]
    [InlineData(250, 500, 0)]
    [InlineData(499, 500, 26)]
    public void EdgeScrollRequestsOnlyDirectionNearViewportEdge(double point, double length, double expected)
        => Assert.Equal(expected, WorkspaceBoardInteraction.AutoScrollDelta(point, length));
}
