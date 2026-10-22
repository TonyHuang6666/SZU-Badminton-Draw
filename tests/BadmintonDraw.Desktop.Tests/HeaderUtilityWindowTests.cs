using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
            foreach (var name in new[] { "HeaderHelpButton", "HeaderTemplatesButton", "HeaderContactButton", "HeaderThemeButton", "HeaderRecoveryButton", "HeaderMoreButton" })
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
    [InlineData("HeaderContactButton", "ContactWindow")]
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
    public Task OpeningAllUtilitiesLeavesTheCurrentTournamentAndPageUntouched() => ui.Dispatch(() =>
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
            foreach (var name in new[] { "HeaderHelpButton", "HeaderTemplatesButton", "HeaderContactButton" })
            {
                var button = window.FindControl<Button>(name);
                Assert.NotNull(button);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Assert.Equal(3, window.OwnedWindows.Count);
            Assert.Same(page, shell.CurrentPage);
            Assert.Same(session, shell.CurrentSession);
            Assert.Equal(savedBytes, File.ReadAllBytes(session.WorkspacePath));
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ContactWindowShowsBothLabeledQrCodesAtReadableSize(bool dark) => ui.Dispatch(() =>
    {
        var directory = Directory.CreateTempSubdirectory("contact-window-");
        var window = new AppShellWindow(new TournamentWorkspaceWorkflow(),
            new RecentWorkspaceStore(Path.Combine(directory.FullName, "recent.json")))
        { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show();
            window.FindControl<Button>("HeaderContactButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var contact = Assert.Single(window.OwnedWindows);
            contact.Width = 700;
            contact.Height = 520;
            contact.UpdateLayout();
            Assert.Contains(contact.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "深大羽协官方微信");
            Assert.Contains(contact.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "程序作者 Tony Huang 的微信");
            var association = contact.FindControl<Image>("ContactAssociationQr")!;
            var author = contact.FindControl<Image>("ContactAuthorQr")!;
            Assert.NotNull(association);
            Assert.NotNull(author);
            Assert.True(association.IsEffectivelyVisible && author.IsEffectivelyVisible);
            Assert.True(association.Bounds.Width >= 240 && author.Bounds.Width >= 240);
            var associationLeft = association.TranslatePoint(default, contact)!.Value.X;
            var authorLeft = author.TranslatePoint(default, contact)!.Value.X;
            Assert.True(associationLeft >= 0 && associationLeft + association.Bounds.Width < authorLeft);
            Assert.True(authorLeft + author.Bounds.Width <= contact.Bounds.Width);
            using var associationStream = AssetLoader.Open(new Uri("avares://BadmintonDraw.Desktop/Assets/contact-szuba-wechat.jpg"));
            using var authorStream = AssetLoader.Open(new Uri("avares://BadmintonDraw.Desktop/Assets/contact-tony-wechat.jpg"));
            Assert.True(associationStream.Length > 60_000);
            Assert.True(authorStream.Length > 100_000);
            Assert.IsAssignableFrom<Bitmap>(association.Source);
            Assert.IsAssignableFrom<Bitmap>(author.Source);
            Assert.NotSame(association.Source, author.Source);
        }
        finally { window.Close(); directory.Delete(true); }
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
