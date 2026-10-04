using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Desktop.Controls;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class ScheduleBoardWindowTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Fact]
    public Task OpeningBoardUsesOneIndependentWindowAndKeepsMainWorkflowUsable() => ui.Dispatch(async () =>
    {
        using var fixture = new ScheduleUiFixture(); Generate(fixture);
        var window = CreateWindow(fixture);
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.True(shell.Navigate(WorkspaceRoute.ScheduleBoard));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var boardWindow = Assert.Single(window.OwnedWindows);
            Assert.True(boardWindow.CanResize);
            Assert.Equal(WindowState.Maximized, boardWindow.WindowState);
            Assert.Empty(window.GetVisualDescendants().OfType<ScheduleBoardControl>());
            Assert.Single(boardWindow.GetVisualDescendants().OfType<ScheduleBoardControl>());
            var model = Assert.IsType<ScheduleBoardPageViewModel>(boardWindow.DataContext);
            await model.InitializeAsync();
            Assert.True(window.IsEnabled); // Nonmodal: settings/materials remain reachable.
            Assert.True(shell.Navigate(WorkspaceRoute.Overview));
            Assert.True(model.CanEdit);
            Assert.True(shell.Navigate(WorkspaceRoute.ScheduleBoard));
            Assert.Same(model, shell.CurrentPage);
            Assert.Same(boardWindow, Assert.Single(window.OwnedWindows));
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task ClosingAndReopeningBoardKeepsViewStateWithoutClosingTheTournament() => ui.Dispatch(async () =>
    {
        using var fixture = new ScheduleUiFixture(); Generate(fixture);
        var window = CreateWindow(fixture);
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleBoard); Dispatcher.UIThread.RunJobs();
            var boardWindow = Assert.Single(window.OwnedWindows);
            var model = Assert.IsType<ScheduleBoardPageViewModel>(boardWindow.DataContext);
            await model.InitializeAsync();
            model.SelectedDay = "2026-10-05"; model.Zoom = .85;
            var before = fixture.Workflow.CurrentSession;
            boardWindow.Close();
            Assert.True(window.IsVisible); Assert.Same(before, fixture.Workflow.CurrentSession);
            Assert.Empty(window.OwnedWindows);
            shell.Navigate(WorkspaceRoute.ScheduleBoard); Dispatcher.UIThread.RunJobs();
            Assert.Same(model, Assert.Single(window.OwnedWindows).DataContext);
            Assert.Equal("2026-10-05", model.SelectedDay); Assert.Equal(.85, model.Zoom);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task DetachedBoardCanSaveAndUndoWhileMainWindowShowsAnotherPage() => ui.Dispatch(async () =>
    {
        using var fixture = new ScheduleUiFixture(); Generate(fixture);
        var window = CreateWindow(fixture);
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleBoard); Dispatcher.UIThread.RunJobs();
            var boardWindow = Assert.Single(window.OwnedWindows);
            var model = Assert.IsType<ScheduleBoardPageViewModel>(boardWindow.DataContext);
            await model.InitializeAsync(); shell.Navigate(WorkspaceRoute.Overview);
            var overview = shell.CurrentPage; var original = model.SelectedMatch!.Placement;
            await model.RequestMoveAsync(new(model.SelectedMatch.Key, "2026-10-05", new(15, 0), "B2"));
            Dispatcher.UIThread.RunJobs(); boardWindow.UpdateLayout();
            Assert.Same(overview, shell.CurrentPage);
            Assert.Equal(new TimeOnly(15, 0), model.Board.Cards.Single().Placement.StartTime);
            Assert.Same(fixture.Workflow.CurrentSession, model.Session);
            Assert.Contains(boardWindow.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("赛程移动已保存") == true);
            await model.UndoCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(original, model.Board.Cards.Single().Placement);
            Assert.Equal(original, fixture.Workflow.CurrentSession!.Workspace.Schedule!.Placements[original.MatchId]);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task SwitchingWorkspacesClosesOldBoardAndDisablesItsRetainedCommands() => ui.Dispatch(async () =>
    {
        using var first = new ScheduleUiFixture(); Generate(first);
        using var second = new ScheduleUiFixture(); Generate(second);
        var window = CreateWindow(first);
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleBoard); Dispatcher.UIThread.RunJobs();
            var oldWindow = Assert.Single(window.OwnedWindows);
            var old = Assert.IsType<ScheduleBoardPageViewModel>(oldWindow.DataContext);
            await old.InitializeAsync(); old.TargetTimeText = "16:00";
            await old.PreviewMoveCommand.ExecuteAsync();
            Assert.True(old.ConfirmMoveCommand.CanExecute(null));
            shell.Navigate(WorkspaceRoute.Overview);
            Assert.True(await shell.OpenWorkspaceAsync(second.Workflow.CurrentSession!.WorkspacePath));
            Dispatcher.UIThread.RunJobs();
            Assert.False(oldWindow.IsVisible);
            Assert.False(old.CanEdit); Assert.False(old.ConfirmMoveCommand.CanExecute(null));
            Assert.DoesNotContain(window.OwnedWindows, child => ReferenceEquals(child.DataContext, old));
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    private static AppShellWindow CreateWindow(ScheduleUiFixture fixture) => new(fixture.Workflow,
        new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "board-window-recent.json")));

    [Fact]
    public Task InspectingAMatchDoesNotOpenAdjustmentFieldsOrChangeTheArchive() => ui.Dispatch(async () =>
    {
        using var fixture = new ScheduleUiFixture(); Generate(fixture);
        var window = CreateWindow(fixture);
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleBoard); Dispatcher.UIThread.RunJobs();
            var boardWindow = Assert.Single(window.OwnedWindows);
            var model = Assert.IsType<ScheduleBoardPageViewModel>(boardWindow.DataContext);
            await model.InitializeAsync(); Dispatcher.UIThread.RunJobs();
            var view = Assert.Single(boardWindow.GetVisualDescendants().OfType<BadmintonDraw.Desktop.Views.ScheduleBoardPage>());
            var before = fixture.Workflow.CurrentSession;
            model.SelectMatch(model.Matches[0].Key); boardWindow.UpdateLayout();
            Assert.True(view.FindControl<Border>("MoveEditorPanel")!.IsVisible);
            Assert.False(view.FindControl<StackPanel>("AdjustmentFields")!.IsVisible);
            var edit = view.FindControl<Button>("EditSelectedMatch")!;
            Assert.True(edit.IsEffectivelyEnabled);
            edit.Command!.Execute(null); boardWindow.UpdateLayout();
            Assert.True(view.FindControl<StackPanel>("AdjustmentFields")!.IsVisible);
            Assert.Same(before, fixture.Workflow.CurrentSession);
            Assert.False(model.ConfirmMoveCommand.CanExecute(null));
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task RegeneratingFromMainWindowRefreshesDetachedBoardAndRejectsOldProposal() => ui.Dispatch(async () =>
    {
        using var fixture = new ScheduleUiFixture(); Generate(fixture);
        var window = CreateWindow(fixture);
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleBoard); Dispatcher.UIThread.RunJobs();
            var boardWindow = Assert.Single(window.OwnedWindows);
            var model = Assert.IsType<ScheduleBoardPageViewModel>(boardWindow.DataContext);
            await model.InitializeAsync(); model.TargetTimeText = "16:00";
            await model.PreviewMoveCommand.ExecuteAsync(); Assert.True(model.ConfirmMoveCommand.CanExecute(null));
            shell.Navigate(WorkspaceRoute.ScheduleSetup);
            fixture.Workflow.GenerateSchedule(new([new(new(2026, 10, 6), new(14, 0), new(18, 0), ["C1"])], 1, 20, 8),
                new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), fixture.Workflow.CurrentSession!.Workspace.Revision);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("2026-10-06", model.SelectedDay);
            Assert.Equal("C1", model.Board.Cards.Single().Placement.Court);
            Assert.False(model.ConfirmMoveCommand.CanExecute(null)); Assert.True(model.HasEditorConflict);
            var saved = fixture.Workflow.CurrentSession;
            await model.ConfirmMoveCommand.ExecuteAsync(); Assert.Same(saved, fixture.Workflow.CurrentSession);
            await model.ResetEditorCommand.ExecuteAsync();
            Assert.True(model.CanEdit); Assert.Equal("C1", model.TargetCourt);
            Assert.Same(boardWindow, Assert.Single(window.OwnedWindows));
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    private static void Generate(ScheduleUiFixture fixture) => fixture.Workflow.GenerateSchedule(
        new([new(new(2026, 10, 4), new(14, 0), new(18, 0), ["B1", "B2"]),
            new(new(2026, 10, 5), new(14, 0), new(18, 0), ["B1", "B2"])], 2, 20, 8),
        new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), fixture.Workflow.CurrentSession!.Workspace.Revision);

    [Fact]
    public Task SelectingAnotherMatchDuringInitialLoadLeavesAVisibleRecoveryAction() => ui.Dispatch(async () =>
    {
        using var store = new BlockingReadStore();
        using var fixture = new ScheduleUiFixture(3, new TournamentWorkspaceWorkflow(store)); Generate(fixture);
        var window = CreateWindow(fixture);
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            store.Block = true;
            shell.Navigate(WorkspaceRoute.ScheduleBoard); Dispatcher.UIThread.RunJobs();
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var boardWindow = Assert.Single(window.OwnedWindows);
            var model = Assert.IsType<ScheduleBoardPageViewModel>(boardWindow.DataContext);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(shell.IsBusy) && !shell.IsBusy) finished.TrySetResult(); };
            model.SelectMatch(model.Matches[1].Key);
            store.Block = false; store.Release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs(); boardWindow.UpdateLayout();
            Assert.False(model.CanEdit);
            Assert.Contains(boardWindow.GetVisualDescendants().OfType<Button>(), button =>
                ReferenceEquals(button.Command, model.ResetEditorCommand) && button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
            await model.ResetEditorCommand.ExecuteAsync();
            Assert.True(model.CanEdit); Assert.True(model.EditSelectedCommand.CanExecute(null));
        }
        finally { store.Release.Set(); window.Close(); }
        return 0;
    }, CancellationToken.None);

    private sealed class BlockingReadStore : TournamentWorkspaceStore, IDisposable
    {
        public bool Block;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public override TournamentWorkspace Read(string path)
        {
            if (Block) { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test read gate was not released."); }
            return base.Read(path);
        }
        public void Dispose() => Release.Dispose();
    }

    public void Dispose() => ui.Dispose();
}
