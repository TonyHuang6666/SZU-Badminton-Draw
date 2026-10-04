using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class DrawSchedulingGuidanceTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(960, 680)]
    [InlineData(1280, 880)]
    public Task UnfinishedDrawsRemainActionableFromTheBottomWithoutChangingTournament(int width, int height) => ui.Dispatch(async () =>
    {
        using var f = new ScheduleUiFixture(3);
        var savedProjects = f.Workflow.CurrentSession!.Workspace.Projects;
        f.Workflow.ReopenDraw(savedProjects[1].Id, "重新检查", f.Workflow.CurrentSession.Workspace.Revision);
        f.Workflow.PreviewDraw(savedProjects[1].Id, savedProjects[1].Draw!.Result.Settings, f.Workflow.CurrentSession.Workspace.Revision);
        f.Workflow.ReopenDraw(savedProjects[2].Id, "重新检查", f.Workflow.CurrentSession.Workspace.Revision);
        var window = new AppShellWindow(f.Workflow, new RecentWorkspaceStore(Path.Combine(f.DirectoryPath, "guidance.json")))
            { Width = width, Height = height };
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.PublicDraw);
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
            var view = Assert.Single(window.GetVisualDescendants().OfType<PublicDrawPage>());
            var next = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, page.ContinueToScheduleCommand));
            Assert.True(next.IsEffectivelyVisible);
            Assert.False(next.IsEffectivelyEnabled);
            var footer = view.FindControl<Border>("DrawActionBar")!;
            var scroll = view.FindControl<ScrollViewer>("DrawContent")!;
            scroll.Offset = new Vector(0, scroll.Extent.Height); Layout();
            Assert.True(scroll.Offset.Y > 0);
            var summary = view.FindControl<Button>("PendingDrawSummaryButton");
            Assert.NotNull(summary);
            Assert.True(summary.IsEffectivelyVisible);
            Assert.Contains("2", summary.Content!.ToString());
            var flyout = Assert.IsType<Flyout>(summary.Flyout);
            Assert.False(flyout.IsOpen);
            var compactHeight = footer.Bounds.Height;
            if (width == 1280) Assert.True(compactHeight < 100, $"Footer should remain one compact row, not {compactHeight}px high.");
            Assert.DoesNotContain(footer.GetVisualDescendants().OfType<Button>(), b => b.Name == "ReviewPendingDrawProject");
            void OpenList()
            {
                Assert.IsAssignableFrom<IInvokeProvider>(ControlAutomationPeer.CreatePeerForElement(summary)).Invoke(); Layout();
                Assert.True(flyout.IsOpen);
                Assert.Equal(compactHeight, footer.Bounds.Height);
            }
            OpenList();
            var list = Assert.IsAssignableFrom<Control>(flyout.Content);
            Button[] Pending() => list.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "ReviewPendingDrawProject" && b.IsEffectivelyVisible).ToArray();
            var pending = Pending();
            Assert.Equal(2, pending.Length);
            Assert.DoesNotContain(pending, b => ReferenceEquals(b.DataContext, page.Projects[0]));
            var needsPreview = Assert.Single(pending, b => ReferenceEquals(b.DataContext, page.Projects[2]));
            Assert.Contains(needsPreview.GetVisualDescendants().OfType<TextBlock>(), b => b.Text?.Contains("未抽签") == true);
            var needsConfirm = Assert.Single(pending, b => ReferenceEquals(b.DataContext, page.Projects[1]));
            Assert.Contains(needsConfirm.GetVisualDescendants().OfType<TextBlock>(), b => b.Text?.Contains("待确认") == true);
            var before = f.Workflow.CurrentSession;
            var bytes = File.ReadAllBytes(before.WorkspacePath);

            Assert.IsAssignableFrom<IInvokeProvider>(ControlAutomationPeer.CreatePeerForElement(needsPreview)).Invoke(); Layout();

            Assert.Same(page.Projects[2], page.SelectedProject);
            Assert.False(flyout.IsOpen);
            Assert.Equal(0, scroll.Offset.Y);
            Assert.Same(before, f.Workflow.CurrentSession);
            Assert.Equal(bytes, File.ReadAllBytes(before.WorkspacePath));
            Assert.Null(page.Session.Workspace.Projects[2].Draw);
            var footerBounds = footer.TranslatePoint(default, window)!.Value;
            Assert.True(footerBounds.Y >= 0 && footerBounds.Y + footer.Bounds.Height <= window.ClientSize.Height);
            foreach (var button in footer.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
            {
                var position = button.TranslatePoint(default, footer)!.Value;
                Assert.True(position.X >= 0 && position.X + button.Bounds.Width <= footer.Bounds.Width + 1,
                    $"Action button should fit the {width}px window.");
            }

            await page.SelectedProject!.PreviewDrawCommand.ExecuteAsync();
            await page.SelectedProject!.ConfirmDrawCommand.ExecuteAsync(); Layout();
            Assert.Contains("1", summary.Content!.ToString()); Assert.False(next.IsEffectivelyEnabled);
            OpenList(); Assert.Single(Pending());
            Assert.IsAssignableFrom<IInvokeProvider>(ControlAutomationPeer.CreatePeerForElement(Pending()[0])).Invoke(); Layout();
            Assert.Same(page.Projects[1], page.SelectedProject);
            await page.SelectedProject!.ConfirmDrawCommand.ExecuteAsync(); Layout();
            Assert.False(flyout.IsOpen); Assert.False(summary.IsEffectivelyVisible);
            Assert.True(next.IsEffectivelyVisible); Assert.True(next.IsEffectivelyEnabled);
            Assert.DoesNotContain(f.Workflow.CurrentSession!.Workspace.AuditEvents, e => e.Action == "DrawPackageExported");
            await page.ContinueToScheduleCommand.ExecuteAsync(); Layout();
            Assert.IsType<ScheduleSetupPageViewModel>(shell.CurrentPage);
            Assert.Null(f.Workflow.CurrentSession.Workspace.Schedule);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
