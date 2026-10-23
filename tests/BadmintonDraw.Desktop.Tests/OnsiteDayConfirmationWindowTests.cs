using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Tests;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class OnsiteDayConfirmationWindowTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Fact]
    public Task DialogProvidesPreviewConfirmationAndScrollableContent() => ui.Dispatch(() =>
    {
        var window = new OnsiteDayConfirmationWindow();
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.Equal("确认当天比赛结果", window.Title);
            Assert.Equal("预览确认与延期", window.FindControl<Button>("DayPreview")!.Content);
            Assert.Equal("确认当天比赛结果", window.FindControl<Button>("DayConfirm")!.Content);
            Assert.NotNull(window.FindControl<ScrollViewer>("DayConfirmationScroll"));
            Assert.NotNull(window.FindControl<ItemsControl>("DayPendingMatches"));
            Assert.NotNull(window.FindControl<ItemsControl>("DayChanges"));
            Assert.True(window.MinWidth >= 750); Assert.True(window.MinHeight >= 500);
        }
        finally { window.Close(); }
        return Task.FromResult(0);
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();

    [Fact]
    public Task PendingRowsRequireConsentAndReasonAndShowFullPreview() => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1);
        var owner = new AppShellWindow(data.Workflow, new RecentWorkspaceStore(data.PathFor("day-dialog.json")));
        OnsiteRecordWindow? sheet = null;
        OnsiteDayConfirmationWindow? dialog = null;
        try
        {
            owner.Show(); var shell = (AppShellViewModel)owner.DataContext!;
            Assert.True(shell.Navigate(WorkspaceRoute.Operations));
            Assert.IsType<OperationsPageViewModel>(shell.CurrentPage).OpenOnsiteRecordsCommand.Execute(null);
            sheet = Assert.Single(owner.OwnedWindows.OfType<OnsiteRecordWindow>());
            var records = Assert.IsType<OnsiteRecordViewModel>(sheet.DataContext);
            var confirmation = Assert.IsType<OnsiteDayConfirmationViewModel>(await records.PrepareDayConfirmationAsync());
            Assert.NotEmpty(confirmation.PendingMatches);
            dialog = new OnsiteDayConfirmationWindow { DataContext = confirmation };
            var modal = dialog.ShowDialog(sheet); Layout(dialog);
            var previewButton = dialog.FindControl<Button>("DayPreview")!;
            var confirmButton = dialog.FindControl<Button>("DayConfirm")!;
            Assert.False(previewButton.IsEnabled); Assert.False(confirmButton.IsEnabled);
            var pending = dialog.FindControl<ItemsControl>("DayPendingMatches")!;
            foreach (var row in confirmation.PendingMatches)
            {
                var consent = Assert.Single(pending.GetVisualDescendants().OfType<CheckBox>(),
                    box => ReferenceEquals(box.DataContext, row));
                Assert.Equal("经裁判长同意延期至下一比赛日", consent.Content);
                consent.IsChecked = true; Layout(dialog);
            }
            Assert.False(previewButton.IsEnabled);
            foreach (var row in confirmation.PendingMatches)
                Assert.Single(pending.GetVisualDescendants().OfType<TextBox>(),
                    box => ReferenceEquals(box.DataContext, row)).Text = "裁判长批准，场地临时停用";
            Layout(dialog); Assert.True(previewButton.IsEnabled);
            await confirmation.PreviewAsync(); Layout(dialog);
            Assert.True(confirmation.HasPreview); Assert.True(confirmButton.IsEnabled);
            Assert.NotEmpty(confirmation.Changes);
            var changes = dialog.FindControl<ItemsControl>("DayChanges")!;
            Assert.True(changes.IsEffectivelyVisible);
            Assert.Equal(confirmation.Changes.Count, changes.ItemCount);
            foreach (var change in confirmation.Changes)
            {
                var rendered = changes.GetVisualDescendants().OfType<SelectableTextBlock>()
                    .Where(block => ReferenceEquals(block.DataContext, change)).Select(block => block.Text).ToArray();
                Assert.Contains("调整前：" + change.Before, rendered);
                Assert.Contains("调整后：" + change.After, rendered);
            }
            confirmation.PendingMatches[0].Reason = "新原因"; Layout(dialog);
            Assert.False(confirmation.HasPreview); Assert.False(confirmButton.IsEnabled);
            Assert.False(changes.IsEffectivelyVisible);
            dialog.Close(); await modal;
        }
        finally { dialog?.Close(); (sheet?.DataContext as OnsiteRecordViewModel)?.DiscardDrafts(); owner.Close(); }
        return 0;
    }, CancellationToken.None);

    private static void Layout(Window window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
}
