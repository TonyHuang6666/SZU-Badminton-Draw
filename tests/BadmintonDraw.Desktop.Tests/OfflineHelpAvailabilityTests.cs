using Avalonia.Controls;
using Avalonia.Headless;
using BadmintonDraw.Desktop.Views;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class OfflineHelpAvailabilityTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Fact]
    public Task HelpCanOpenWithoutNetworkOrTournament() => ui.Dispatch(() =>
    {
        var window = new HelpWindow();
        try
        {
            window.Show();
            Assert.NotNull(window.DataContext);
            Assert.NotNull(window.FindControl<Control>("HelpDocument"));
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
