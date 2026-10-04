using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class ScheduleVenuePageTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ShellOpensRealVenueDialogAndReturnsCompactDraftSummary(bool dark) => ui.Dispatch(async () =>
    {
        using var fixture = new ScheduleUiFixture();
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "venue-recent.json")))
            { Width = 960, Height = 680, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleSetup); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var page = Assert.IsType<ScheduleSetupPageViewModel>(shell.CurrentPage);
            var view = Assert.Single(window.GetVisualDescendants().OfType<ScheduleSetupPage>());
            var choose = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "ChooseVenueCourtsButton");
            Assert.Same(page.Days[0].ChooseCourtsCommand, choose.Command);
            Assert.Equal("选择场馆与场地", choose.Content);
            var pending = page.Days[0].ChooseCourtsCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows.OfType<VenueCourtSelectionDialog>());
            var options = Assert.IsType<VenueCourtSelectionViewModel>(dialog.DataContext);
            options.VenueIndex = 0; options.SelectAllCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            dialog.FindControl<Button>("AcceptButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(32, page.Days[0].Build().Courts.Count);
            Assert.Contains("共 32 片", page.Days[0].CourtsSummary);
            Assert.Contains("运动广场东馆羽毛球场", page.Days[0].VenueSummary);
            Assert.False(view.FindControl<Expander>("AdvancedScheduleSettings")!.IsExpanded);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBox>(), input => input.Text == page.Days[0].CourtsText);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<CheckBox>(), input => input.DataContext is CourtChoiceViewModel);
            Assert.Null(fixture.Workflow.CurrentSession!.Workspace.Resources);

            page.Days[0].AddUnavailableCommand.Execute(null);
            var unavailable = page.Days[0].Unavailable[0];
            var blockPending = unavailable.ChooseCourtsCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
            var blockDialog = Assert.Single(window.OwnedWindows.OfType<UnavailableCourtSelectionDialog>());
            var blockModel = Assert.IsType<UnavailableCourtSelectionViewModel>(blockDialog.DataContext);
            blockModel.AllCourts = false; blockModel.Courts[0].IsSelected = true;
            Dispatcher.UIThread.RunJobs();
            blockDialog.FindControl<Button>("AcceptButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await blockPending;
            Assert.Equal("粤海东馆 · A1", unavailable.CourtsText);
        }
        finally { foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(); window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReuseButtonConfirmsPreviousDaysCourtsAndTimeTogether(bool dark) => ui.Dispatch(async () =>
    {
        using var fixture = new ScheduleUiFixture();
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "copy-recent.json")))
            { Width = 960, Height = 680, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleSetup); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var page = Assert.IsType<ScheduleSetupPageViewModel>(shell.CurrentPage);
            var source = page.Days[0]; source.DateText = "2026-10-03";
            source.CourtsText = "粤海东馆 · A1"; source.StartText = "14:00"; source.EndText = "18:00";
            page.AddDayCommand.Execute(null); var target = page.Days[0];
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var view = Assert.Single(window.GetVisualDescendants().OfType<ScheduleSetupPage>());
            var reuse = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, target.CopyPreviousCourtsCommand));
            Assert.Equal("沿用上一个比赛日的场地和时间", reuse.Content);
            var pending = ((AsyncCommand)reuse.Command!).ExecuteAsync(); Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows.OfType<VenueCourtSelectionDialog>());
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible &&
                text.Text != null && text.Text.Contains("2026-10-03") && text.Text.Contains("14:00–18:00"));
            Assert.Equal("使用所选场地和时间", dialog.FindControl<Button>("AcceptButton")!.Content);
            Assert.Equal("09:00", target.StartText);
            dialog.FindControl<Button>("AcceptButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pending; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal("粤海东馆 · A1", target.CourtsText);
            Assert.Equal("14:00", target.StartText); Assert.Equal("18:00", target.EndText);
            Assert.Equal("2026-10-04", target.DateText);
        }
        finally { foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(); window.Close(); }
        return 0;
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
