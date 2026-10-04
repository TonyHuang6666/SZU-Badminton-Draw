using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Tests;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class OperationsCompactLayoutTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(1080, 760)]
    [InlineData(760, 560)]
    [InlineData(600, 500)]
    public Task HeaderKeepsAllThreeTabsNearTheTopAndProgressBesideTheTitle(int width, int height) => ui.Dispatch(() =>
    {
        using var fixture = new OperationsUiFixture();
        var view = new OperationsPage { DataContext = fixture.Page };
        var window = new Window { Width = width, Height = height, Content = view };
        try
        {
            window.Show(); Layout(window);
            var tabs = view.FindControl<TabControl>("OperationsTabs")!;
            for (var index = 0; index < 3; index++)
            {
                tabs.SelectedIndex = index; Layout(window);
                // A tall fixed heading must not consume the working area when switching tabs.
                Assert.InRange(tabs.TranslatePoint(default, view)!.Value.Y, 0, 90);
                var title = view.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "比赛现场");
                var progress = view.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Inlines?.OfType<Run>().FirstOrDefault()?.Text == "已记录 ");
                var titlePoint = title.TranslatePoint(default, view)!.Value;
                var progressPoint = progress.TranslatePoint(default, view)!.Value;
                Assert.True(progressPoint.X >= titlePoint.X + title.Bounds.Width);
                Assert.InRange(Math.Abs(progressPoint.Y + progress.Bounds.Height / 2 - titlePoint.Y - title.Bounds.Height / 2), 0, 3);
                var backup = view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "备份与恢复"));
                var point = backup.TranslatePoint(default, window)!.Value;
                Assert.InRange(point.X, 0, window.ClientSize.Width - backup.Bounds.Width);
                Assert.True(backup.IsEffectivelyVisible);
            }
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData(1080, 760)]
    [InlineData(760, 560)]
    public Task MaterialAndImportFootersLeaveRoomForScrollableEvidence(int width, int height) => ui.Dispatch(async () =>
    {
        using var fixture = new OperationsUiFixture(post: action => Dispatcher.UIThread.Post(action));
        var view = new OperationsPage { DataContext = fixture.Page };
        var window = new Window { Width = width, Height = height, Content = view };
        try
        {
            window.Show(); Layout(window);
            AssertCompactFooter(view.FindControl<Button>("ExportOperationalPackage")!, window);
            view.FindControl<TabControl>("OperationsTabs")!.SelectedIndex = 1; Layout(window);
            var panel = view.FindControl<ResultImportPanel>("OperationsResultImport")!;
            AssertCompactFooter(panel.FindControl<Button>("ApplyResultImport")!, window);
            fixture.NextFiles = [fixture.Record()];
            await fixture.Page.ResultImport.PickFilesCommand.ExecuteAsync();
            await fixture.Page.ResultImport.PreviewCommand.ExecuteAsync(); Layout(window);
            AssertCompactFooter(panel.FindControl<Button>("ApplyResultImport")!, window);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData(1080, 760)]
    [InlineData(760, 560)]
    public Task MaterialConsentScrollsWithTheScopeSummaryWhileItsActionStaysFixed(int width, int height) => ui.Dispatch(() =>
    {
        using var fixture = new OperationsUiFixture();
        var view = new OperationsPage { DataContext = fixture.Page };
        var window = new Window { Width = width, Height = height, Content = view };
        try
        {
            window.Show(); Layout(window);
            var confirmation = view.FindControl<CheckBox>("ConfirmOperationalScope")!;
            var scroll = Assert.Single(confirmation.GetVisualAncestors().OfType<ScrollViewer>());
            var summaryCard = Assert.Single(confirmation.GetVisualAncestors().OfType<Border>(), border => border.Classes.Contains("inset"));
            Assert.Contains(summaryCard.GetVisualDescendants().OfType<ResultImportEvidenceText>(), text => text.Text == fixture.Page.Materials.ScopeSummary);
            var action = view.FindControl<Button>("ExportOperationalPackage")!;
            view.FindControl<Expander>("OperationalAdvancedSettings")!.IsExpanded = true; Layout(window);
            AssertMovesWithEvidence(confirmation, action, scroll, window);

            fixture.NextOutput = fixture.Data.PathFor("compact-materials");
            Assert.False(action.IsEffectivelyEnabled); Assert.False(action.Command!.CanExecute(null));
            confirmation.IsChecked = true;
            Assert.True(action.IsEffectivelyEnabled); Assert.True(action.Command.CanExecute(null));
            fixture.Page.Materials.PdfRows = 2;
            Assert.False(confirmation.IsChecked); Assert.False(action.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData(1080, 760)]
    [InlineData(760, 560)]
    public Task ImportConsentAppearsOnlyForCurrentChecksAndScrollsWithTheirEvidence(int width, int height) => ui.Dispatch(async () =>
    {
        using var fixture = new ResultImportUiFixture(1, 2, post: action => Dispatcher.UIThread.Post(action));
        var panel = new ResultImportPanel { DataContext = fixture.ViewModel };
        var window = new Window { Width = width, Height = height, Content = panel };
        try
        {
            window.Show(); Layout(window);
            var confirmation = panel.FindControl<CheckBox>("ConfirmResultImport")!;
            var action = panel.FindControl<Button>("ApplyResultImport")!;
            Assert.False(confirmation.IsVisible); Assert.False(action.IsEffectivelyEnabled);
            var file = fixture.Data.Export();
            await fixture.Choose(file); Layout(window);
            Assert.False(confirmation.IsVisible);
            await fixture.ViewModel.PreviewCommand.ExecuteAsync(); Layout(window);
            Assert.True(confirmation.IsVisible); Assert.False(action.IsEffectivelyEnabled);
            var scroll = Assert.Single(confirmation.GetVisualAncestors().OfType<ScrollViewer>());
            var resultCard = Assert.Single(confirmation.GetVisualAncestors().OfType<Border>(), border => border.Classes.Contains("callout"));
            Assert.Contains(resultCard.GetVisualDescendants().OfType<ResultImportEvidenceText>(), text => text.Name == "ResultImportDiagnostics");
            Assert.Single(panel.GetVisualDescendants().OfType<Expander>()).IsExpanded = true; Layout(window);
            AssertMovesWithEvidence(confirmation, action, scroll, window);
            confirmation.IsChecked = true;
            Assert.True(action.IsEffectivelyEnabled); Assert.True(action.Command!.CanExecute(null));
            fixture.ViewModel.ClearFilesCommand.Execute(null); Layout(window);
            Assert.False(confirmation.IsVisible); Assert.False(confirmation.IsChecked);
            Assert.False(action.IsEffectivelyEnabled); Assert.False(action.Command.CanExecute(null));
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task CompactImportActionStillRequiresCorrectionPermissionReasonAndFreshConsent() => ui.Dispatch(async () =>
    {
        using var fixture = new ResultImportUiFixture(1, 2, post: action => Dispatcher.UIThread.Post(action));
        await fixture.Preview(fixture.Data.Export()); await fixture.Accept();
        var correction = fixture.Data.Export("compact-correction.xlsx");
        WorkspaceResultImportFacadeFixture.Edit(correction, sheet => sheet.Cell(6, 10).Value = 25);
        var panel = new ResultImportPanel { DataContext = fixture.ViewModel };
        var window = new Window { Width = 760, Height = 560, Content = panel };
        try
        {
            window.Show(); await fixture.Preview(correction); Layout(window);
            var confirmation = panel.FindControl<CheckBox>("ConfirmResultImport")!;
            var permission = panel.FindControl<CheckBox>("AllowResultCorrections")!;
            var action = panel.FindControl<Button>("ApplyResultImport")!;
            Assert.True(permission.IsEffectivelyVisible);
            confirmation.IsChecked = true;
            Assert.False(action.IsEffectivelyEnabled); Assert.False(action.Command!.CanExecute(null));
            permission.IsChecked = true; confirmation.IsChecked = true;
            Assert.False(action.IsEffectivelyEnabled); Assert.False(action.Command.CanExecute(null));
            panel.FindControl<TextBox>("ResultCorrectionReason")!.Text = "核对裁判原始记录";
            Assert.False(confirmation.IsChecked); Assert.False(action.IsEffectivelyEnabled);
            confirmation.IsChecked = true;
            Assert.True(action.IsEffectivelyEnabled); Assert.True(action.Command.CanExecute(null));
            permission.IsChecked = false;
            Assert.False(confirmation.IsChecked); Assert.False(action.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task ProgressTabDoesNotAskForWriteConsent() => ui.Dispatch(() =>
    {
        using var fixture = new OperationsUiFixture();
        var view = new OperationsPage { DataContext = fixture.Page };
        var window = new Window { Width = 760, Height = 560, Content = view };
        try
        {
            window.Show(); view.FindControl<TabControl>("OperationsTabs")!.SelectedIndex = 2; Layout(window);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<CheckBox>(), checkbox => checkbox.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("OpenUpdatedSchedule")!.IsEffectivelyVisible);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    private static void AssertCompactFooter(Button action, Window window)
    {
        var footer = Assert.Single(action.GetVisualAncestors().OfType<Border>(), border => border.Classes.Contains("action-bar"));
        Assert.InRange(footer.Bounds.Height, 1, 76);
        Assert.True(action.IsEffectivelyVisible);
        var topLeft = action.TranslatePoint(default, window)!.Value;
        Assert.InRange(topLeft.Y, 0, window.ClientSize.Height - action.Bounds.Height);
        Assert.InRange(topLeft.X, 0, window.ClientSize.Width - action.Bounds.Width);
    }

    private static void AssertMovesWithEvidence(CheckBox confirmation, Button action, ScrollViewer scroll, Window window)
    {
        scroll.Offset = default; Layout(window);
        var confirmationBefore = confirmation.TranslatePoint(default, window)!.Value;
        var actionBefore = action.TranslatePoint(default, window)!.Value;
        scroll.Offset = new Vector(0, scroll.Extent.Height); Layout(window);
        Assert.True(scroll.Offset.Y > 0);
        Assert.True(confirmation.TranslatePoint(default, window)!.Value.Y < confirmationBefore.Y);
        Assert.Equal(actionBefore, action.TranslatePoint(default, window)!.Value);
    }

    private static void Layout(Window window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

    public void Dispose() => ui.Dispose();
}
