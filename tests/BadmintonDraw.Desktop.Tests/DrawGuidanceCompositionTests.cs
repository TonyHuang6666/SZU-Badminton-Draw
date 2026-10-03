using Avalonia.Controls;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class DrawGuidanceCompositionTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Fact]
    public Task SeedEditorUpdatesNextActionAndBlocksBothActualNavigationButtons() => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(2);
        foreach (var project in fixture.Workflow.CurrentSession!.Workspace.Projects)
            fixture.Workflow.ReopenDraw(project.Id, "核对种子", fixture.Workflow.CurrentSession.Workspace.Revision);
        var window = new AppShellWindow(fixture.Workflow,
            new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "seed-navigation-recent.json")));
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.True(shell.Navigate(WorkspaceRoute.Rosters));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var page = Assert.IsType<RostersPageViewModel>(shell.CurrentPage);
            var first = page.Projects[0];
            Button FindButton(object command) => Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, command));
            void Invoke(Button button)
            {
                Assert.True(button.IsEffectivelyVisible);
                Assert.True(button.IsEffectivelyEnabled);
                Assert.IsAssignableFrom<IInvokeProvider>(ControlAutomationPeer.CreatePeerForElement(button)).Invoke();
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            }
            var originalLabel = FindButton(page.NextCommand).Content;
            Invoke(FindButton(first.BeginSeedEditCommand));
            first.Rows[0].IsSeed = true; first.Rows[0].SeedRankText = "1";
            page.SelectedProject = page.Projects[1];
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.NotEqual(originalLabel, FindButton(page.NextCommand).Content);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible &&
                text.Text?.Contains(first.DisplayLabel) == true && text.Text.Contains("保存") && text.Text.Contains("取消"));

            Invoke(FindButton(page.NextCommand));
            Assert.Same(page, shell.CurrentPage);
            Assert.Same(first, page.SelectedProject);
            Invoke(FindButton(shell.NavigationItems.Single(item => item.Route == WorkspaceRoute.PublicDraw).Command));
            Assert.Same(page, shell.CurrentPage);
            Assert.Equal("1", first.Rows[0].SeedRankText);
            var saved = fixture.Workflow.CurrentSession;
            Invoke(FindButton(first.CancelSeedEditCommand));
            Assert.Equal(originalLabel, FindButton(page.NextCommand).Content);
            Invoke(FindButton(page.NextCommand));
            Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
            Assert.Same(saved, fixture.Workflow.CurrentSession);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task SeedEditorOnlyOffersReloadAfterAConflictingUpdateWithoutSavingDrafts() => ui.Dispatch(async () =>
    {
        using var fixture = new ScheduleUiFixture();
        var projectId = fixture.Workflow.CurrentSession!.Workspace.Projects[0].Id;
        fixture.Workflow.ReopenDraw(projectId, "重新核对种子", fixture.Workflow.CurrentSession.Workspace.Revision);
        var window = new AppShellWindow(fixture.Workflow,
            new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "seed-guidance-recent.json")));
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.True(shell.Navigate(WorkspaceRoute.Rosters));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var page = Assert.IsType<RostersPageViewModel>(shell.CurrentPage);
            var project = page.SelectedProject!;
            Button FindButton(object command) => Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, command));
            project.BeginSeedEditCommand.Execute(null);
            project.Rows[0].IsSeed = true; project.Rows[0].SeedRankText = "1";
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(FindButton(project.SaveSeedsCommand).IsEffectivelyVisible);
            Assert.True(FindButton(project.CancelSeedEditCommand).IsEffectivelyVisible);
            Assert.False(FindButton(project.ResetSeedsCommand).IsEffectivelyVisible);
            Assert.False(project.ResetSeedsCommand.CanExecute(null));

            var other = new TournamentWorkspaceWorkflow();
            other.OpenWorkspace(fixture.Workflow.CurrentSession.WorkspacePath);
            other.UpdateRosterSeeds(projectId, [new(0, true, 2), new(1, false, null)], other.CurrentSession!.Workspace.Revision);
            await shell.ReloadCommand.ExecuteAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(project.HasEditorConflict);
            Assert.Equal("1", project.Rows[0].SeedRankText);
            Assert.False(FindButton(project.SaveSeedsCommand).IsEffectivelyEnabled);
            var reload = FindButton(project.ResetSeedsCommand);
            Assert.True(reload.IsEffectivelyVisible);
            Assert.True(reload.IsEffectivelyEnabled);
            var saved = fixture.Workflow.CurrentSession;

            project.ResetSeedsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(project.IsSeedEditing);
            Assert.False(project.HasEditorConflict);
            Assert.Equal("2", project.Rows[0].SeedRankText);
            Assert.False(FindButton(project.ResetSeedsCommand).IsEffectivelyVisible);
            Assert.False(project.ResetSeedsCommand.CanExecute(null));
            Assert.True(FindButton(project.SaveSeedsCommand).IsEffectivelyEnabled);
            Assert.Same(saved, fixture.Workflow.CurrentSession);

            project.Rows[0].SeedRankText = "9";
            project.CancelSeedEditCommand.Execute(null);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.False(project.IsSeedEditing);
            Assert.Equal("2", project.Rows[0].SeedRankText);
            Assert.False(FindButton(project.SaveSeedsCommand).IsEffectivelyVisible);
            Assert.False(FindButton(project.ResetSeedsCommand).IsEffectivelyVisible);
            Assert.Same(saved, fixture.Workflow.CurrentSession);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task PrimaryActionTracksSavedPreviewAndConfirmationWithoutLeavingTheDrawPage() => ui.Dispatch(async () =>
    {
        using var fixture = new ScheduleUiFixture();
        var projectId = fixture.Workflow.CurrentSession!.Workspace.Projects[0].Id;
        fixture.Workflow.ReopenDraw(projectId, "重新检查", fixture.Workflow.CurrentSession.Workspace.Revision);
        var window = new AppShellWindow(fixture.Workflow,
            new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "guidance-recent.json")));
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.True(shell.Navigate(WorkspaceRoute.PublicDraw));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var view = Assert.Single(window.GetVisualDescendants().OfType<PublicDrawPage>());
            var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
            Button Button(string name) => Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Name == name);
            var preview = Button("ProjectDrawPrimaryPreview");
            var confirm = Button("ProjectDrawPrimaryConfirm");
            var export = Button("DrawExportButton");
            Assert.True(preview.IsEffectivelyVisible); Assert.False(confirm.IsEffectivelyVisible); Assert.False(export.IsEffectivelyVisible);

            var advanced = Assert.Single(view.GetVisualDescendants().OfType<Expander>(), e => e.Name == "DrawRandomSettings");
            Assert.False(advanced.IsExpanded);
            advanced.IsExpanded = true; window.UpdateLayout();
            var seed = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(), t => t.Name == "DrawRandomSeed");
            seed.Text = "公开抽签-设置保留";
            advanced.IsExpanded = false; window.UpdateLayout(); advanced.IsExpanded = true; window.UpdateLayout();
            Assert.Equal("公开抽签-设置保留", seed.Text);
            Assert.Equal("公开抽签-设置保留", page.SelectedProject!.RandomSeed);

            await Assert.IsType<AsyncCommand>(preview.Command).ExecuteAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Same(page, shell.CurrentPage);
            Assert.Null(shell.LastError);
            Assert.True(page.SelectedProject.HasPreview, $"Preview missing; status={shell.Status}; editable={page.SelectedProject.CanEditSettings}");
            Assert.False(page.SelectedProject.ShowPreviewAction, $"ViewModel still requests preview; hint={page.SelectedProject.EditHint}");
            // Saving refreshes the selector items and recreates the selected project's visual template.
            preview = Button("ProjectDrawPrimaryPreview"); confirm = Button("ProjectDrawPrimaryConfirm"); export = Button("DrawExportButton");
            Assert.False(preview.IsEffectivelyVisible); Assert.True(confirm.IsEffectivelyVisible); Assert.True(export.IsEffectivelyVisible);
            Assert.Contains("secondary", export.Classes);
            page.SelectedProject.RandomSeed = "已修改但尚未重新抽签"; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(preview.IsEffectivelyVisible); Assert.False(confirm.IsEffectivelyVisible);
            await Assert.IsType<AsyncCommand>(preview.Command).ExecuteAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            confirm = Button("ProjectDrawPrimaryConfirm");
            await Assert.IsType<AsyncCommand>(confirm.Command).ExecuteAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Same(page, shell.CurrentPage);
            preview = Button("ProjectDrawPrimaryPreview"); confirm = Button("ProjectDrawPrimaryConfirm"); export = Button("DrawExportButton");
            Assert.False(preview.IsEffectivelyVisible); Assert.False(confirm.IsEffectivelyVisible); Assert.True(export.IsEffectivelyVisible);
            Assert.Null(fixture.Workflow.CurrentSession!.Workspace.Schedule);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
