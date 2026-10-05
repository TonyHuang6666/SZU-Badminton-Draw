using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class HeaderUtilityWindowTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HeaderUtilitiesFitTheSmallestWindowInBothThemes(bool dark) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture();
        var window = new AppShellWindow(fixture.Workflow,
            new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "utility-recent.json")))
        { Width = 960, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var previousRight = window.FindControl<Image>("HeaderLogo")!.TranslatePoint(default, window)!.Value.X + 48;
            foreach (var name in new[] { "HeaderHelpButton", "HeaderTemplatesButton", "HeaderThemeButton", "HeaderRecoveryButton", "HeaderMoreButton" })
            {
                var button = window.FindControl<Button>(name);
                Assert.NotNull(button);
                Assert.True(button.IsEffectivelyVisible);
                var point = button.TranslatePoint(default, window)!.Value;
                Assert.True(point.X >= previousRight && point.X + button.Bounds.Width <= window.Bounds.Width,
                    $"{name} overlaps another header action or extends outside the window.");
                Assert.True(button.Bounds.Width >= 40 && button.Bounds.Height >= 36);
                previousRight = point.X + button.Bounds.Width;
            }
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData("HeaderHelpButton", "HelpWindow")]
    [InlineData("HeaderTemplatesButton", "TemplateExportWindow")]
    public Task UtilitiesOpenWithoutATournamentAndReuseTheirWindow(string buttonName, string windowType) => ui.Dispatch(() =>
    {
        var directory = Directory.CreateTempSubdirectory("header-utilities-");
        var workflow = new TournamentWorkspaceWorkflow();
        var window = new AppShellWindow(workflow, new RecentWorkspaceStore(Path.Combine(directory.FullName, "recent.json")));
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            var page = shell.CurrentPage;
            var button = window.FindControl<Button>(buttonName);
            Assert.NotNull(button);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var child = Assert.Single(window.OwnedWindows);
            Assert.Equal(windowType, child.GetType().Name);
            Assert.True(child.IsVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(child, Assert.Single(window.OwnedWindows));
            Assert.Same(page, shell.CurrentPage);
            Assert.Null(workflow.CurrentSession);
            child.Close();
            Assert.Empty(window.OwnedWindows);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var reopened = Assert.Single(window.OwnedWindows);
            Assert.NotSame(child, reopened);
            window.Close();
            Assert.False(reopened.IsVisible);
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.szbd", SearchOption.AllDirectories));
        }
        finally { window.Close(); directory.Delete(true); }
    }, CancellationToken.None);

    [Fact]
    public Task OpeningBothUtilitiesLeavesTheCurrentTournamentAndPageUntouched() => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture();
        var window = new AppShellWindow(fixture.Workflow,
            new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "utility-workspace.json")));
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            var page = shell.CurrentPage;
            var session = shell.CurrentSession;
            var savedBytes = File.ReadAllBytes(session!.WorkspacePath);
            foreach (var name in new[] { "HeaderHelpButton", "HeaderTemplatesButton" })
            {
                var button = window.FindControl<Button>(name);
                Assert.NotNull(button);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Assert.Equal(2, window.OwnedWindows.Count);
            Assert.Same(page, shell.CurrentPage);
            Assert.Same(session, shell.CurrentSession);
            Assert.Equal(savedBytes, File.ReadAllBytes(session.WorkspacePath));
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
