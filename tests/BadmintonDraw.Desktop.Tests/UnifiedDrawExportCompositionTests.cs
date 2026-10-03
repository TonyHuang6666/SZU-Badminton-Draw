using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class UnifiedDrawExportCompositionTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConfirmedDrawsHaveOneExportEntryWithAllSettingsTogetherAndCancelChangesNothing(bool drawOnly) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(3, purpose: drawOnly ? TournamentPurpose.PublicDrawOnly : TournamentPurpose.FullTournament);
        var saved = fixture.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(saved.WorkspacePath);
        var window = new AppShellWindow(fixture.Workflow,
            new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "unified-export.json")));
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.True(shell.Navigate(WorkspaceRoute.PublicDraw));
            var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
            page.SelectedProject = page.Projects.Last();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var view = Assert.Single(window.GetVisualDescendants().OfType<PublicDrawPage>());
            var export = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible &&
                b.Content?.ToString()?.StartsWith("导出") == true);
            Assert.Equal("导出抽签结果…", export.Content);
            Assert.Contains(drawOnly ? "primary" : "secondary", export.Classes);
            var next = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, page.ContinueToScheduleCommand));
            Assert.True(next.IsEffectivelyEnabled);
            Assert.Contains(drawOnly ? "secondary" : "primary", next.Classes);
            Assert.IsAssignableFrom<IInvokeProvider>(ControlAutomationPeer.CreatePeerForElement(export)).Invoke();
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.Equal("导出抽签结果", dialog.Title);
            dialog.UpdateLayout();
            Assert.Equal(2, dialog.GetVisualDescendants().OfType<ComboBox>().Count());
            Assert.Contains(dialog.GetVisualDescendants().OfType<Expander>(), e => e.Header?.ToString() == "PDF 分页设置");
            dialog.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.OwnedWindows);
            Assert.Same(saved, fixture.Workflow.CurrentSession);
            Assert.Equal(bytes, File.ReadAllBytes(saved.WorkspacePath));
            Assert.Same(page, shell.CurrentPage);
        }
        finally
        {
            foreach (var child in window.OwnedWindows.ToArray()) child.Close();
            window.Close();
        }
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NativeOptionsChooseCurrentScopeThenConfirmOnlyItsExistingFile(bool replace) => ui.Dispatch(async () =>
    {
        using var fixture = new ScheduleUiFixture(2);
        var output = Path.Combine(fixture.DirectoryPath, "exports");
        var targets = fixture.Workflow.ExportDrawPackage(null, new(output, WorkflowExportFormat.Excel),
            fixture.Workflow.CurrentSession!.Workspace.Revision).Outputs.Select(item => item.Path).ToArray();
        var saved = fixture.Workflow.CurrentSession!;
        var archive = File.ReadAllBytes(saved.WorkspacePath);
        var original = targets.ToDictionary(path => path, File.ReadAllBytes);
        var window = new AppShellWindow(fixture.Workflow,
            new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "unified-conflict.json")),
            drawOutputPicker: () => Task.FromResult<string?>(output));
        Task? pending = null;
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.True(shell.Navigate(WorkspaceRoute.PublicDraw));
            var page = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage);
            page.SelectedProject = page.Projects.Last();
            var selectedId = page.SelectedProject.ProjectId.ToString("N");
            pending = page.OpenExportCommand.ExecuteAsync();
            Dispatcher.UIThread.RunJobs();
            var optionsDialog = Assert.Single(window.OwnedWindows.OfType<DrawExportOptionsDialog>());
            var options = Assert.IsType<DrawExportOptionsViewModel>(optionsDialog.DataContext);
            Assert.Null(Assert.IsType<DrawExportScope>(options.SelectedScope).ProjectId);
            optionsDialog.FindControl<ComboBox>("ScopeSelector")!.SelectedItem = options.Scopes.Single(s => s.ProjectId is not null);
            Dispatcher.UIThread.RunJobs();
            optionsDialog.FindControl<Button>("ExportButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!window.OwnedWindows.OfType<ExportOverwriteDialog>().Any() && !pending.IsCompleted && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            var overwrite = Assert.Single(window.OwnedWindows.OfType<ExportOverwriteDialog>());
            Dispatcher.UIThread.RunJobs(); overwrite.UpdateLayout();
            var selectedTarget = targets.Single(path => path.Contains(selectedId));
            Assert.Equal(selectedTarget, Assert.Single(overwrite.GetVisualDescendants().OfType<SelectableTextBlock>()).Text);
            Assert.Equal(archive, File.ReadAllBytes(saved.WorkspacePath));
            overwrite.FindControl<Button>(replace ? "ReplaceButton" : "CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending;
            Assert.Null(shell.LastError);
            Assert.Equal(saved.Workspace.Revision + (replace ? 1 : 0), fixture.Workflow.CurrentSession!.Workspace.Revision);
            foreach (var path in targets.Where(path => !replace || path != selectedTarget))
                Assert.Equal(original[path], File.ReadAllBytes(path));
            if (!replace) Assert.Equal(archive, File.ReadAllBytes(saved.WorkspacePath));
            Assert.Null(fixture.Workflow.CurrentSession.Workspace.Schedule);
        }
        finally
        {
            foreach (var child in window.OwnedWindows.ToArray()) child.Close();
            if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(15));
            window.Close();
        }
        return 0;
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
