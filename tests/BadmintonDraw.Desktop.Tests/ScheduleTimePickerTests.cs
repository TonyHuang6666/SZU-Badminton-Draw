using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class ScheduleTimePickerTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ClosedTimeDisplayMatchesTheAdjacentDateField(bool dark) => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        fixture.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        fixture.Model.Days[0].StartTime = new TimeSpan(9, 0, 0); Layout(fixture.Window);
        var date = Assert.Single(fixture.Page.GetVisualDescendants().OfType<CalendarDatePicker>());
        var label = Assert.Single(fixture.OpenButton.GetVisualDescendants().OfType<TextBlock>());
        var border = Assert.Single(fixture.OpenButton.GetVisualDescendants().OfType<Border>());
        Assert.Equal("09:00", label.Text);
        Assert.Equal(date.FontFamily, label.FontFamily);
        Assert.Equal(date.FontSize, label.FontSize);
        Assert.Equal(date.FontWeight, label.FontWeight);
        Assert.Equal(HorizontalAlignment.Left, label.HorizontalAlignment);
        Assert.Equal(VerticalAlignment.Center, label.VerticalAlignment);
        Assert.Equal(date.Background?.ToString(), border.Background?.ToString());
        Assert.Equal(date.BorderBrush?.ToString(), border.BorderBrush?.ToString());
        Assert.Equal(date.CornerRadius, border.CornerRadius);
        Assert.Equal(date.Padding, border.Padding);
        fixture.Model.Days[0].StartTime = new TimeSpan(7, 5, 0); Layout(fixture.Window);
        Assert.Equal("07:05", label.Text);
        fixture.Model.Days[0].StartTime = TimeSpan.Zero; Layout(fixture.Window);
        Assert.Equal("00:00", label.Text);
        fixture.Model.Days[0].StartTime = new TimeSpan(23, 59, 0); Layout(fixture.Window);
        Assert.Equal("23:59", label.Text);
        fixture.Model.Days[0].StartTime = null; Layout(fixture.Window);
        Assert.Equal("选择时间", label.Text);
        Assert.Null(fixture.Picker.SelectedTime);
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ClosedTimeFieldKeepsThemeFeedbackAndReadOnlyAppearance(bool dark) => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        fixture.Window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light; Layout(fixture.Window);
        var border = Assert.Single(fixture.OpenButton.GetVisualDescendants().OfType<Border>());
        var label = Assert.Single(fixture.OpenButton.GetVisualDescendants().OfType<TextBlock>());
        string? Brush(string key) => Assert.IsAssignableFrom<IBrush>(fixture.Window.FindResource(fixture.Window.ActualThemeVariant, key)).ToString();
        var point = fixture.OpenButton.TranslatePoint(new Point(30, 20), fixture.Window)!.Value;
        fixture.Window.MouseMove(point, RawInputModifiers.None); Layout(fixture.Window);
        Assert.Equal(Brush("AppInputHoverBackgroundBrush"), border.Background?.ToString());
        Assert.Equal(Brush("AppInputHoverBorderBrush"), border.BorderBrush?.ToString());
        fixture.OpenButton.Focus(); Layout(fixture.Window);
        Assert.Equal(Brush("AppAccentBrush"), border.BorderBrush?.ToString());
        fixture.Page.IsEnabled = false; Layout(fixture.Window);
        Assert.Equal(Brush("AppDisabledBackgroundBrush"), border.Background?.ToString());
        Assert.Equal(Brush("AppDisabledBorderBrush"), border.BorderBrush?.ToString());
        Assert.Equal(Brush("AppDisabledTextBrush"), label.Foreground?.ToString());
        Assert.False(fixture.OpenButton.IsEffectivelyEnabled);
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ClickingTheTimeTextOrClockIconOpensTheSamePicker(bool icon) => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        var target = icon
            ? (Control)Assert.Single(fixture.OpenButton.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>())
            : Assert.Single(fixture.OpenButton.GetVisualDescendants().OfType<TextBlock>());
        Click(target); Layout(fixture.Window);
        Assert.True(fixture.Popup.IsOpen);
        Assert.Equal("14:07:30.123", fixture.Model.Days[0].StartText);
        Click(fixture.PresenterButton("PART_DismissButton"));
        Assert.False(fixture.Popup.IsOpen);
        Assert.Equal("14:07:30.123", fixture.Model.Days[0].StartText);
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DailyTimeSelectionsUpdateOnlyTheChosenTimeWithoutSaving(bool dark) => ui.Dispatch(() =>
    {
        using var data = new ScheduleUiFixture();
        using var model = new ScheduleSetupPageViewModel(data.Shell, data.Workflow.CurrentSession!);
        var day = model.Days[0];
        day.DateText = "2026-10-24"; day.StartText = "09:00"; day.EndText = "18:00";
        day.CourtsText = "粤海东馆 · B1, 粤海东馆 · B2";
        var session = data.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(session.WorkspacePath);
        var view = new ScheduleSetupPage { DataContext = model };
        var window = new Window { Width = 960, Height = 680, Content = view,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); Layout(window);
            var pickers = view.GetVisualDescendants().OfType<TimePicker>().ToArray();
            Assert.Equal(2, pickers.Length);
            Assert.Equal(new TimeSpan(9, 0, 0), pickers[0].SelectedTime);
            Assert.Equal(new TimeSpan(18, 0, 0), pickers[1].SelectedTime);
            pickers[0].SelectedTime = new TimeSpan(14, 7, 0); Layout(window);
            Assert.Equal("14:07", day.StartText); Assert.Equal("18:00", day.EndText);
            pickers[1].SelectedTime = new TimeSpan(18, 59, 0); Layout(window);
            Assert.Equal(new TimeOnly(14, 7), day.Build().DayStart);
            Assert.Equal(new TimeOnly(18, 59), day.Build().DayEnd);
            Assert.Equal("2026-10-24", day.DateText);
            Assert.Equal("粤海东馆 · B1, 粤海东馆 · B2", day.CourtsText);
            Assert.Same(session, data.Workflow.CurrentSession);
            Assert.Equal(bytes, File.ReadAllBytes(session.WorkspacePath));
            day.StartText = "00:01"; Layout(window);
            Assert.Equal(new TimeSpan(0, 1, 0), pickers[0].SelectedTime);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task AssistiveToolsCannotTypeTimeOrOpenAReadOnlyPicker() => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        var value = Assert.IsAssignableFrom<IValueProvider>(ControlAutomationPeer.CreatePeerForElement(fixture.Picker));
        Assert.True(value.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => value.SetValue("15:20"));
        var button = fixture.OpenButton;
        fixture.Page.IsEnabled = false;
        Assert.Throws<ElementNotEnabledException>(() => Assert.IsAssignableFrom<IInvokeProvider>(ControlAutomationPeer.CreatePeerForElement(button)).Invoke());
        Assert.False(fixture.Popup.IsOpen);
        Assert.Equal("14:07:30.123", fixture.Model.Days[0].StartText);
    }, CancellationToken.None);

    [Fact]
    public Task KeyboardReopeningAndEscapeNeverApplyPendingTime() => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        for (var i = 0; i < 2; i++)
        {
            fixture.OpenButton.Focus();
            fixture.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            fixture.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " "); Layout(fixture.Window);
            Assert.True(fixture.Popup.IsOpen);
            Assert.True(fixture.Hour.IsFocused);
            var root = TopLevel.GetTopLevel(fixture.Presenter)!;
            root.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            root.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Layout(fixture.Window);
            Assert.False(fixture.Popup.IsOpen);
            Assert.True(fixture.OpenButton.IsFocused);
            Assert.Equal("14:07:30.123", fixture.Model.Days[0].StartText);
        }
    }, CancellationToken.None);

    [Theory]
    [InlineData(0, 0, 23, 59)]
    [InlineData(14, 7, 18, 59)]
    public Task ConfirmedTimesSurviveScheduleGenerationAndReopen(int hour, int minute, int endHour, int endMinute) => ui.Dispatch(async () =>
    {
        using var fixture = new PickerFixture();
        fixture.Model.Days[0].CourtsText = "丽湖至快 · 1号场";
        fixture.Open(); fixture.Hour.SelectedValue = hour; fixture.Minute.SelectedValue = minute;
        Click(fixture.PresenterButton("PART_AcceptButton"));
        var end = fixture.Page.GetVisualDescendants().OfType<TimePicker>().Last();
        Click(Assert.Single(end.GetVisualDescendants().OfType<Button>(), b => b.Name == "PART_FlyoutButton"));
        var presenter = Assert.IsType<TimePickerPresenter>(Assert.Single(end.GetVisualDescendants().OfType<Popup>()).Child);
        Assert.Single(presenter.GetVisualDescendants().OfType<DateTimePickerPanel>(), p => p.Name == "PART_HourSelector").SelectedValue = endHour;
        Assert.Single(presenter.GetVisualDescendants().OfType<DateTimePickerPanel>(), p => p.Name == "PART_MinuteSelector").SelectedValue = endMinute;
        Click(Assert.Single(presenter.GetVisualDescendants().OfType<Button>(), b => b.Name == "PART_AcceptButton"));
        await fixture.Model.GenerateCommand.ExecuteAsync();
        Assert.Null(fixture.Data.Shell.LastError);
        var saved = new TournamentWorkspaceStore().Read(fixture.Data.Workflow.CurrentSession!.WorkspacePath);
        var day = Assert.Single(saved.Schedule!.Resources.Days);
        Assert.Equal(new TimeOnly(hour, minute), day.DayStart);
        Assert.Equal(new TimeOnly(endHour, endMinute), day.DayEnd);
        using var reopened = new ScheduleSetupPageViewModel(fixture.Data.Shell, fixture.Data.Workflow.CurrentSession!);
        Assert.Equal(new TimeSpan(hour, minute, 0), reopened.Days[0].StartTime);
        Assert.Equal(new TimeSpan(endHour, endMinute, 0), reopened.Days[0].EndTime);
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData(14)]
    [InlineData(13)]
    public Task EndingAtOrBeforeStartReportsTheTimeProblemAndLeavesArchiveUntouched(int hour) => ui.Dispatch(async () =>
    {
        using var fixture = new PickerFixture();
        fixture.Model.Days[0].CourtsText = "B1";
        fixture.Model.Days[0].StartTime = new TimeSpan(14, 0, 0);
        fixture.Model.Days[0].EndTime = new TimeSpan(hour, 0, 0);
        var path = fixture.Data.Workflow.CurrentSession!.WorkspacePath; var bytes = File.ReadAllBytes(path);
        await fixture.Model.GenerateCommand.ExecuteAsync();
        Assert.Contains("结束时间必须晚于开始时间", fixture.Data.Shell.LastError?.Message);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(new TimeSpan(hour, 0, 0), fixture.Model.Days[0].EndTime);
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData("cancel")]
    [InlineData("escape")]
    [InlineData("outside")]
    [InlineData("disable")]
    public Task ReopeningAfterCancelStartsFromTheSavedDraftTime(string action) => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        fixture.Open(); fixture.Hour.SelectedValue = 17; fixture.Minute.SelectedValue = 59;
        switch (action)
        {
            case "cancel": Click(fixture.PresenterButton("PART_DismissButton")); break;
            case "escape": TopLevel.GetTopLevel(fixture.Presenter)!.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); break;
            case "outside":
                fixture.Window.MouseDown(new Point(5, 5), MouseButton.Left, RawInputModifiers.None);
                fixture.Window.MouseUp(new Point(5, 5), MouseButton.Left, RawInputModifiers.None); break;
            case "disable": fixture.Page.IsEnabled = false; Layout(fixture.Window); fixture.Page.IsEnabled = true; break;
        }
        Layout(fixture.Window);
        Assert.False(fixture.Popup.IsOpen);
        fixture.Open();
        Assert.Equal(14, fixture.Hour.SelectedValue);
        Assert.Equal(7, fixture.Minute.SelectedValue);
        Assert.Equal("14:07:30.123", fixture.Model.Days[0].StartText);
        Click(fixture.PresenterButton("PART_AcceptButton"));
        Assert.Equal("14:07", fixture.Model.Days[0].StartText);
    }, CancellationToken.None);

    [Fact]
    public Task HoursAndMinutesAreAppliedOnlyAfterConfirmation() => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        fixture.Open();
        fixture.Hour.SelectedValue = 15; fixture.Minute.SelectedValue = 59;
        Assert.Equal("14:07:30.123", fixture.Model.Days[0].StartText);
        Click(fixture.PresenterButton("PART_AcceptButton")); Layout(fixture.Window);
        Assert.False(fixture.Popup.IsOpen);
        Assert.Equal("15:59", fixture.Model.Days[0].StartText);
        Assert.Equal("18:00", fixture.Model.Days[0].EndText);
    }, CancellationToken.None);

    [Theory]
    [InlineData("cancel")]
    [InlineData("escape")]
    [InlineData("outside")]
    [InlineData("disable")]
    [InlineData("detach")]
    [InlineData("conflict")]
    public Task DismissingOrInvalidatingThePopupNeverAppliesPendingTime(string action) => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        var bytes = File.ReadAllBytes(fixture.Data.Workflow.CurrentSession!.WorkspacePath);
        fixture.Open(); fixture.Hour.SelectedValue = 17; fixture.Minute.SelectedValue = 59;
        switch (action)
        {
            case "cancel": Click(fixture.PresenterButton("PART_DismissButton")); break;
            case "escape": TopLevel.GetTopLevel(fixture.Presenter)!.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); break;
            case "outside":
                fixture.Window.MouseDown(new Point(5, 5), MouseButton.Left, RawInputModifiers.None);
                fixture.Window.MouseUp(new Point(5, 5), MouseButton.Left, RawInputModifiers.None); break;
            case "disable": fixture.Page.IsEnabled = false; break;
            case "detach": fixture.Window.Content = null; break;
            case "conflict":
                var session = fixture.Data.Workflow.CurrentSession!;
                fixture.Model.RefreshSession(session with { Workspace = session.Workspace with
                    { Resources = new([new(new(2026, 11, 1), new(14, 0), new(18, 0), ["B1"])], 1, 30, 4) } });
                Assert.False(fixture.Model.CanEdit); break;
        }
        Layout(fixture.Window);
        Assert.False(fixture.Popup.IsOpen);
        Assert.Equal("14:07:30.123", fixture.Model.Days[0].StartText);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Data.Workflow.CurrentSession!.WorkspacePath));
    }, CancellationToken.None);

    private sealed class PickerFixture : IDisposable
    {
        internal ScheduleUiFixture Data { get; } = new();
        internal ScheduleSetupPageViewModel Model { get; }
        internal ScheduleSetupPage Page { get; }
        internal Window Window { get; }
        internal TimePicker Picker { get; }
        internal Popup Popup => Assert.Single(Picker.GetVisualDescendants().OfType<Popup>());
        internal TimePickerPresenter Presenter => Assert.IsType<TimePickerPresenter>(Popup.Child);
        internal DateTimePickerPanel Hour => Assert.Single(Presenter.GetVisualDescendants().OfType<DateTimePickerPanel>(), p => p.Name == "PART_HourSelector");
        internal DateTimePickerPanel Minute => Assert.Single(Presenter.GetVisualDescendants().OfType<DateTimePickerPanel>(), p => p.Name == "PART_MinuteSelector");
        internal Button PresenterButton(string name) => Assert.Single(Presenter.GetVisualDescendants().OfType<Button>(), b => b.Name == name);
        internal Button OpenButton => Assert.Single(Picker.GetVisualDescendants().OfType<Button>(), b => b.Name == "PART_FlyoutButton");
        internal PickerFixture()
        {
            Model = new(Data.Shell, Data.Workflow.CurrentSession!);
            Model.Days[0].StartText = "14:07:30.123";
            Page = new() { DataContext = Model };
            Window = new() { Width = 960, Height = 680, Content = Page };
            Window.Show(); Layout(Window);
            Picker = Page.GetVisualDescendants().OfType<TimePicker>().First();
            Picker.BringIntoView(); Layout(Window);
        }
        internal void Open()
        {
            Click(OpenButton);
            Layout(Window); Assert.True(Popup.IsOpen);
        }
        public void Dispose() { Window.Close(); Model.Dispose(); Data.Dispose(); }
    }

    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        root.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs(); root.UpdateLayout();
    }
    private static void Layout(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    public void Dispose() => ui.Dispose();
}
