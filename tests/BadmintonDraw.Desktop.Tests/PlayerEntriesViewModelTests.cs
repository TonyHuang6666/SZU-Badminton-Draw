using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Scheduling;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class PlayerEntriesViewModelTests
{
    [Fact]
    public void NegativeGapIsNotClaimedAsTheActualOverlapDuration()
    {
        var row = new PlayerEntryRowViewModel(new PlayerEntrySummary("student:1", "选手", "1", "选手（1）",
            ["男单", "男双"], [], [], 1, 0, -50));
        Assert.Equal("同日最短排定间隔：-50 分钟（存在重叠）", row.RestSummary);
    }

    [Fact]
    public void SingleProjectHasNoMultiEventPlayersOrFocusCommands()
    {
        using var fixture = new ScheduleUiFixture();
        fixture.Workflow.GenerateSchedule(new([new(new(2026, 10, 4), new(14, 0), new(18, 0), ["B1"])], 1, 20, 8),
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), fixture.Workflow.CurrentSession!.Workspace.Revision);
        using var model = new PlayerEntriesViewModel(fixture.Workflow.CurrentSession!, _ => throw new Exception("No player to focus"));
        Assert.True(model.HasNoPlayers); Assert.False(model.HasSelection);
        Assert.Empty(model.ConfirmedMatches); Assert.Empty(model.PotentialMatches);
    }

    [Fact]
    public void RefreshKeepsSelectedPlayerAndSortAndUsesNewPlacementForFocus()
    {
        using var fixture = CreateFixture();
        WorkspaceMatchKey? focused = null;
        using var model = new PlayerEntriesViewModel(fixture.Workflow.CurrentSession!, key => focused = key);
        model.SelectedSortIndex = 1;
        model.SelectedPlayer = model.Players.Last();
        var identity = model.SelectedPlayer.IdentityKey;
        var selected = model.ConfirmedMatches[0].Appearance;
        var initial = fixture.Workflow.CurrentSession!;
        fixture.Workflow.MoveMatch(new(selected.Key, "2026-10-05", new(15, 0), "B2", fixture.Workflow.CaptureScheduleEditBaseline()), initial.Workspace.Revision);
        model.RefreshSession(fixture.Workflow.CurrentSession!);
        Assert.Equal(identity, model.SelectedPlayer!.IdentityKey);
        Assert.Equal(1, model.SelectedSortIndex);
        var refreshed = model.ConfirmedMatches.Single(m => m.Appearance.Key == selected.Key);
        Assert.Contains("2026-10-05 15:00", refreshed.Appearance.Position);
        var saved = fixture.Workflow.CurrentSession;
        refreshed.FocusCommand.Execute(null);
        Assert.Equal(selected.Key, focused);
        Assert.Same(saved, fixture.Workflow.CurrentSession);
    }

    [Fact]
    public void StaleOrDisposedViewsDisableRetainedFocusCommands()
    {
        using var fixture = CreateFixture();
        WorkspaceMatchKey? focused = null;
        var model = new PlayerEntriesViewModel(fixture.Workflow.CurrentSession!, key => focused = key);
        var oldCommand = model.ConfirmedMatches[0].FocusCommand;
        model.RefreshSession(fixture.Workflow.CurrentSession! with { RequiresReload = true });
        Assert.True(model.IsStale);
        Assert.False(oldCommand.CanExecute(null)); oldCommand.Execute(null);
        Assert.Null(focused);
        model.RefreshSession(fixture.Workflow.CurrentSession!);
        Assert.True(model.ConfirmedMatches[0].FocusCommand.CanExecute(null));
        var currentCommand = model.ConfirmedMatches[0].FocusCommand;
        model.Dispose();
        Assert.False(currentCommand.CanExecute(null)); currentCommand.Execute(null);
        Assert.Null(focused);
    }

    [Fact]
    public void DifferentWorkspaceCannotReuseOldPlayerOrMatchSelection()
    {
        using var first = CreateFixture(); using var second = CreateFixture();
        using var model = new PlayerEntriesViewModel(first.Workflow.CurrentSession!, _ => throw new Exception("Old workspace must not focus"));
        var old = model.ConfirmedMatches[0].FocusCommand;
        model.RefreshSession(second.Workflow.CurrentSession!);
        Assert.False(old.CanExecute(null));
        Assert.Empty(model.Players);
        Assert.Empty(model.ConfirmedMatches);
    }

    private static ScheduleUiFixture CreateFixture()
    {
        var fixture = new ScheduleUiFixture(3, deterministicPlayerIds: true);
        fixture.Workflow.GenerateSchedule(new([new(new(2026, 10, 4), new(14, 0), new(18, 0), ["B1", "B2"]),
            new(new(2026, 10, 5), new(14, 0), new(18, 0), ["B1", "B2"])], 2, 20, 8),
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), fixture.Workflow.CurrentSession!.Workspace.Revision);
        return fixture;
    }
}
