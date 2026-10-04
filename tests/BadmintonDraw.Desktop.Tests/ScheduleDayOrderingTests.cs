using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class ScheduleDayOrderingTests
{
    [Fact]
    public void ConsecutiveAddsPrependNewDaysAndPreserveEarlierDrafts()
    {
        using var fixture = new ScheduleUiFixture();
        using var page = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        var original = page.Days[0]; original.DateText = "2026-10-03";
        original.StartText = "14:00"; original.CourtsText = "粤海东馆 · B1";
        original.AddUnavailableCommand.Execute(null); original.Unavailable[0].StartText = "15:00";
        var path = fixture.Workflow.CurrentSession!.WorkspacePath;
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i < 4; i++) page.AddDayCommand.Execute(null);
        Assert.Equal(["2026-10-07", "2026-10-06", "2026-10-05", "2026-10-04", "2026-10-03"], page.Days.Select(d => d.DateText));
        Assert.Same(original, page.Days[^1]);
        Assert.Equal("14:00", original.StartText); Assert.Equal("粤海东馆 · B1", original.CourtsText);
        Assert.Equal("15:00", Assert.Single(original.Unavailable).StartText);
        Assert.All(page.Days.Take(4), d => Assert.Empty(d.Build().Courts));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void NextDateUsesLatestValidDateNotCardPosition()
    {
        using var fixture = new ScheduleUiFixture();
        using var page = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        var first = page.Days[0]; first.DateText = "2026-10-03";
        page.AddDayCommand.Execute(null);
        var second = page.Days.Single(d => !ReferenceEquals(d, first)); second.DateText = "2026-12-31";
        page.AddDayCommand.Execute(null);
        var invalid = page.Days.Single(d => d != first && d != second); invalid.DateText = "";
        page.AddDayCommand.Execute(null);
        Assert.Equal("2027-01-01", page.Days[0].DateText);
        Assert.Contains(invalid, page.Days); Assert.Equal("", invalid.DateText);
    }

    [Fact]
    public void EditingDateDoesNotMoveCardAndRemovedDayDoesNotAffectNextDate()
    {
        using var fixture = new ScheduleUiFixture();
        using var page = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        page.Days[0].DateText = "2026-10-03";
        page.AddDayCommand.Execute(null); page.AddDayCommand.Execute(null);
        var before = page.Days.ToArray();
        page.Days[1].DateText = "2026-10-20";
        Assert.Equal(before, page.Days);
        page.Days[1].RemoveCommand.Execute(null);
        page.AddDayCommand.Execute(null);
        Assert.Equal("2026-10-06", page.Days[0].DateText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoValidDatesStartsFromToday(bool removeExistingDay)
    {
        using var fixture = new ScheduleUiFixture();
        using var page = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        if (removeExistingDay) page.Days[0].RemoveCommand.Execute(null);
        else page.Days[0].DateText = "";
        var today = DateOnly.FromDateTime(DateTime.Today);
        page.AddDayCommand.Execute(null);
        Assert.Equal(today, page.Days[0].Build().Date);
    }

    [Fact]
    public void LastSupportedDateReportsErrorWithoutThrowingOrChangingDraft()
    {
        using var fixture = new ScheduleUiFixture();
        using var page = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        page.Days[0].DateText = "9999-12-31";
        page.AddDayCommand.Execute(null);
        Assert.Equal("9999-12-31", Assert.Single(page.Days).DateText);
        Assert.NotNull(fixture.Shell.LastError);
        Assert.Contains("日期", fixture.Shell.LastError.Message);
    }

    [Fact]
    public async Task SaveAndReopenShowNewestFirstButScheduleAndSavedResourcesStayChronological()
    {
        using var fixture = new ScheduleUiFixture();
        using var page = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        page.Days[0].DateText = "2026-10-03";
        page.AddDayCommand.Execute(null); page.AddDayCommand.Execute(null);
        foreach (var day in page.Days) { day.CourtsText = "粤海东馆 · B1"; day.StartText = "14:00"; }
        page.Days[0].AddUnavailableCommand.Execute(null);
        page.Days[0].Unavailable[0].StartText = "15:00";
        page.Days[0].Unavailable[0].EndText = "16:00";
        Assert.Equal([3, 4, 5], page.BuildSetup().Resources.Days.Select(d => d.Date.Day));
        await page.GenerateCommand.ExecuteAsync();
        Assert.Null(fixture.Shell.LastError);
        var saved = new TournamentWorkspaceStore().Read(fixture.Workflow.CurrentSession!.WorkspacePath);
        Assert.Equal([3, 4, 5], saved.Schedule!.Resources.Days.Select(d => d.Date.Day));
        Assert.Equal("2026-10-03", Assert.Single(saved.Schedule.Placements.Values).DayLabel);
        using var reopened = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!, confirmDiscard: () => Task.FromResult(true));
        Assert.Equal(["2026-10-05", "2026-10-04", "2026-10-03"], reopened.Days.Select(d => d.DateText));
        Assert.Equal("15:00", Assert.Single(reopened.Days[0].Unavailable).StartText);
        Assert.Equal("16:00", Assert.Single(reopened.Days[0].Unavailable).EndText);
        reopened.Days[0].DateText = "2026-11-01";
        await reopened.ResetCommand.ExecuteAsync();
        Assert.Equal(["2026-10-05", "2026-10-04", "2026-10-03"], reopened.Days.Select(d => d.DateText));
    }
}

[Collection("Avalonia UI dispatcher")]
public sealed class ScheduleDayOrderingViewTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AddedDayAndAddButtonStayVisibleDuringRepeatedAdds(bool dark) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture();
        using var model = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        model.Days[0].DateText = "2026-10-03";
        var page = new ScheduleSetupPage { DataContext = model };
        var window = new Window { Width = 960, Height = 680, Content = page,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); Layout(window);
            var button = page.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, model.AddDayCommand));
            button.BringIntoView(); Layout(window);
            for (var i = 0; i < 5; i++)
            {
                var point = button.TranslatePoint(new Point(30, 20), window)!.Value;
                window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(point, MouseButton.Left, RawInputModifiers.None); Layout(window);
                Assert.Equal(i + 2, model.Days.Count);
                Assert.Equal($"2026-10-{i + 4:00}", model.Days[0].DateText);
                var date = page.GetVisualDescendants().OfType<CalendarDatePicker>().Single(p => ReferenceEquals(p.DataContext, model.Days[0]));
                var scroll = date.GetVisualAncestors().OfType<ScrollViewer>().First();
                AssertVisible(button, scroll); AssertVisible(date, scroll);
                var courts = page.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, model.Days[0].ChooseCourtsCommand));
                AssertVisible(courts, scroll);
            }
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData("reset")]
    [InlineData("context")]
    [InlineData("detach")]
    public Task StaleAddDoesNotScrollAfterPageChanges(string change) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture();
        using var model = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!, confirmDiscard: () => Task.FromResult(true));
        using var other = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        var page = new ScheduleSetupPage { DataContext = model };
        var window = new Window { Width = 960, Height = 680, Content = page };
        try
        {
            window.Show(); Layout(window);
            var scroll = page.GetVisualDescendants().OfType<CalendarDatePicker>().First().GetVisualAncestors().OfType<ScrollViewer>().First();
            model.AddDayCommand.Execute(null);
            switch (change)
            {
                case "reset": model.ResetCommand.Execute(null); break;
                case "context": page.DataContext = other; break;
                case "detach": window.Content = null; window.Content = page; break;
            }
            scroll.Offset = new Vector(0, 100); Layout(window);
            Assert.Equal(100, scroll.Offset.Y);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    private static void AssertVisible(Control control, ScrollViewer scroll)
    {
        var point = control.TranslatePoint(default, scroll)!.Value;
        Assert.InRange(point.Y, 0, scroll.Viewport.Height - control.Bounds.Height);
    }
    private static void Layout(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    public void Dispose() => ui.Dispose();
}
