using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class BrandThemeContrastTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    // Catch illegible label/background combinations when either theme's shared palette changes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BrandSurfacesAndStatusMessagesKeepReadableText(bool dark) => ui.Dispatch(() =>
    {
        var window = new Window { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show();
            var pairs = new (string Text, string Background)[]
            {
                ("AppText", "AppSurface"), ("AppMutedText", "AppBackground"),
                ("AppTitle", "AppSurface"), ("AppFieldLabel", "AppInputBackground"),
                ("AppAccentText", "AppAccent"), ("AppAccentText", "AppAccentHover"),
                ("AppHeroButtonText", "AppHeroButtonBackground"), ("AppHeroButtonText", "AppHeroButtonHoverBackground"),
                ("AppButtonText", "AppButtonBackground"), ("AppButtonHoverText", "AppButtonHoverBackground"),
                ("AppButtonHoverText", "AppButtonPressedBackground"),
                ("AppSelectionText", "AppListSelectedBackground"), ("AppSelectionText", "AppListSelectedHoverBackground"),
                ("AppText", "AppBrandSelectedBackground"), ("AppMutedText", "AppBrandSelectedBackground"),
                ("AppSidebarText", "AppSidebarBackground"), ("AppSidebarMutedText", "AppSidebarBackground"),
                ("AppSidebarText", "AppSidebarSelectedBackground"),
                ("AppSidebarMutedText", "AppSidebarSelectedBackground"),
                ("AppSidebarMutedText", "AppSidebarCalloutBackground"),
                ("AppTableHeaderText", "AppTableHeaderBackground"),
                ("AppPurpleText", "AppPurpleCardBackground"),
                ("AppSuccessText", "AppSuccessCardBackground"),
                ("AppWarningText", "AppWarningCardBackground"),
                ("AppErrorText", "AppErrorCardBackground")
            };
            foreach (var pair in pairs)
            {
                var a = Luminance(Brush(pair.Text));
                var b = Luminance(Brush(pair.Background));
                var contrast = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
                Assert.True(contrast >= 4.5, $"{window.ActualThemeVariant}: {pair.Text} on {pair.Background} has {contrast:F2}:1 contrast.");
            }
            Assert.NotEqual(Brush("AppSuccessCardBackground"), Brush("AppBrandSelectedBackground"));
            Assert.NotEqual(Brush("AppSuccessText"), Brush("AppAccent"));

            Color Brush(string key) => Assert.IsAssignableFrom<ISolidColorBrush>(
                window.FindResource(window.ActualThemeVariant, key + "Brush")).Color;
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    public void Dispose() => ui.Dispose();
}
