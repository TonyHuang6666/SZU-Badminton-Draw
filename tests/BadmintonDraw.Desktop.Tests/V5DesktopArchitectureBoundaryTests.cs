using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using BadmintonDraw.Desktop;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class V5DesktopArchitectureBoundaryTests : IDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));

    [Fact]
    public void RetiredMainWindowIsAbsent()
    {
        Assert.Null(typeof(AppShellWindow).Assembly.GetType("BadmintonDraw.Desktop.MainWindow"));
    }

    [Fact]
    public void DesktopHasNoDirectExcelOrSqliteReference()
    {
        var references = typeof(AppShellWindow).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name == "BadmintonDraw.Excel");
        Assert.DoesNotContain(references, reference => reference.Name is "Microsoft.Data.Sqlite" ||
                                                     reference.Name!.StartsWith("SQLitePCLRaw", StringComparison.Ordinal));
    }

    [Fact]
    public Task ActualApplicationStartupAssignsOnlyAppShellWindow() => session.Dispatch(() =>
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var app = new App { ApplicationLifetime = lifetime };
        app.Initialize();
        app.OnFrameworkInitializationCompleted();
        try
        {
            Assert.IsType<AppShellWindow>(lifetime.MainWindow);
            Assert.Equal("BadmintonDraw.Desktop.AppShellWindow", lifetime.MainWindow!.GetType().FullName);
        }
        finally
        {
            lifetime.MainWindow?.Close();
        }
        return 0;
    }, CancellationToken.None);

    public void Dispose() => session.Dispose();
}
