using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class PlayerEntriesWindowTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(WorkspaceRoute.Rosters)]
    [InlineData(WorkspaceRoute.ScheduleSetup)]
    [InlineData(WorkspaceRoute.ScheduleBoard)]
    public Task SingleProjectHidesEntriesOnEveryMainPage(WorkspaceRoute route) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(); Generate(fixture);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "single-entries.json")));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.True(shell.Navigate(route)); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.IsVisible && Equals(b.Content, "选手兼项"));
            var command = shell.CurrentPage switch
            {
                RostersPageViewModel roster => roster.PlayerEntriesCommand,
                ScheduleSetupPageViewModel setup => setup.PlayerEntriesCommand,
                ScheduleBoardPageViewModel board => board.ShowPlayerEntriesCommand,
                _ => throw new InvalidOperationException("Unexpected page")
            };
            Assert.False(command.CanExecute(null)); command.Execute(null); Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.OwnedWindows.OfType<PlayerEntriesWindow>());
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    public Task RosterEntriesStayHiddenWithFewerThanTwoImportedProjects(int projectCount, int importCount) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(projectCount, purpose: TournamentPurpose.PublicDrawOnly, confirmDraws: false, importCount: importCount);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "hidden-entries.json")));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.Rosters); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.IsVisible && Equals(b.Content, "选手兼项"));
            var roster = Assert.IsType<RostersPageViewModel>(shell.CurrentPage);
            Assert.False(roster.PlayerEntriesCommand.CanExecute(null)); roster.PlayerEntriesCommand.Execute(null);
            Assert.Empty(window.OwnedWindows.OfType<PlayerEntriesWindow>());
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData(TournamentPurpose.FullTournament)]
    [InlineData(TournamentPurpose.PublicDrawOnly)]
    public Task ImportingSecondProjectRevealsEntriesWithoutReopeningPage(TournamentPurpose purpose) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(2, purpose: purpose, confirmDraws: false, importCount: 1);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "second-entries.json")));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.Rosters); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var roster = Assert.IsType<RostersPageViewModel>(shell.CurrentPage);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.IsVisible && Equals(b.Content, "选手兼项"));
            var workspace = fixture.Workflow.CurrentSession!.Workspace;
            fixture.Workflow.ImportRoster(workspace.Projects[1].Id, Path.Combine(fixture.DirectoryPath, workspace.Projects[0].Id + ".xlsx"), workspace.Revision);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Same(roster, shell.CurrentPage);
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => b.IsVisible && Equals(b.Content, "选手兼项"));
            Assert.True(button.IsEffectivelyEnabled); button.Command!.Execute(null);
            Assert.Single(window.OwnedWindows.OfType<PlayerEntriesWindow>());
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task MainPageOffersPeerButtonsAndEntriesCanOpenBeforeTheScheduleBoard() => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(3, deterministicPlayerIds: true);
        Generate(fixture);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "entries-recent.json")));
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleSetup); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Empty(window.OwnedWindows);
            var setup = Assert.IsType<ScheduleSetupPageViewModel>(shell.CurrentPage);
            setup.MinimumRestText = "25";
            var before = fixture.Workflow.CurrentSession;
            var button = window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => Equals(b.Content, "选手兼项"));
            Assert.NotNull(button);
            var boardButton = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "查看赛程板"));
            Assert.Same(boardButton.Parent, button.Parent);
            Assert.True(button.IsEffectivelyEnabled);
            button.Command!.Execute(null); Dispatcher.UIThread.RunJobs();
            var entries = Assert.IsType<PlayerEntriesWindow>(Assert.Single(window.OwnedWindows));
            Assert.True(entries.CanResize);
            Assert.True(window.IsEnabled);
            Assert.Contains(entries.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("兼项选手 2 人") == true);
            button.Command.Execute(null); Dispatcher.UIThread.RunJobs();
            Assert.Same(entries, Assert.Single(window.OwnedWindows));
            Assert.Same(before, fixture.Workflow.CurrentSession);
            var model = Assert.IsType<PlayerEntriesViewModel>(entries.DataContext);
            var match = model.ConfirmedMatches.Last();
            match.FocusCommand.Execute(null); Dispatcher.UIThread.RunJobs();
            var board = Assert.Single(window.OwnedWindows.OfType<ScheduleBoardWindow>());
            var boardModel = Assert.IsType<ScheduleBoardPageViewModel>(board.DataContext);
            Assert.Equal(match.Appearance.Key, boardModel.SelectedMatch!.Key);
            Assert.Equal(match.Appearance.Placement.DayLabel, boardModel.SelectedDay);
            Assert.IsType<ScheduleSetupPageViewModel>(shell.CurrentPage); // Viewing does not discard setup drafts.
            Assert.Equal("25", setup.MinimumRestText);
            board.Close(); Dispatcher.UIThread.RunJobs();
            Assert.True(entries.IsVisible);
            match.FocusCommand.Execute(null); Dispatcher.UIThread.RunJobs();
            var reopenedBoard = Assert.Single(window.OwnedWindows.OfType<ScheduleBoardWindow>());
            Assert.Same(before, fixture.Workflow.CurrentSession);
            var selectedIdentity = model.SelectedPlayer!.IdentityKey;
            entries.Close(); Dispatcher.UIThread.RunJobs(); Assert.True(reopenedBoard.IsVisible);
            button.Command.Execute(null); Dispatcher.UIThread.RunJobs();
            var reopenedEntries = Assert.Single(window.OwnedWindows.OfType<PlayerEntriesWindow>());
            Assert.Same(model, reopenedEntries.DataContext);
            Assert.Equal(selectedIdentity, model.SelectedPlayer!.IdentityKey);
            window.Close(); Dispatcher.UIThread.RunJobs(); Assert.False(reopenedEntries.IsVisible);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task LiveWindowFollowsMoveUndoAndResultImportWhileMainWindowShowsAnotherPage() => ui.Dispatch(async () =>
    {
        using var fixture = new ScheduleUiFixture(3, deterministicPlayerIds: true);
        Generate(fixture);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "entries-live.json")));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleBoard); Dispatcher.UIThread.RunJobs();
            var boardWindow = Assert.Single(window.OwnedWindows);
            var board = Assert.IsType<ScheduleBoardPageViewModel>(boardWindow.DataContext);
            await board.InitializeAsync(); board.ShowPlayerEntriesCommand.Execute(null);
            var entriesWindow = Assert.Single(window.OwnedWindows.OfType<PlayerEntriesWindow>());
            var entries = Assert.IsType<PlayerEntriesViewModel>(entriesWindow.DataContext);
            entries.SelectedSortIndex = 2; entries.SelectedPlayer = entries.Players.Last();
            var identity = entries.SelectedPlayer.IdentityKey;
            var original = entries.ConfirmedMatches[0].Appearance;
            shell.Navigate(WorkspaceRoute.Overview);
            await board.RequestMoveAsync(new(original.Key, "2026-10-05", new(15, 0), "B2"));
            Dispatcher.UIThread.RunJobs();
            var moved = entries.ConfirmedMatches.Single(m => m.Appearance.Key == original.Key);
            Assert.Contains("2026-10-05 15:00", moved.Appearance.Position);
            Assert.Equal(identity, entries.SelectedPlayer!.IdentityKey); Assert.Equal(2, entries.SelectedSortIndex);
            Assert.Equal(2, entriesWindow.FindControl<ComboBox>("PlayerEntriesSort")!.SelectedIndex);
            Assert.Same(entries.SelectedPlayer, entriesWindow.FindControl<ListBox>("PlayerEntriesList")!.SelectedItem);
            var beforeFocus = fixture.Workflow.CurrentSession;
            moved.FocusCommand.Execute(null); Dispatcher.UIThread.RunJobs();
            Assert.Equal(original.Key, board.SelectedMatch!.Key); Assert.Equal("2026-10-05", board.SelectedDay);
            Assert.Same(beforeFocus, fixture.Workflow.CurrentSession);
            await board.UndoCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(original.Placement, entries.ConfirmedMatches.Single(m => m.Appearance.Key == original.Key).Appearance.Placement);

            // Import through the real record reader/workflow, not by manually refreshing the view model.
            var workspace = fixture.Workflow.CurrentSession!.Workspace;
            var path = Path.Combine(fixture.DirectoryPath, "filled-record.xlsx");
            new WorkspaceMatchRecordWriter().Write(path, new WorkspaceScheduleExportContext(workspace),
                [new WorkspaceRecordExportRow(original.Key, DateOnly.Parse(original.Placement.DayLabel))]);
            using (var book = new XLWorkbook(path))
            {
                var sheet = book.Worksheet("对阵记录表");
                sheet.Cell(6, 9).Value = "21-10"; sheet.Cell(6, 10).Value = 20;
                sheet.Cell(6, 12).Value = "A"; sheet.Cell(6, 21).Value = "正常";
                sheet.Cell(6, 22).Value = original.Placement.DayLabel;
                book.Save();
            }
            var preview = fixture.Workflow.PreviewResultImport([path]);
            fixture.Workflow.ImportResults(preview, new(false, null), workspace.Revision);
            Dispatcher.UIThread.RunJobs();
            Assert.True(entries.ConfirmedMatches.Single(m => m.Appearance.Key == original.Key).Appearance.IsCompleted);
            Assert.Equal(1, entries.SelectedPlayer!.Entry.CompletedCount);
            Assert.Equal(identity, entries.SelectedPlayer.IdentityKey); Assert.Equal(2, entries.SelectedSortIndex);
            Assert.Same(entriesWindow, Assert.Single(window.OwnedWindows.OfType<PlayerEntriesWindow>()));
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task SwitchingWorkspacesClosesEntriesEvenIfTheBoardWasNeverOpened() => ui.Dispatch(async () =>
    {
        using var first = new ScheduleUiFixture(3, deterministicPlayerIds: true); Generate(first);
        using var second = new ScheduleUiFixture(); Generate(second);
        var window = new AppShellWindow(first.Workflow, new RecentWorkspaceStore(Path.Combine(first.DirectoryPath, "entries-switch.json")));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleSetup); Dispatcher.UIThread.RunJobs();
            var setup = Assert.IsType<ScheduleSetupPageViewModel>(shell.CurrentPage);
            setup.PlayerEntriesCommand.Execute(null);
            var entriesWindow = Assert.Single(window.OwnedWindows.OfType<PlayerEntriesWindow>());
            Assert.Empty(window.OwnedWindows.OfType<ScheduleBoardWindow>());
            var entries = Assert.IsType<PlayerEntriesViewModel>(entriesWindow.DataContext);
            var retainedFocus = entries.ConfirmedMatches[0].FocusCommand;
            Assert.True(await shell.OpenWorkspaceAsync(second.Workflow.CurrentSession!.WorkspacePath));
            Dispatcher.UIThread.RunJobs();
            Assert.False(entriesWindow.IsVisible); Assert.Empty(window.OwnedWindows);
            Assert.False(retainedFocus.CanExecute(null)); Assert.False(setup.PlayerEntriesCommand.CanExecute(null));
            var opened = first.Workflow.CurrentSession;
            retainedFocus.Execute(null); Assert.Same(opened, first.Workflow.CurrentSession);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task SummaryPageAlsoOffersEntriesNextToTheScheduleBoard() => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(3, deterministicPlayerIds: true); Generate(fixture);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "entries-summary.json")));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleBoard); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var boardWindow = Assert.Single(window.OwnedWindows);
            var openBoard = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "打开独立赛程板 ↗"));
            var entriesButton = window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => Equals(b.Content, "选手兼项"));
            Assert.NotNull(entriesButton); Assert.Same(openBoard.Parent, entriesButton.Parent);
            Assert.DoesNotContain(boardWindow.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "选手兼项"));
            entriesButton.Command!.Execute(null); Dispatcher.UIThread.RunJobs();
            Assert.Single(window.OwnedWindows.OfType<PlayerEntriesWindow>());
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task EntriesCanOpenFromSetupBeforeAScheduleExists() => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(3, deterministicPlayerIds: true);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "entries-no-schedule.json")));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleSetup); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var button = window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => Equals(b.Content, "选手兼项"));
            Assert.NotNull(button); Assert.True(button.IsEffectivelyEnabled);
            button.Command!.Execute(null);
            Assert.Single(window.OwnedWindows.OfType<PlayerEntriesWindow>());
            Assert.Empty(window.OwnedWindows.OfType<ScheduleBoardWindow>());
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData(TournamentPurpose.FullTournament)]
    [InlineData(TournamentPurpose.PublicDrawOnly)]
    public Task RosterPageOpensReadOnlyEntriesWithoutDrawingOrLeavingSeedEdits(TournamentPurpose purpose) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(3, purpose: purpose, deterministicPlayerIds: true, confirmDraws: false);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "roster-entries.json")));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.Rosters); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var roster = Assert.IsType<RostersPageViewModel>(shell.CurrentPage);
            roster.SelectedProject!.BeginSeedEditCommand.Execute(null);
            var before = fixture.Workflow.CurrentSession;
            var button = window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => Equals(b.Content, "选手兼项"));
            Assert.NotNull(button); Assert.True(button.IsEffectivelyEnabled);
            var next = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, roster.NextCommand));
            Assert.Same(next.Parent, button.Parent);
            button.Command!.Execute(null); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var entriesWindow = Assert.Single(window.OwnedWindows.OfType<PlayerEntriesWindow>());
            var model = Assert.IsType<PlayerEntriesViewModel>(entriesWindow.DataContext);
            Assert.Equal(2, model.Players.Count);
            Assert.Empty(model.ConfirmedMatches); Assert.Empty(model.PotentialMatches);
            Assert.False(entriesWindow.FindControl<StackPanel>("PlayerScheduleDetails")!.IsVisible);
            Assert.Equal(3, entriesWindow.FindControl<ItemsControl>("PlayerRegistrations")!.ItemCount);
            Assert.DoesNotContain(entriesWindow.GetVisualDescendants().OfType<Button>(), b => b.IsVisible && Equals(b.Content, "在赛程板中定位"));
            Assert.Same(before, fixture.Workflow.CurrentSession);
            Assert.Same(roster, shell.CurrentPage); Assert.True(roster.SelectedProject.IsSeedEditing);
            Assert.Empty(window.OwnedWindows.OfType<ScheduleBoardWindow>());
            model.SelectedPlayer = model.Players.Last(); var selected = model.SelectedPlayer.IdentityKey;
            entriesWindow.Close(); button.Command.Execute(null); Dispatcher.UIThread.RunJobs();
            var reopened = Assert.Single(window.OwnedWindows.OfType<PlayerEntriesWindow>());
            Assert.Equal(selected, Assert.IsType<PlayerEntriesViewModel>(reopened.DataContext).SelectedPlayer!.IdentityKey);
            var search = reopened.FindControl<TextBox>("PlayerEntriesSearch")!;
            search.Text = model.SelectedPlayer!.Entry.StudentId; Dispatcher.UIThread.RunJobs();
            Assert.Single(model.Players); Assert.Equal(selected, model.SelectedPlayer!.IdentityKey);
            Assert.Same(model.SelectedPlayer, reopened.FindControl<ListBox>("PlayerEntriesList")!.SelectedItem);
            search.Text = "找不到的姓名"; Dispatcher.UIThread.RunJobs();
            Assert.Empty(model.Players); Assert.True(reopened.FindControl<TextBlock>("PlayerSearchEmpty")!.IsVisible);
            search.Text = ""; Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, model.Players.Count); Assert.Equal(selected, model.SelectedPlayer!.IdentityKey);
            Assert.Same(model.SelectedPlayer, reopened.FindControl<ListBox>("PlayerEntriesList")!.SelectedItem);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task RosterEntriesBecomeAvailableOnlyAfterAllImports() => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(3, deterministicPlayerIds: true, confirmDraws: false, importCount: 2);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "partial-entries.json")));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.Rosters); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var button = window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => Equals(b.Content, "选手兼项"));
            Assert.NotNull(button); Assert.True(button.IsVisible); Assert.False(button.IsEffectivelyEnabled);
            button.Command!.Execute(null); Assert.Empty(window.OwnedWindows);
            var project = fixture.Workflow.CurrentSession!.Workspace.Projects.Last();
            var file = Path.Combine(fixture.DirectoryPath, "last-roster.xlsx");
            using (var book = new XLWorkbook())
            {
                var sheet = book.AddWorksheet("名单");
                sheet.Cell(1, 1).Value = "姓名"; sheet.Cell(1, 2).Value = "学号";
                sheet.Cell(1, 3).Value = "搭档姓名"; sheet.Cell(1, 4).Value = "搭档学号";
                sheet.Cell(2, 1).Value = "甲"; sheet.Cell(2, 2).Value = "ui-player-1";
                sheet.Cell(2, 3).Value = "乙"; sheet.Cell(2, 4).Value = "partner-1";
                sheet.Cell(3, 1).Value = "丙"; sheet.Cell(3, 2).Value = "ui-player-2";
                sheet.Cell(3, 3).Value = "丁"; sheet.Cell(3, 4).Value = "partner-2";
                book.SaveAs(file);
            }
            fixture.Workflow.ImportRoster(project.Id, file, fixture.Workflow.CurrentSession.Workspace.Revision);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(button.IsEffectivelyEnabled); button.Command.Execute(null);
            Assert.Single(window.OwnedWindows.OfType<PlayerEntriesWindow>());
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task OpenEntriesSurviveScheduleCreationAndRemoval() => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(3, deterministicPlayerIds: true);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "entries-lifecycle.json")));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.Rosters); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var button = window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => Equals(b.Content, "选手兼项"));
            Assert.NotNull(button); button.Command!.Execute(null);
            var entriesWindow = Assert.Single(window.OwnedWindows.OfType<PlayerEntriesWindow>());
            var model = Assert.IsType<PlayerEntriesViewModel>(entriesWindow.DataContext);
            model.SelectedPlayer = model.Players.Last(); var identity = model.SelectedPlayer.IdentityKey;
            Assert.False(entriesWindow.FindControl<StackPanel>("PlayerScheduleDetails")!.IsVisible);
            Generate(fixture); Dispatcher.UIThread.RunJobs();
            Assert.Same(entriesWindow, Assert.Single(window.OwnedWindows.OfType<PlayerEntriesWindow>()));
            Assert.Equal(identity, model.SelectedPlayer!.IdentityKey); Assert.NotEmpty(model.ConfirmedMatches);
            Assert.Same(model.SelectedPlayer, entriesWindow.FindControl<ListBox>("PlayerEntriesList")!.SelectedItem);
            Assert.True(entriesWindow.FindControl<StackPanel>("PlayerScheduleDetails")!.IsVisible);
            Assert.False(entriesWindow.FindControl<Expander>("PlayerRegistrationsExpander")!.IsExpanded);
            var focus = model.ConfirmedMatches[0].FocusCommand;
            focus.Execute(null); Dispatcher.UIThread.RunJobs();
            Assert.Single(window.OwnedWindows.OfType<ScheduleBoardWindow>());
            var first = fixture.Workflow.CurrentSession!.Workspace.Projects.First();
            fixture.Workflow.ReopenDraw(first.Id, "核对名单", fixture.Workflow.CurrentSession.Workspace.Revision);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(entriesWindow, Assert.Single(window.OwnedWindows));
            Assert.Equal(identity, model.SelectedPlayer!.IdentityKey);
            Assert.Same(model.SelectedPlayer, entriesWindow.FindControl<ListBox>("PlayerEntriesList")!.SelectedItem);
            Assert.Empty(model.ConfirmedMatches); Assert.False(focus.CanExecute(null));
            Assert.False(entriesWindow.FindControl<StackPanel>("PlayerScheduleDetails")!.IsVisible);
            Assert.True(entriesWindow.FindControl<Expander>("PlayerRegistrationsExpander")!.IsExpanded);
            Assert.Equal(3, entriesWindow.FindControl<ItemsControl>("PlayerRegistrations")!.ItemCount);
            focus.Execute(null); Assert.Empty(window.OwnedWindows.OfType<ScheduleBoardWindow>());
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    private static void Generate(ScheduleUiFixture fixture) => fixture.Workflow.GenerateSchedule(
        new([new(new(2026, 10, 4), new(14, 0), new(18, 0), ["B1", "B2"]),
            new(new(2026, 10, 5), new(14, 0), new(18, 0), ["B1", "B2"])], 2, 20, 8),
        new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), fixture.Workflow.CurrentSession!.Workspace.Revision);

    public void Dispose() => ui.Dispose();
}
