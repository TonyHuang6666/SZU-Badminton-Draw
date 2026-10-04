using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Scheduling;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class PlayerEntriesViewModelTests
{
    // Before a draw, opening entries must expose imported registrations without a schedule.
    [Fact]
    public void ImportedRosterCanOpenBeforeDrawAndSchedule()
    {
        using var fixture = new ScheduleUiFixture(3, deterministicPlayerIds: true, confirmDraws: false);
        using var model = new PlayerEntriesViewModel(fixture.Workflow.CurrentSession!, _ => throw new Exception("No schedule to focus"));

        Assert.Equal(2, model.Players.Count);
        Assert.Equal(3, model.SelectedPlayer!.Entry.ProjectCount);
        Assert.Empty(model.ConfirmedMatches);
        Assert.Empty(model.PotentialMatches);
    }

    // A failed reload must not replace last-known display data with a partial incoming snapshot.
    [Fact]
    public void RequiresReloadKeepsLastKnownPlayersAndPlacements()
    {
        using var fixture = CreateFixture();
        using var model = new PlayerEntriesViewModel(fixture.Workflow.CurrentSession!, _ => { });
        var originalPlayer = model.SelectedPlayer!.IdentityKey;
        var originalPosition = model.ConfirmedMatches[0].Appearance.Position;
        var session = fixture.Workflow.CurrentSession!;
        model.RefreshSession(session with { RequiresReload = true, Workspace = session.Workspace with { Projects = [], Schedule = null } });

        Assert.True(model.IsStale);
        Assert.Equal(originalPlayer, model.SelectedPlayer!.IdentityKey);
        Assert.Equal(originalPosition, model.ConfirmedMatches[0].Appearance.Position);
        Assert.False(model.ConfirmedMatches[0].FocusCommand.CanExecute(null));
    }

    // Roster mode must not claim zero schedule risk or show rest-based sorting.
    [Fact]
    public void RosterModeShowsRegistrationsAndSuppressesScheduleMetrics()
    {
        using var fixture = new ScheduleUiFixture(3, deterministicPlayerIds: true, confirmDraws: false);
        using var model = new PlayerEntriesViewModel(fixture.Workflow.CurrentSession!, _ => { });

        Assert.True(model.HasNoSchedule);
        Assert.False(model.CanSortByRest);
        model.SelectedSortIndex = 2;
        Assert.Equal(0, model.SelectedSortIndex);
        Assert.Single(model.SortOptions);
        Assert.Equal(3, model.Registrations.Count);
        Assert.DoesNotContain("场", model.SelectedPlayer!.ShortSummary);
        Assert.Empty(model.SelectedPlayer.RiskCounts);
        Assert.Empty(model.SelectedPlayer.RestSummary);
        Assert.Empty(model.SelectedSummary);
        Assert.Empty(model.RiskSummary);
        Assert.Empty(model.RestSummary);
        Assert.False(model.HasNoConfirmedMatches);
    }

    // Filtering must include names and student IDs, and no results must differ from no registrations.
    [Fact]
    public void SearchByNameOrStudentIdHasASeparateNoResultsState()
    {
        using var fixture = new ScheduleUiFixture(3, deterministicPlayerIds: true, confirmDraws: false);
        using var model = new PlayerEntriesViewModel(fixture.Workflow.CurrentSession!, _ => { });
        model.SearchText = "男子单打1";
        Assert.Equal("ui-player-1", Assert.Single(model.Players).Entry.StudentId);
        model.SearchText = "ui-player-2";
        Assert.Equal("ui-player-2", Assert.Single(model.Players).Entry.StudentId);
        model.SearchText = "不存在";
        Assert.Empty(model.Players);
        Assert.True(model.HasNoSearchResults);
        Assert.False(model.HasNoPlayers);
        Assert.False(model.HasSelection);
        model.SearchText = "";
        Assert.Equal(2, model.Players.Count);
        Assert.False(model.HasNoSearchResults);
    }

    // Schedule removal must restore the roster view and revoke a command retained by the old view.
    [Fact]
    public void ScheduleClearingRestoresRosterViewAndDisablesRetainedFocus()
    {
        using var fixture = CreateFixture();
        using var model = new PlayerEntriesViewModel(fixture.Workflow.CurrentSession!, _ => throw new Exception("Old schedule must not focus"));
        model.SelectedPlayer = model.Players.Last();
        model.SelectedSortIndex = 2;
        var identity = model.SelectedPlayer!.IdentityKey;
        var command = model.ConfirmedMatches[0].FocusCommand;
        var session = fixture.Workflow.CurrentSession!;
        model.RefreshSession(session with { Workspace = session.Workspace with { Schedule = null } });

        Assert.Equal(identity, model.SelectedPlayer!.IdentityKey);
        Assert.Equal(3, model.Registrations.Count);
        Assert.True(model.HasNoSchedule);
        Assert.Equal(0, model.SelectedSortIndex);
        Assert.Empty(model.ConfirmedMatches);
        Assert.False(command.CanExecute(null));
        command.Execute(null);
    }

    // A roster replacement must refresh partner data while maintaining the current identity.
    [Fact]
    public void RosterReplacementKeepsSelectedIdentityAndRefreshesRegistrationDetails()
    {
        using var fixture = new ScheduleUiFixture(3, deterministicPlayerIds: true, confirmDraws: false);
        using var model = new PlayerEntriesViewModel(fixture.Workflow.CurrentSession!, _ => { });
        model.SelectedPlayer = model.Players.Last();
        var identity = model.SelectedPlayer!.IdentityKey;
        var session = fixture.Workflow.CurrentSession!;
        model.RefreshSession(session with { Workspace = session.Workspace with
        {
            Projects = session.Workspace.Projects.Select(project => project.Discipline == EventDiscipline.MenDoubles ? project with
            { Roster = project.Roster! with { Participants = project.Roster.Participants.Select(r => r with { PartnerName = "新搭档" }).ToArray() } } : project).ToArray()
        } });

        Assert.Equal(identity, model.SelectedPlayer!.IdentityKey);
        Assert.Equal("新搭档", model.Registrations.Single(r => r.ProjectName == "男子双打").PartnerName);
    }

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
