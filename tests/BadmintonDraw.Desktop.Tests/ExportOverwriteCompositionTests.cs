using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Tests;
using BadmintonDraw.Workflows;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class ExportOverwriteCompositionTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task RealWindowConfirmsExactFilesAtExportTime(bool operational, bool replace) => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1, 2);
        var output = data.PathFor("window-output");
        var revision = data.Workflow.CurrentSession!.Workspace.Revision;
        var targets = operational
            ? data.Workflow.ExportOperationalPackage(new(output), revision).Outputs.Select(item => item.Path).ToArray()
            : data.Workflow.ExportDrawPackage(null, new(output, WorkflowExportFormat.Excel), revision).Outputs.Select(item => item.Path).ToArray();
        var before = data.Workflow.CurrentSession!;
        var originalFiles = targets.Select(File.ReadAllBytes).ToArray();
        var archive = File.ReadAllBytes(before.WorkspacePath);
        var window = new AppShellWindow(data.Workflow, new RecentWorkspaceStore(data.PathFor("recent-window.json")));
        Task? pending = null;
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            if (operational)
            {
                Assert.True(shell.Navigate(WorkspaceRoute.Operations));
                var page = Assert.IsType<OperationsPageViewModel>(shell.CurrentPage);
                page.Materials.OutputDirectory = output;
                page.Materials.ScopeConfirmed = true;
                pending = page.Materials.ExportCommand.ExecuteAsync();
            }
            else
            {
                shell.RegisterPageFactory(WorkspaceRoute.PublicDraw, session => new PublicDrawPageViewModel(shell, session,
                    () => Task.FromResult<string?>(output)));
                Assert.True(shell.Navigate(WorkspaceRoute.PublicDraw));
                pending = Assert.IsType<PublicDrawPageViewModel>(shell.CurrentPage).SelectedProject!.ExportConfirmedCommand.ExecuteAsync();
            }
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!window.OwnedWindows.OfType<ExportOverwriteDialog>().Any() && !pending.IsCompleted && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            var dialog = Assert.Single(window.OwnedWindows.OfType<ExportOverwriteDialog>());
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); dialog.UpdateLayout();
            Assert.Equal(targets.Order(), dialog.GetVisualDescendants().OfType<SelectableTextBlock>().Select(block => block.Text!).Order());
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<CheckBox>(), box => box.Name == "OverwriteOperationalOutputs");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.Contains("允许下一次导出") == true);
            Assert.Equal(archive, File.ReadAllBytes(before.WorkspacePath));
            Assert.Equal(originalFiles, targets.Select(File.ReadAllBytes).ToArray());
            dialog.FindControl<Button>(replace ? "ReplaceButton" : "CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending; Dispatcher.UIThread.RunJobs();
            Assert.Null(shell.LastError);
            Assert.Equal(before.Workspace.Revision + (replace ? 1 : 0), data.Workflow.CurrentSession!.Workspace.Revision);
            if (!replace)
            {
                Assert.Equal(archive, File.ReadAllBytes(before.WorkspacePath));
                Assert.Equal(originalFiles, targets.Select(File.ReadAllBytes).ToArray());
            }
        }
        finally
        {
            foreach (var child in window.OwnedWindows.ToArray()) child.Close();
            if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(20));
            window.Close();
        }
        return 0;
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
