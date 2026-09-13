using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class WorkspaceRecoveryPanelTests : IDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
    [Fact]
    public Task RealWindowHeaderAndStartOpenTheSameUnboundToSessionPanel() => session.Dispatch(() =>
    {
        using var fixture = new RecoveryUiFixture();
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(fixture.PathFor("window-recent.json")));
        try
        {
            window.Show(); window.UpdateLayout(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.Null(shell.CurrentSession); Assert.False(shell.CanNavigate(WorkspaceRoute.Operations));
            var panel = window.FindControl<WorkspaceRecoveryPanel>("RecoveryPanel")!;
            Assert.Same(shell.Recovery, panel.DataContext); Assert.False(panel.IsVisible);
            var start = Assert.Single(window.GetVisualDescendants().OfType<StartPage>());
            var startButton = start.FindControl<Button>("StartRecoveryButton")!;
            Assert.Same(shell.OpenRecoveryCommand, startButton.Command); Activate(window, startButton);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Assert.True(panel.IsVisible);
            var close = panel.FindControl<Button>("CloseRecoveryButton")!;
            Assert.Same(shell.Recovery.CloseCommand, close.Command);
            Activate(window, close);
            Assert.False(shell.Recovery.IsOpen, $"Close button enabled={close.IsEnabled}, focused={close.IsFocused}; status={shell.Status}");
            Assert.False(panel.IsVisible);
            Activate(window, window.FindControl<Button>("HeaderRecoveryButton")!);
            Assert.True(panel.IsVisible); Assert.False(panel.FindControl<Button>("RestoreBackupButton")!.IsEffectivelyEnabled);
            var input = panel.FindControl<TextBox>("RecoveryReason")!; input.Text = "核对原因";
            Assert.Equal("核对原因", shell.Recovery.Reason);
            Assert.False(panel.FindControl<CheckBox>("RecoveryConfirmation")!.IsChecked);
        }
        finally { window.Close(); }
    }, CancellationToken.None);
    [Fact]
    public Task RealWindowFailedOpenPreviewAndExplicitApplyUseBoundControlsAndBackgroundStore() => session.Dispatch(async () =>
    {
        using var fixture = new RecoveryUiFixture(); var (target, backup) = fixture.CorruptTarget();
        var longBackup = fixture.PathFor(new string('备', 70) + ".szbd"); File.Copy(backup, longBackup);
        var reads = new System.Collections.Concurrent.ConcurrentBag<bool>();
        fixture.Store.BeforeRead = _ => reads.Add(Dispatcher.UIThread.CheckAccess());
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(fixture.PathFor("window-recent.json")),
            openPicker: () => Task.FromResult<string?>(target), recoveryBackupPicker: () => Task.FromResult<string?>(longBackup))
            { Width = 880, Height = 620 };
        try
        {
            window.Show(); window.UpdateLayout(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            await shell.OpenCommand.ExecuteAsync(); Assert.Null(shell.CurrentSession);
            Activate(window, window.FindControl<Button>("HeaderRecoveryButton")!);
            var panel = window.FindControl<WorkspaceRecoveryPanel>("RecoveryPanel")!;
            Assert.True(panel.IsVisible); Assert.Equal(target, panel.FindControl<TextBox>("RecoveryTargetPath")!.Text);
            await shell.Recovery.PickBackupCommand.ExecuteAsync();
            Assert.Equal(longBackup, panel.FindControl<TextBox>("RecoveryBackupPath")!.Text);
            await Assert.IsType<AsyncCommand>(panel.FindControl<Button>("PreviewRecoveryButton")!.Command).ExecuteAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var details = panel.FindControl<SelectableTextBlock>("RecoveryPreviewDetails")!;
            Assert.Contains(longBackup, details.Text); Assert.Contains(RecoveryUiFixture.Hash(longBackup), details.Text);
            Assert.Equal(TextWrapping.Wrap, details.TextWrapping); Assert.True(panel.Bounds.Width <= window.ClientSize.Width);
            var confirmation = panel.FindControl<CheckBox>("RecoveryConfirmation")!;
            var apply = panel.FindControl<Button>("RestoreBackupButton")!;
            Assert.True(confirmation.IsEnabled); Assert.False(confirmation.IsChecked); Assert.False(apply.IsEffectivelyEnabled);
            panel.FindControl<TextBox>("RecoveryReason")!.Text = "本人核对了完整路径、赛事身份及修订";
            confirmation.IsChecked = true; Assert.True(apply.IsEffectivelyEnabled);
            await Assert.IsType<AsyncCommand>(apply.Command).ExecuteAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Null(shell.LastError); Assert.Equal(target, shell.WorkspacePath);
            Assert.False(apply.IsEffectivelyEnabled); Assert.False(confirmation.IsChecked);
            Assert.Contains(target, panel.FindControl<SelectableTextBlock>("RecoveryOutputDetails")!.Text);
            Assert.NotEmpty(reads); Assert.DoesNotContain(true, reads);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);
    private static void Activate(Window window, Button button)
    {
        Assert.True(button.Focus());
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
    }
    public void Dispose() => session.Dispose();
}
