using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

[Collection("Avalonia UI dispatcher")]
public sealed class ScheduleDatePickerTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CalendarSelectionChangesOnlyTheDateAndKeepsTheDraftUnsaved(bool dark) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture();
        using var model = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        var day = model.Days[0];
        day.DateText = "2026-10-03"; day.StartText = "14:00"; day.EndText = "18:00";
        day.CourtsText = "粤海东馆 · B1, 粤海东馆 · B2";
        day.AddUnavailableCommand.Execute(null);
        day.Unavailable[0].StartText = "15:00"; day.Unavailable[0].EndText = "16:00";
        var session = fixture.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(session.WorkspacePath);
        var page = new ScheduleSetupPage { DataContext = model };
        var window = new Window { Width = 960, Height = 680, Content = page,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); Layout(window);
            var picker = Assert.Single(page.GetVisualDescendants().OfType<CalendarDatePicker>());
            Assert.Equal(new DateTime(2026, 10, 3), picker.SelectedDate);
            picker.SelectedDate = new DateTime(2028, 2, 29); Layout(window);
            Assert.Equal("2028-02-29", day.DateText);
            Assert.Equal(new DateOnly(2028, 2, 29), day.Build().Date);
            var resource = Assert.Single(model.BuildSetup().Resources.Days);
            Assert.Equal("2028-02-29", resource.DayLabel);
            Assert.Equal(new TimeOnly(15, 0), Assert.Single(resource.UnavailableCourtWindows!).StartTime);
            Assert.Equal("14:00", day.StartText); Assert.Equal("18:00", day.EndText);
            Assert.Equal("粤海东馆 · B1, 粤海东馆 · B2", day.CourtsText);
            Assert.Same(session, fixture.Workflow.CurrentSession);
            Assert.Equal(bytes, File.ReadAllBytes(session.WorkspacePath));
            day.DateText = "2027-12-31"; Layout(window);
            Assert.Equal(new DateTime(2027, 12, 31), picker.SelectedDate);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData(3)]
    [InlineData(20)]
    public Task ClickingADayClosesTheCalendarIncludingTheAlreadySelectedDay(int day) => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        fixture.ClickText();
        var button = Assert.Single(fixture.Calendar.GetVisualDescendants().OfType<CalendarDayButton>(),
            b => b.DataContext is DateTime date && date == new DateTime(2026, 10, day));
        Click(button); Layout(fixture.Window);
        Assert.False(fixture.Picker.IsDropDownOpen);
        Assert.Equal(new DateTime(2026, 10, day), fixture.Model.Days[0].SelectedDate);
    }, CancellationToken.None);

    [Theory]
    [InlineData(CalendarMode.Month)]
    [InlineData(CalendarMode.Year)]
    [InlineData(CalendarMode.Decade)]
    public Task EscapeDismissesEachCalendarViewWithoutChangingTheDate(CalendarMode mode) => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        fixture.ClickText();
        var header = Assert.Single(fixture.Calendar.GetVisualDescendants().OfType<Button>(), b => b.Name == "PART_HeaderButton");
        for (var i = 0; i < (int)mode; i++) Click(header);
        Assert.Equal(mode, fixture.Calendar.DisplayMode);
        Assert.True(fixture.Calendar.IsKeyboardFocusWithin, $"Focused: {fixture.Window.FocusManager?.GetFocusedElement()?.GetType().Name}; calendar focusable={fixture.Calendar.Focusable}");
        TopLevel.GetTopLevel(fixture.Calendar)!.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Layout(fixture.Window);
        Assert.False(fixture.Picker.IsDropDownOpen);
        Assert.Equal("2026-10-03", fixture.Model.Days[0].DateText);
        Assert.True(fixture.Picker.IsFocused);
    }, CancellationToken.None);

    [Fact]
    public Task KeyboardConfirmationDoesNotReopenAndEscapeRestoresTheOpeningDate() => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        fixture.Picker.Focus();
        fixture.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        fixture.Window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Layout(fixture.Window);
        Assert.True(fixture.Picker.IsDropDownOpen);
        var popup = TopLevel.GetTopLevel(fixture.Calendar)!;
        popup.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        popup.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Layout(fixture.Window);
        Assert.False(fixture.Picker.IsDropDownOpen);
        Assert.Equal("2026-10-03", fixture.Model.Days[0].DateText);
        fixture.ClickText();
        popup = TopLevel.GetTopLevel(fixture.Calendar)!;
        popup.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        popup.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        fixture.Window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Layout(fixture.Window);
        Assert.False(fixture.Picker.IsDropDownOpen);
        Assert.Equal("2026-10-04", fixture.Model.Days[0].DateText);
    }, CancellationToken.None);

    [Fact]
    public Task BrowsingMonthsAndClickingOutsideDoesNotChangeDateOrSave() => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        var session = fixture.Data.Workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(session.WorkspacePath);
        fixture.ClickText();
        var next = Assert.Single(fixture.Calendar.GetVisualDescendants().OfType<Button>(), b => b.Name == "PART_NextButton");
        Click(next);
        Assert.Equal(11, fixture.Calendar.DisplayDate.Month);
        fixture.Window.MouseDown(new Point(5, 5), MouseButton.Left, RawInputModifiers.None);
        fixture.Window.MouseUp(new Point(5, 5), MouseButton.Left, RawInputModifiers.None);
        Layout(fixture.Window);
        Assert.False(fixture.Picker.IsDropDownOpen);
        Assert.Equal("2026-10-03", fixture.Model.Days[0].DateText);
        Assert.Equal(bytes, File.ReadAllBytes(session.WorkspacePath));
    }, CancellationToken.None);

    [Fact]
    public Task ScrollingOverDateDoesNotChangeItAndReadOnlyPageDisablesSelection() => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        var point = fixture.Picker.TranslatePoint(new Point(30, 20), fixture.Window)!.Value;
        fixture.Window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.None); Layout(fixture.Window);
        Assert.Equal("2026-10-03", fixture.Model.Days[0].DateText);
        fixture.Model.RefereeCountText = "2";
        fixture.ClickText();
        var session = fixture.Data.Workflow.CurrentSession!;
        fixture.Model.RefreshSession(session with { Workspace = session.Workspace with
            { Resources = new([new(new(2026, 11, 1), new(14, 0), new(18, 0), ["B1"])], 1, 30, 4) } });
        Layout(fixture.Window);
        Assert.False(fixture.Model.CanEdit);
        Assert.False(fixture.Picker.IsEffectivelyEnabled);
        Assert.False(fixture.Picker.IsDropDownOpen);
        fixture.ClickText();
        Assert.False(fixture.Picker.IsDropDownOpen);
    }, CancellationToken.None);

    [Fact]
    public Task SelectedDatePersistsAfterGenerationAndReloadAndDuplicateDayIsRejected() => ui.Dispatch(async () =>
    {
        using var fixture = new PickerFixture();
        fixture.Picker.SelectedDate = new DateTime(2028, 2, 29); Layout(fixture.Window);
        fixture.Model.Days[0].CourtsText = "丽湖至快 · 1号场";
        fixture.Model.Days[0].StartText = "14:00"; fixture.Model.Days[0].EndText = "18:00";
        await fixture.Model.GenerateCommand.ExecuteAsync();
        Assert.Null(fixture.Data.Shell.LastError);
        var path = fixture.Data.Workflow.CurrentSession!.WorkspacePath;
        var saved = new TournamentWorkspaceStore().Read(path);
        Assert.Equal(new DateOnly(2028, 2, 29), Assert.Single(saved.Schedule!.Resources.Days).Date);
        using var reopened = new ScheduleSetupPageViewModel(fixture.Data.Shell, fixture.Data.Workflow.CurrentSession!);
        Assert.Equal(new DateTime(2028, 2, 29), reopened.Days[0].SelectedDate);
        var bytes = File.ReadAllBytes(path);
        var original = reopened.Days[0];
        reopened.AddDayCommand.Execute(null);
        reopened.Days[0].SelectedDate = original.SelectedDate;
        reopened.Days[0].CourtsText = "丽湖至快 · 1号场";
        await reopened.GenerateCommand.ExecuteAsync();
        Assert.NotNull(fixture.Data.Shell.LastError);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task DateFieldIsNonTypingAndOpensFromTextOrKeyboard() => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        var text = Assert.Single(fixture.Picker.GetVisualDescendants().OfType<TextBox>());
        Assert.True(text.IsReadOnly);
        Assert.False(text.Focusable);
        Assert.False(text.IsHitTestVisible);
        fixture.ClickText();
        Assert.True(fixture.Picker.IsDropDownOpen);
        Assert.Equal(new DateTime(2026, 10, 3), fixture.Calendar.SelectedDate);
        fixture.Picker.IsDropDownOpen = false;
        fixture.Picker.Focus();
        fixture.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        fixture.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Layout(fixture.Window);
        Assert.True(fixture.Picker.IsDropDownOpen);
    }, CancellationToken.None);

    [Fact]
    public Task CalendarIconOpensAndYearThenMonthSelectionReturnsToDays() => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        var icon = Assert.Single(fixture.Picker.GetVisualDescendants().OfType<Button>(), b => b.Name == "PART_Button");
        Click(icon); Layout(fixture.Window);
        Assert.True(fixture.Picker.IsDropDownOpen);
        var header = Assert.Single(fixture.Calendar.GetVisualDescendants().OfType<Button>(), b => b.Name == "PART_HeaderButton");
        Click(header); Click(header);
        Assert.Equal(CalendarMode.Decade, fixture.Calendar.DisplayMode);
        Click(Assert.Single(fixture.Calendar.GetVisualDescendants().OfType<CalendarButton>(), b => b.DataContext is DateTime d && d.Year == 2028));
        Assert.Equal(CalendarMode.Year, fixture.Calendar.DisplayMode);
        Click(Assert.Single(fixture.Calendar.GetVisualDescendants().OfType<CalendarButton>(), b => b.DataContext is DateTime d && d.Month == 2));
        Assert.Equal(CalendarMode.Month, fixture.Calendar.DisplayMode);
        Click(Assert.Single(fixture.Calendar.GetVisualDescendants().OfType<CalendarDayButton>(), b => b.DataContext is DateTime d && d == new DateTime(2028, 2, 29)));
        Layout(fixture.Window);
        Assert.Equal("2028-02-29", fixture.Model.Days[0].DateText);
        Assert.False(fixture.Picker.IsDropDownOpen);
    }, CancellationToken.None);

    [Fact]
    public Task AccessibilityExposesReadOnlyDateAndCannotOpenADisabledPicker() => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        var peer = ControlAutomationPeer.CreatePeerForElement(fixture.Picker);
        var value = Assert.IsAssignableFrom<IValueProvider>(peer);
        var expand = Assert.IsAssignableFrom<IExpandCollapseProvider>(peer);
        Assert.True(value.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => value.SetValue("2028-02-29"));
        Assert.Equal("2026-10-03", fixture.Model.Days[0].DateText);
        expand.Expand(); Layout(fixture.Window);
        Assert.True(fixture.Picker.IsDropDownOpen);
        expand.Collapse();
        fixture.Page.IsEnabled = false;
        Assert.Throws<ElementNotEnabledException>(() => expand.Expand());
        Assert.Throws<ElementNotEnabledException>(() => Assert.IsAssignableFrom<IInvokeProvider>(peer).Invoke());
        Assert.False(fixture.Picker.IsDropDownOpen);
    }, CancellationToken.None);

    [Fact]
    public Task DisablingThePageClosesTheCalendarAndDetachingAlsoClosesIt() => ui.Dispatch(() =>
    {
        using var fixture = new PickerFixture();
        fixture.Picker.IsDropDownOpen = true; Layout(fixture.Window);
        fixture.Page.IsEnabled = false; Layout(fixture.Window);
        Assert.False(fixture.Picker.IsDropDownOpen);
        fixture.Page.IsEnabled = true; Layout(fixture.Window);
        fixture.Picker.IsDropDownOpen = true; Layout(fixture.Window);
        fixture.Window.Content = null; Layout(fixture.Window);
        Assert.False(fixture.Picker.IsDropDownOpen);
        Assert.Equal("2026-10-03", fixture.Model.Days[0].DateText);
    }, CancellationToken.None);

    private sealed class PickerFixture : IDisposable
    {
        internal ScheduleUiFixture Data { get; } = new();
        internal ScheduleSetupPageViewModel Model { get; }
        internal ScheduleSetupPage Page { get; }
        internal Window Window { get; }
        internal CalendarDatePicker Picker { get; }
        internal Calendar Calendar => Assert.IsType<Calendar>(Assert.Single(Picker.GetVisualDescendants().OfType<Popup>()).Child);
        internal PickerFixture()
        {
            Model = new(Data.Shell, Data.Workflow.CurrentSession!);
            Model.Days[0].DateText = "2026-10-03";
            Page = new() { DataContext = Model };
            Window = new() { Width = 960, Height = 680, Content = Page };
            Window.Show(); Layout(Window);
            Picker = Assert.Single(Page.GetVisualDescendants().OfType<CalendarDatePicker>());
            Picker.BringIntoView(); Layout(Window);
        }
        internal void ClickText()
        {
            var point = Picker.TranslatePoint(new Point(30, Picker.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            Layout(Window);
        }
        public void Dispose() { Window.Close(); Model.Dispose(); Data.Dispose(); }
    }

    private static void Layout(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        root.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs(); root.UpdateLayout();
    }
    public void Dispose() => ui.Dispose();
}
