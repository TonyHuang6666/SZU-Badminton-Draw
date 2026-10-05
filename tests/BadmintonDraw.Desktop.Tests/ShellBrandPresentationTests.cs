using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.ViewModels;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class ShellBrandPresentationTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HeaderKeepsOriginalLogoLegibleWithoutClippingItsActions(bool dark) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture();
        var window = new AppShellWindow(fixture.Workflow,
            new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "brand-logo-recent.json")))
        { Width = 960, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var logo = Assert.Single(window.GetVisualDescendants().OfType<Image>());
            Assert.IsAssignableFrom<Bitmap>(logo.Source);
            Assert.True(logo.Bounds.Width >= 47 && logo.Bounds.Height >= 47,
                $"The source logo is reduced to {logo.Bounds.Size}, making the ring lettering unreadable.");
            Assert.Equal(Stretch.Uniform, logo.Stretch);
            Assert.Equal(BitmapInterpolationMode.HighQuality, RenderOptions.GetBitmapInterpolationMode(logo));
            var recovery = window.FindControl<Button>("HeaderRecoveryButton")!;
            var position = recovery.TranslatePoint(default, window)!.Value;
            Assert.True(recovery.IsEffectivelyVisible);
            Assert.True(position.X >= 0 && position.X + recovery.Bounds.Width <= window.Bounds.Width);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OperationStatusKeepsSemanticColorsWhenBrandThemeChanges(bool dark) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture();
        var window = new AppShellWindow(fixture.Workflow,
            new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "brand-status-recent.json")))
        { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var symbol = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                text => ReferenceEquals(text.DataContext, shell) && text.Text == shell.StatusSymbol);
            AssertColor("AppSuccessTextBrush");
            shell.ReportError(new InvalidOperationException("测试操作失败"));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal("!", symbol.Text);
            AssertColor("AppErrorTextBrush");
            shell.HomeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal("✓", symbol.Text);
            AssertColor("AppSuccessTextBrush");

            void AssertColor(string key) => Assert.Equal(
                Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource(window.ActualThemeVariant, key)).Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(symbol.Foreground).Color);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
