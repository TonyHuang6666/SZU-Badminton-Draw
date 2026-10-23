using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Tests;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class OnsiteRecordPresentationTests : IDisposable
{
    // A real Skia capture must run in its own filtered test process. Other desktop tests
    // use HeadlessDrawing, whose cached glyph implementations cannot be mixed with Skia.
    private static bool CaptureFrames => Environment.GetEnvironmentVariable("BADMINTON_ONSITE_RENDER") == "1";
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(
        CaptureFrames ? typeof(OnsiteRenderingApp) : typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LongTableScrollsAndSavedResultCorrectionRemainsReachableInBothThemes(bool dark) => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(2, 16);
        var owner = new AppShellWindow(data.Workflow, new RecentWorkspaceStore(data.PathFor("onsite-presentation.json")));
        OnsiteRecordViewModel? model = null;
        try
        {
            owner.Show(); var shell = (AppShellViewModel)owner.DataContext!;
            shell.Navigate(WorkspaceRoute.Operations);
            ((OperationsPageViewModel)shell.CurrentPage).OpenOnsiteRecordsCommand.Execute(null);
            var sheet = Assert.Single(owner.OwnedWindows.OfType<OnsiteRecordWindow>());
            sheet.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            sheet.Width = 1100; sheet.Height = 650; Layout(sheet);
            model = (OnsiteRecordViewModel)sheet.DataContext!;
            Assert.True(sheet.FindControl<ComboBox>("OnsiteProject")!.IsEffectivelyVisible);
            var horizontal = sheet.FindControl<ScrollViewer>("OnsiteHorizontalScroll")!;
            Assert.True(horizontal.Extent.Width > horizontal.Viewport.Width);
            var list = sheet.FindControl<ListBox>("OnsiteRows")!;
            var vertical = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(vertical.Extent.Height > vertical.Viewport.Height);
            Assert.InRange(list.Bounds.Height, 150, sheet.Bounds.Height);
            vertical.ScrollToEnd(); Layout(sheet);
            Assert.True(vertical.Offset.Y > 0);
            list.ScrollIntoView(model.VisibleRows.Last()); Layout(sheet);
            Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), text =>
                ReferenceEquals(text.DataContext, model.VisibleRows.Last()) && text.Text == model.VisibleRows.Last().SideB.Insert(0, "B  "));

            var first = model.VisibleRows.First(r => r.CanEdit);
            list.ScrollIntoView(first); Layout(sheet);
            first.WinnerIndex = 1; first.KindIndex = 0;
            await model.WaitForAutomaticSavesAsync();
            Assert.True(first.IsSaved);
            first.Score = "21-10"; first.Duration = "20";
            Assert.True(await model.SaveRowAsync(first));
            first.Duration = "25"; Layout(sheet);
            var correction = Assert.Single(list.GetVisualDescendants().OfType<CheckBox>(),
                box => box.Name == "OnsiteAllowCorrection" && ReferenceEquals(box.DataContext, first));
            var reason = Assert.Single(list.GetVisualDescendants().OfType<TextBox>(),
                box => box.Name == "OnsiteCorrectionReason" && ReferenceEquals(box.DataContext, first));
            Assert.True(correction.IsEffectivelyVisible); Assert.True(reason.IsEffectivelyVisible);
            Assert.True(reason.Bounds.Width > 100);
            var text = Assert.Single(list.GetVisualDescendants().OfType<TextBlock>(),
                control => ReferenceEquals(control.DataContext, first) && control.Text == first.ProjectName);
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(sheet.FindResource(sheet.ActualThemeVariant, "AppTextBrush")).Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);

            if (CaptureFrames)
            {
                using var frame = sheet.CaptureRenderedFrame();
                Assert.NotNull(frame);
                Assert.True(frame.PixelSize.Width >= 1100);
                frame.Save(Path.Combine(Path.GetTempPath(), $"szuba-onsite-records-{(dark ? "dark" : "light")}.png"));
            }

            var other = model.VisibleRows.First(r => r.CanEdit && r != first);
            other.Score = "21-9";
            Assert.Single(list.GetVisualDescendants().OfType<Button>(),
                button => button.Name == "OnsiteDiscardRow" && ReferenceEquals(button.DataContext, first))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(first.HasDraft); Assert.True(other.HasDraft); Assert.Equal("20", first.Duration);
        }
        finally { model?.DiscardDrafts(); owner.Close(); }
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DayConfirmationPreviewScrollsWithReachableFooterInBothThemes(bool dark) => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1, 16);
        var owner = new AppShellWindow(data.Workflow, new RecentWorkspaceStore(data.PathFor("day-presentation.json")));
        OnsiteRecordViewModel? records = null;
        OnsiteDayConfirmationWindow? dialog = null;
        try
        {
            owner.Show(); var shell = (AppShellViewModel)owner.DataContext!;
            Assert.True(shell.Navigate(WorkspaceRoute.Operations));
            Assert.IsType<OperationsPageViewModel>(shell.CurrentPage).OpenOnsiteRecordsCommand.Execute(null);
            var sheet = Assert.Single(owner.OwnedWindows.OfType<OnsiteRecordWindow>());
            records = Assert.IsType<OnsiteRecordViewModel>(sheet.DataContext);
            var confirmation = Assert.IsType<OnsiteDayConfirmationViewModel>(await records.PrepareDayConfirmationAsync());
            Assert.True(confirmation.PendingMatches.Count > 10);
            foreach (var row in confirmation.PendingMatches)
            { row.IsPostponed = true; row.Reason = "场馆临时停用，裁判长批准延期"; }
            await confirmation.PreviewAsync();
            Assert.True(confirmation.CanConfirm); Assert.NotEmpty(confirmation.Changes);
            dialog = new OnsiteDayConfirmationWindow
            {
                DataContext = confirmation,
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
            };
            var modal = dialog.ShowDialog(sheet); Layout(dialog);
            var scroll = dialog.FindControl<ScrollViewer>("DayConfirmationScroll")!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
            scroll.ScrollToEnd(); Layout(dialog);
            Assert.True(scroll.Offset.Y > 0);
            var changes = dialog.FindControl<ItemsControl>("DayChanges")!;
            Assert.True(changes.IsEffectivelyVisible);
            Assert.Equal(confirmation.Changes.Count, changes.ItemCount);
            AssertConfirmationFooterWithinWindow(dialog);

            if (CaptureFrames)
            {
                using var frame = dialog.CaptureRenderedFrame();
                Assert.NotNull(frame);
                Assert.True(frame.PixelSize.Width >= 900);
                frame.Save(Path.Combine(Path.GetTempPath(), $"szuba-onsite-confirmation-{(dark ? "dark" : "light")}.png"));
            }

            dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight; Layout(dialog);
            AssertConfirmationFooterWithinWindow(dialog);
            Assert.True(scroll.Viewport.Height > 80);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
            dialog.Close(); await modal;
        }
        finally { dialog?.Close(); records?.DiscardDrafts(); owner.Close(); }
        return 0;
    }, CancellationToken.None);

    private static void AssertConfirmationFooterWithinWindow(OnsiteDayConfirmationWindow dialog)
    {
        foreach (var name in new[] { "DayPreview", "DayConfirm" })
        {
            var button = dialog.FindControl<Button>(name)!;
            Assert.True(button.IsEffectivelyVisible); Assert.True(button.IsEnabled);
            var origin = button.TranslatePoint(default, dialog);
            Assert.NotNull(origin);
            Assert.InRange(origin.Value.X, 0, dialog.Bounds.Width - button.Bounds.Width);
            Assert.InRange(origin.Value.Y, 0, dialog.Bounds.Height - button.Bounds.Height);
        }
    }

    private static void Layout(Window window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }

    public void Dispose() => ui.Dispose();

    public static class OnsiteRenderingApp
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia()
            .WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
