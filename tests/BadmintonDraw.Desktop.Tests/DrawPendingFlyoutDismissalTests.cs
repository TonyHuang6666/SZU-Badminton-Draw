using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class DrawPendingFlyoutDismissalTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Fact]
    public Task KeyboardOpeningFocusesPendingActionAndEscapeRestoresSummaryFocus() => ui.Dispatch(() =>
    {
        using var f = new PendingFlyoutFixture();
        var session = f.Data.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(session.WorkspacePath);
        Assert.False(f.Flyout.IsOpen);
        Assert.True(f.Summary.Focus());
        f.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        f.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        f.Layout();
        Assert.True(f.Flyout.IsOpen);
        Assert.Equal(2, f.PendingButtons.Length);
        var focused = Assert.Single(f.PendingButtons, button => button.IsFocused);
        Assert.Same(Assert.IsType<ProjectDrawViewModel>(focused.DataContext).ReviewDrawCommand, focused.Command);
        var popup = TopLevel.GetTopLevel(focused)!;
        popup.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        f.Layout();
        Assert.False(f.Flyout.IsOpen);
        Assert.True(f.Summary.IsFocused);
        Assert.Same(session, f.Data.Workflow.CurrentSession);
        Assert.Equal(bytes, File.ReadAllBytes(session.WorkspacePath));
    }, CancellationToken.None);

    [Fact]
    public Task OutsidePointerClickDismissesWithoutChangingSelectionOrArchive() => ui.Dispatch(() =>
    {
        using var f = new PendingFlyoutFixture();
        var session = f.Data.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(session.WorkspacePath);
        var selected = f.Page.SelectedProject;
        f.OpenByPointer();
        var outside = new Point(30, 30);
        f.Window.MouseDown(outside, MouseButton.Left, RawInputModifiers.None);
        f.Window.MouseUp(outside, MouseButton.Left, RawInputModifiers.None);
        f.Layout();
        Assert.False(f.Flyout.IsOpen);
        Assert.True(f.Summary.IsFocused);
        Assert.Same(selected, f.Page.SelectedProject);
        Assert.Same(session, f.Data.Workflow.CurrentSession);
        Assert.Equal(bytes, File.ReadAllBytes(session.WorkspacePath));
    }, CancellationToken.None);

    [Fact]
    public Task ReloadAndDrawUpdatesNeverOpenPendingFlyoutAutomatically() => ui.Dispatch(async () =>
    {
        using var f = new PendingFlyoutFixture();
        Assert.False(f.Flyout.IsOpen);
        await f.Shell.ReloadCommand.ExecuteAsync(); f.Layout();
        Assert.False(f.Flyout.IsOpen);
        var project = f.Data.Workflow.CurrentSession!.Workspace.Projects[0];
        f.Data.Workflow.PreviewDraw(project.Id, project.Draw!.Result.Settings, f.Data.Workflow.CurrentSession.Workspace.Revision);
        f.Layout();
        Assert.False(f.Flyout.IsOpen);
        f.OpenByInvoke();
        var focused = f.PendingButtons[0]; focused.Focus();
        TopLevel.GetTopLevel(focused)!.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        f.Layout();
        await f.Shell.ReloadCommand.ExecuteAsync(); f.Layout();
        Assert.False(f.Flyout.IsOpen);
        Assert.Equal(2, f.Page.PendingProjects.Count);
        var session = f.Data.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(session.WorkspacePath);
        f.OpenByInvoke();
        Assert.Same(session, f.Data.Workflow.CurrentSession);
        Assert.Equal(bytes, File.ReadAllBytes(session.WorkspacePath));
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task ConfirmingAllPendingProjectsClosesFlyoutAndEnablesScheduling() => ui.Dispatch(() =>
    {
        using var f = new PendingFlyoutFixture();
        f.OpenByPointer();
        foreach (var project in f.Data.Workflow.CurrentSession!.Workspace.Projects)
            f.Data.Workflow.ConfirmDraw(project.Id, f.Data.Workflow.CurrentSession.Workspace.Revision);
        var saved = f.Data.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(saved.WorkspacePath);
        f.Layout();
        Assert.False(f.Flyout.IsOpen);
        Assert.False(f.Summary.IsEffectivelyVisible);
        Assert.Empty(f.Page.PendingProjects);
        var next = Assert.Single(f.Window.GetVisualDescendants().OfType<Button>(), button =>
            ReferenceEquals(button.Command, f.Page.ContinueToScheduleCommand));
        Assert.True(next.IsEffectivelyEnabled); Assert.True(next.IsEffectivelyVisible);
        Assert.Same(saved, f.Data.Workflow.CurrentSession);
        Assert.Equal(bytes, File.ReadAllBytes(saved.WorkspacePath));
        Assert.Null(saved.Workspace.Schedule);
    }, CancellationToken.None);

    [Fact]
    public Task PageNavigationClosesAnchoredFlyoutWithoutSavingAnything() => ui.Dispatch(() =>
    {
        using var f = new PendingFlyoutFixture();
        var session = f.Data.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(session.WorkspacePath);
        f.OpenByInvoke();
        var navigation = Assert.Single(f.Window.GetVisualDescendants().OfType<Button>(), button =>
            ReferenceEquals(button.Command, f.Shell.NavigationItems.Single(item => item.Route == WorkspaceRoute.Rosters).Command));
        Invoke(navigation); f.Layout();
        Assert.IsType<RostersPageViewModel>(f.Shell.CurrentPage);
        Assert.False(f.Flyout.IsOpen);
        Assert.Same(session, f.Data.Workflow.CurrentSession);
        Assert.Equal(bytes, File.ReadAllBytes(session.WorkspacePath));
    }, CancellationToken.None);

    private static void Invoke(Button button) =>
        Assert.IsAssignableFrom<IInvokeProvider>(ControlAutomationPeer.CreatePeerForElement(button)).Invoke();

    private sealed class PendingFlyoutFixture : IDisposable
    {
        internal ScheduleUiFixture Data { get; } = new(2);
        internal AppShellWindow Window { get; }
        internal AppShellViewModel Shell { get; }
        internal PublicDrawPageViewModel Page { get; }
        internal Button Summary { get; }
        internal Flyout Flyout { get; }
        internal Button[] PendingButtons => Assert.IsAssignableFrom<Control>(Flyout.Content).GetVisualDescendants()
            .OfType<Button>().Where(button => button.Name == "ReviewPendingDrawProject" && button.IsEffectivelyVisible).ToArray();

        internal PendingFlyoutFixture()
        {
            foreach (var project in Data.Workflow.CurrentSession!.Workspace.Projects)
            {
                Data.Workflow.ReopenDraw(project.Id, "检查浮层", Data.Workflow.CurrentSession.Workspace.Revision);
                Data.Workflow.PreviewDraw(project.Id, project.Draw!.Result.Settings, Data.Workflow.CurrentSession.Workspace.Revision);
            }
            Window = new(Data.Workflow, new RecentWorkspaceStore(Path.Combine(Data.DirectoryPath, "pending-flyout.json")))
                { Width = 960, Height = 680 };
            Window.Show(); Shell = Assert.IsType<AppShellViewModel>(Window.DataContext);
            Assert.True(Shell.Navigate(WorkspaceRoute.PublicDraw)); Layout();
            Page = Assert.IsType<PublicDrawPageViewModel>(Shell.CurrentPage);
            var view = Assert.Single(Window.GetVisualDescendants().OfType<PublicDrawPage>());
            Summary = view.FindControl<Button>("PendingDrawSummaryButton")!;
            Assert.NotNull(Summary);
            Flyout = Assert.IsType<Flyout>(Summary.Flyout);
        }

        internal void OpenByInvoke() { Invoke(Summary); Layout(); Assert.True(Flyout.IsOpen); }
        internal void OpenByPointer()
        {
            var point = Summary.TranslatePoint(new Point(Summary.Bounds.Width / 2, Summary.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            Layout(); Assert.True(Flyout.IsOpen);
        }
        internal void Layout() { Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); }
        public void Dispose() { Window.Close(); Data.Dispose(); }
    }

    public void Dispose() => ui.Dispose();
}
