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
            Assert.Single(window.OwnedWindows.OfType<ScheduleBoardWindow>());
            Assert.Same(before, fixture.Workflow.CurrentSession);
            window.Close(); Dispatcher.UIThread.RunJobs(); Assert.False(entries.IsVisible);
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
    public Task EntriesStayUnavailableUntilAScheduleExists() => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(3, deterministicPlayerIds: true);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "entries-no-schedule.json")));
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleSetup); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var button = window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => Equals(b.Content, "选手兼项"));
            Assert.NotNull(button); Assert.False(button.IsEffectivelyEnabled);
            button.Command!.Execute(null); Assert.Empty(window.OwnedWindows);
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
