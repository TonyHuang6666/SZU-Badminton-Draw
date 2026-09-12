using System.Security.Cryptography;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Workflows.Tournaments;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class ScheduleBoardPageViewModelTests
{
    private static async Task<ScheduleBoardPageViewModel> Page(ScheduleUiFixture fixture)
    {
        fixture.Workflow.GenerateSchedule(new([new(new(2026, 9, 13), new(9, 0), new(18, 0), ["B1", "B2"]), new(new(2026, 9, 15), new(9, 0), new(18, 0), ["B1", "B2"])], 2, 30, 4),
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), fixture.Workflow.CurrentSession!.Workspace.Revision);
        fixture.Shell.RegisterPageFactory(WorkspaceRoute.ScheduleBoard, s => new ScheduleBoardPageViewModel(fixture.Shell, s));
        fixture.Shell.Navigate(WorkspaceRoute.ScheduleBoard);
        var page = Assert.IsType<ScheduleBoardPageViewModel>(fixture.Shell.CurrentPage); await page.InitializeAsync(); return page;
    }
    [Fact]
    public async Task ManualPreviewRequiresExplicitConfirmThenUndoRestoresOnlyPlacement()
    {
        using var fixture = new ScheduleUiFixture(3); var page = await Page(fixture);
        var before = fixture.Workflow.CurrentSession!; var original = page.SelectedMatch!.Placement;
        page.TargetDay = "2026-09-15"; page.TargetTimeText = "11:30"; page.TargetCourt = "B2";
        await page.PreviewMoveCommand.ExecuteAsync();
        Assert.Same(before, fixture.Workflow.CurrentSession); Assert.True(page.ConfirmMoveCommand.CanExecute(null));
        Assert.Single(page.PreviewChanges); Assert.Equal("2026-09-15", page.PreviewChanges[0].After.DayLabel);
        await page.ConfirmMoveCommand.ExecuteAsync();
        Assert.Equal(before.Workspace.Revision + 1, fixture.Workflow.CurrentSession!.Workspace.Revision);
        Assert.Equal("B2", fixture.Workflow.CurrentSession.Workspace.Schedule!.Placements[original.MatchId].Court);
        Assert.True(page.UndoCommand.CanExecute(null));
        await page.UndoCommand.ExecuteAsync();
        Assert.Equal(original, fixture.Workflow.CurrentSession.Workspace.Schedule!.Placements[original.MatchId]);
    }
    [Fact]
    public async Task EditedTargetCancelsOldPreviewAndBlockedCourtNeverSaves()
    {
        using var fixture = new ScheduleUiFixture(); var page = await Page(fixture);
        page.TargetTimeText = "12:00"; await page.PreviewMoveCommand.ExecuteAsync(); Assert.True(page.ConfirmMoveCommand.CanExecute(null));
        page.TargetCourt = "不存在"; Assert.False(page.ConfirmMoveCommand.CanExecute(null));
        var before = fixture.Workflow.CurrentSession!;
        await page.PreviewMoveCommand.ExecuteAsync();
        Assert.False(page.ConfirmMoveCommand.CanExecute(null)); Assert.NotEmpty(page.PreviewViolations);
        Assert.Same(before, fixture.Workflow.CurrentSession);
    }
    [Fact]
    public async Task AuditRefreshInvalidatesConfirmationButRetainsUncommittedManualInput()
    {
        using var fixture = new ScheduleUiFixture(); var page = await Page(fixture);
        page.TargetTimeText = "12:00"; await page.PreviewMoveCommand.ExecuteAsync();
        fixture.Workflow.ExportRosterTemplate(page.SelectedMatch!.Key.ProjectId, Path.Combine(fixture.DirectoryPath, "template.xlsx"), fixture.Workflow.CurrentSession!.Workspace.Revision);
        Assert.Equal("12:00", page.TargetTimeText); Assert.False(page.HasEditorConflict); Assert.False(page.ConfirmMoveCommand.CanExecute(null));
        await page.PreviewMoveCommand.ExecuteAsync(); Assert.True(page.ConfirmMoveCommand.CanExecute(null));
    }
    [Fact]
    public async Task ReloadingExternallyChangedSchedulePreservesFieldsButBlocksOldEditor()
    {
        using var fixture = new ScheduleUiFixture(); var page = await Page(fixture); page.TargetTimeText = "12:00";
        var other = new TournamentWorkspaceWorkflow(); other.OpenWorkspace(fixture.Workflow.CurrentSession!.WorkspacePath);
        other.MoveMatch(new(page.SelectedMatch!.Key, "2026-09-15", new(13, 0), "B1", other.CaptureScheduleEditBaseline()), other.CurrentSession!.Workspace.Revision);
        await fixture.Shell.ReloadCommand.ExecuteAsync();
        Assert.Equal("12:00", page.TargetTimeText); Assert.True(page.HasEditorConflict); Assert.False(page.PreviewMoveCommand.CanExecute(null));
        var path = fixture.Workflow.CurrentSession!.WorkspacePath; var bytes = SHA256.HashData(File.ReadAllBytes(path));
        await page.ConfirmMoveCommand.ExecuteAsync(); Assert.Equal(bytes, SHA256.HashData(File.ReadAllBytes(path)));
        await page.ResetEditorCommand.ExecuteAsync(); Assert.False(page.HasEditorConflict); Assert.StartsWith("13:00", page.TargetTimeText);
    }
    [Fact]
    public async Task CascadePreviewIsSeparateAndCancelDoesNotPersist()
    {
        using var fixture = new ScheduleUiFixture(); var page = await Page(fixture);
        page.TargetDay = "2026-09-15"; page.TargetTimeText = "10:00";
        var before = fixture.Workflow.CurrentSession;
        await page.PreviewCascadeCommand.ExecuteAsync(); Assert.True(page.IsCascadePreview); Assert.True(page.ConfirmMoveCommand.CanExecute(null));
        page.CancelPreviewCommand.Execute(null); Assert.False(page.ConfirmMoveCommand.CanExecute(null)); Assert.Empty(page.PreviewChanges); Assert.Same(before, fixture.Workflow.CurrentSession);
    }
    [Fact]
    public async Task DisposedPageCannotSubmitRetainedCommands()
    {
        using var fixture = new ScheduleUiFixture(); var page = await Page(fixture); page.TargetTimeText = "12:00";
        await page.PreviewMoveCommand.ExecuteAsync(); page.Dispose();
        Assert.False(page.ConfirmMoveCommand.CanExecute(null)); Assert.False(page.PreviewMoveCommand.CanExecute(null));
    }
    [Fact]
    public async Task TargetEditedWhilePreviewIsRunningDoesNotReceiveOldConfirmation()
    {
        using var store = new BlockingReadStore(); using var fixture = new ScheduleUiFixture(workflow: new TournamentWorkspaceWorkflow(store));
        var page = await Page(fixture); page.TargetTimeText = "12:00";
        store.Block = true;
        var task = page.PreviewMoveCommand.ExecuteAsync(); await store.Entered.Task;
        page.TargetTimeText = "13:00"; store.Release.Set(); await task;
        Assert.Equal("13:00", page.TargetTimeText); Assert.False(page.ConfirmMoveCommand.CanExecute(null)); Assert.Empty(page.PreviewChanges);
    }
    [Fact]
    public async Task LegalDropAutosavesButBlockedDropOnlyShowsReadonlyFailure()
    {
        using var fixture = new ScheduleUiFixture(); var page = await Page(fixture); var key = page.SelectedMatch!.Key;
        var before = fixture.Workflow.CurrentSession!.Workspace.Revision;
        await page.RequestMoveAsync(new(key, "2026-09-15", new(12, 0), "B2"));
        Assert.Equal(before + 1, fixture.Workflow.CurrentSession!.Workspace.Revision);
        Assert.Equal("B2", fixture.Workflow.CurrentSession.Workspace.Schedule!.Placements[key.MatchId].Court);
        var session = fixture.Workflow.CurrentSession;
        await page.RequestMoveAsync(new(key, "2026-09-15", new(12, 0), "不存在"));
        Assert.Same(session, fixture.Workflow.CurrentSession); Assert.NotEmpty(page.PreviewViolations); Assert.False(page.ConfirmMoveCommand.CanExecute(null));
    }
    [Fact]
    public async Task TransientSelectionClearDuringRefreshCannotEraseRetainedTarget()
    {
        using var fixture = new ScheduleUiFixture(); var page = await Page(fixture); var selected = page.SelectedMatch;
        page.TargetTimeText = "12:34";
        page.SelectedMatch = null; // ItemsSource replacement may transiently clear a ComboBox selection.
        Assert.Equal(selected, page.SelectedMatch); Assert.Equal("12:34", page.TargetTimeText);
    }
    [Fact]
    public async Task ImportedResultLocksItsCardAndDisablesManualMoveWithoutLockingOtherProjects()
    {
        using var fixture = new ScheduleUiFixture(3); var page = await Page(fixture); var key = page.SelectedMatch!.Key;
        var session = fixture.Workflow.CurrentSession!; var node = session.Workspace.Projects.Single(p => p.Id == key.ProjectId).MatchGraph!.Matches.Single();
        new TournamentWorkspaceStore().Mutate(session.WorkspacePath, session.Workspace.Revision, workspace => workspace with
        {
            Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [key] = new(key,
                Assert.IsType<BadmintonDraw.Core.Matches.EntrantSource.Participant>(node.SideA), Assert.IsType<BadmintonDraw.Core.Matches.EntrantSource.Participant>(node.SideB), "21-10,21-12", 30, DateTimeOffset.UtcNow) },
            Stage = TournamentStage.InProgress
        });
        await fixture.Shell.ReloadCommand.ExecuteAsync(); await page.ResetEditorCommand.ExecuteAsync();
        Assert.True(page.SelectedMatch!.IsLocked); Assert.False(page.PreviewMoveCommand.CanExecute(null));
        page.SelectedMatch = page.Matches.First(c => c.Key.ProjectId != key.ProjectId);
        Assert.True(page.PreviewMoveCommand.CanExecute(null));
    }
    [Fact]
    public async Task ConstraintRelatedMatchFocusUsesOwningProjectRatherThanRootsProject()
    {
        using var fixture = new ScheduleUiFixture(3); var page = await Page(fixture);
        var root = page.Matches[0]; var other = page.Matches.First(c => c.Key.ProjectId != root.Key.ProjectId);
        page.SelectedMatch = root; page.TargetDay = other.Placement.DayLabel; page.TargetTimeText = other.Placement.StartTime.ToString("HH:mm:ss"); page.TargetCourt = other.Placement.Court;
        await page.PreviewMoveCommand.ExecuteAsync();
        WorkspaceMatchKey? focused = null; page.FocusRequested += key => focused = key;
        var issue = page.Issues.First(i => i.FocusRelatedCommand.CanExecute(null)); issue.FocusRelatedCommand.Execute(null);
        Assert.Equal(other.Key, focused);
    }
    [Fact]
    public async Task UnknownDropIdentityCannotFallBackToCurrentlySelectedMatch()
    {
        using var fixture = new ScheduleUiFixture(); var page = await Page(fixture); var session = fixture.Workflow.CurrentSession;
        await page.RequestMoveAsync(new(new(page.SelectedMatch!.Key.ProjectId, Guid.NewGuid()), "2026-09-15", new(12, 0), "B2"));
        Assert.Same(session, fixture.Workflow.CurrentSession); Assert.False(page.ConfirmMoveCommand.CanExecute(null));
    }
    [Fact]
    public async Task OpeningScheduleArchiveInitializesBoardAfterShellOpenBusyPeriod()
    {
        using var fixture = new ScheduleUiFixture(); await Page(fixture);
        using var shell = new AppShellViewModel(new TournamentWorkspaceWorkflow(), () => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null),
            new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "other-recent.json")), action => action());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.RegisterPageFactory(WorkspaceRoute.ScheduleBoard, session =>
        {
            var page = new ScheduleBoardPageViewModel(shell, session);
            page.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(page.CanEdit) && page.CanEdit) ready.TrySetResult(); };
            Assert.True(shell.IsBusy); // The native view attaches inside Open's ApplySession, before its finally releases busy.
            _ = page.InitializeAsync();
            return page;
        });
        Assert.True(await shell.OpenWorkspaceAsync(fixture.Workflow.CurrentSession!.WorkspacePath));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Assert.IsType<ScheduleBoardPageViewModel>(shell.CurrentPage).PreviewMoveCommand.CanExecute(null));
    }
    private sealed class BlockingReadStore : TournamentWorkspaceStore, IDisposable
    {
        public bool Block;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public override TournamentWorkspace Read(string path)
        {
            if (Block) { Entered.TrySetResult(); Release.Wait(TimeSpan.FromSeconds(5)); }
            return base.Read(path);
        }
        public void Dispose() => Release.Dispose();
    }
}
