using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.ViewModels;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class ShellPresentationCompositionTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MoreMenuOpensFromWizardAndRetainsShellCommands(bool useAutomation) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture();
        var window = new AppShellWindow(fixture.Workflow,
            new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "shell-presentation-recent.json")));
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.NewCommand.Execute(null);
            Assert.IsType<NewWorkspaceWizardViewModel>(shell.CurrentPage);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                item => AutomationProperties.GetName(item) == "更多赛事操作");
            var menu = Assert.IsType<MenuFlyout>(button.Flyout);
            Assert.True(button.IsEffectivelyEnabled); Assert.False(menu.IsOpen);
            if (useAutomation)
                Assert.IsAssignableFrom<IInvokeProvider>(ControlAutomationPeer.CreatePeerForElement(button)).Invoke();
            else
            {
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point, RawInputModifiers.None);
                window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            }
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(menu.IsOpen, $"More menu did not open using {(useAutomation ? "automation invoke" : "pointer click")}");
            var items = menu.Items.OfType<MenuItem>().ToArray();
            var home = Assert.Single(items, item => Equals(item.Header, "返回首页"));
            var create = Assert.Single(items, item => Equals(item.Header, "创建新比赛"));
            var open = Assert.Single(items, item => Equals(item.Header, "打开已有比赛"));
            var reload = Assert.Single(items, item => Equals(item.Header, "重新读取赛事文件"));
            Assert.Same(shell, home.DataContext);
            Assert.Same(shell.HomeCommand, home.Command);
            Assert.Same(shell.NewCommand, create.Command);
            Assert.Same(shell.OpenCommand, open.Command);
            Assert.Same(shell.ReloadCommand, reload.Command);
            Assert.True(home.Command!.CanExecute(null));
            home.Command.Execute(null);
            Assert.Same(shell.StartPage, shell.CurrentPage);
            menu.Hide();
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
