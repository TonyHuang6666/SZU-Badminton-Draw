using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class VenueCourtSelectionDialogTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData("VenueCourtSelectionDialog", false)]
    [InlineData("VenueCourtSelectionDialog", true)]
    [InlineData("UnavailableCourtSelectionDialog", false)]
    [InlineData("UnavailableCourtSelectionDialog", true)]
    public Task SmallWindowKeepsCancelFocusedAndActionsVisible(string dialogName, bool dark) => ui.Dispatch(() =>
    {
        var dialog = CreateDialog(dialogName, large: true);
        dialog.Width = 440;
        dialog.Height = 400;
        dialog.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            dialog.Show();
            if (dialogName == "VenueCourtSelectionDialog") dialog.FindControl<ComboBox>("VenueSelector")!.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();
            var cancel = dialog.FindControl<Button>("CancelButton")!;
            var accept = dialog.FindControl<Button>("AcceptButton")!;
            Assert.True(cancel.IsFocused);
            Assert.True(cancel.IsDefault);
            Assert.False(accept.IsDefault);
            var position = accept.TranslatePoint(default, dialog)!.Value;
            Assert.True(position.Y + accept.Bounds.Height <= dialog.ClientSize.Height);
            Assert.True(dialog.FindControl<ScrollViewer>("SettingsScroll")!.Viewport.Height > 70);
            var scroll = dialog.FindControl<ScrollViewer>("SettingsScroll")!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        }
        finally { dialog.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData("VenueCourtSelectionDialog", "accept", true)]
    [InlineData("VenueCourtSelectionDialog", "cancel", false)]
    [InlineData("VenueCourtSelectionDialog", "escape", false)]
    [InlineData("VenueCourtSelectionDialog", "close", false)]
    [InlineData("VenueCourtSelectionDialog", "enter", false)]
    [InlineData("UnavailableCourtSelectionDialog", "accept", true)]
    [InlineData("UnavailableCourtSelectionDialog", "cancel", false)]
    [InlineData("UnavailableCourtSelectionDialog", "escape", false)]
    [InlineData("UnavailableCourtSelectionDialog", "close", false)]
    [InlineData("UnavailableCourtSelectionDialog", "enter", false)]
    public Task OnlyExplicitUseAcceptsTheSelection(string dialogName, string action, bool expected) => ui.Dispatch(async () =>
    {
        var owner = new Window();
        var dialog = CreateDialog(dialogName);
        try
        {
            owner.Show();
            var result = dialog.ShowDialog<bool>(owner);
            if (dialogName == "VenueCourtSelectionDialog")
            {
                dialog.FindControl<ComboBox>("VenueSelector")!.SelectedIndex = 3;
                dialog.FindControl<TextBox>("CustomCourtsInput")!.Text = "球场1\n球场2";
            }
            else dialog.FindControl<CheckBox>("AllCourtsToggle")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            if (action is "accept" or "cancel") Click(dialog, action == "accept" ? "AcceptButton" : "CancelButton");
            else if (action == "close") dialog.Close();
            else
            {
                var key = action == "escape" ? Key.Escape : Key.Enter;
                var physical = action == "escape" ? PhysicalKey.Escape : PhysicalKey.Enter;
                dialog.KeyPress(key, RawInputModifiers.None, physical, null);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.True(result.IsCompleted);
            Assert.Equal(expected, await result);
        }
        finally { dialog.Close(); owner.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task PresetGroupsExposeNaturalOrderAndGroupSelection() => ui.Dispatch(() =>
    {
        var dialog = CreateDialog("VenueCourtSelectionDialog");
        try
        {
            dialog.Show();
            dialog.FindControl<ComboBox>("VenueSelector")!.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            var groups = dialog.FindControl<ItemsControl>("PresetGroups")!;
            var choices = groups.GetVisualDescendants().OfType<CheckBox>().ToArray();
            Assert.Equal(32, choices.Length);
            Assert.Equal(new[] { "A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8", "B1" },
                choices.Take(9).Select(choice => choice.Content?.ToString()));
            Assert.All(choices, choice => Assert.False(choice.IsChecked));
            Assert.All(choices, choice => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(choice))));
            Activate(dialog, groups.GetVisualDescendants().OfType<Button>().First());
            Dispatcher.UIThread.RunJobs();
            Assert.All(choices.Take(8), choice => Assert.True(choice.IsChecked));
            Assert.All(choices.Skip(8), choice => Assert.False(choice.IsChecked));
            Assert.Contains("8", dialog.FindControl<TextBlock>("SelectionSummaryText")!.Text);
            Assert.Contains("32", dialog.FindControl<TextBlock>("SelectionSummaryText")!.Text);
            Activate(dialog, groups.GetVisualDescendants().OfType<Button>().Skip(1).First());
            Dispatcher.UIThread.RunJobs();
            Assert.All(choices, choice => Assert.False(choice.IsChecked));
            Click(dialog, "SelectAllButton");
            Assert.All(choices, choice => Assert.True(choice.IsChecked));
            Click(dialog, "ClearButton");
            Assert.All(choices, choice => Assert.False(choice.IsChecked));
            Assert.False(dialog.FindControl<Button>("AcceptButton")!.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(dialog.FindControl<TextBlock>("ValidationText")!.Text));
        }
        finally { dialog.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task CustomInputsEnableUseAndGenerateNumberedCourts() => ui.Dispatch(() =>
    {
        var dialog = CreateDialog("VenueCourtSelectionDialog");
        try
        {
            dialog.Show();
            dialog.FindControl<ComboBox>("VenueSelector")!.SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            var input = dialog.FindControl<TextBox>("CustomCourtsInput")!;
            Assert.True(input.IsEffectivelyVisible);
            Assert.True(input.AcceptsReturn);
            input.Text = "北场，南场";
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.FindControl<Button>("AcceptButton")!.IsEnabled);
            dialog.FindControl<TextBox>("NumberedCourtCountInput")!.Text = "3";
            Click(dialog, "GenerateNumberedCourtsButton");
            Assert.Equal(new[] { "1号场", "2号场", "3号场" }, input.Text!.Split('\n', StringSplitOptions.TrimEntries));
            input.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.FindControl<Button>("AcceptButton")!.IsEnabled);
            Click(dialog, "AcceptButton");
            Assert.True(dialog.IsVisible);
        }
        finally { dialog.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task UnavailableAllCourtsDisablesIndividualChoicesAndEmptyCannotAccept() => ui.Dispatch(() =>
    {
        var dialog = CreateDialog("UnavailableCourtSelectionDialog");
        try
        {
            dialog.Show();
            dialog.FindControl<CheckBox>("AllCourtsToggle")!.IsChecked = false;
            Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            var choices = dialog.FindControl<ItemsControl>("CourtChoices")!.GetVisualDescendants().OfType<CheckBox>().ToArray();
            Assert.Equal(new[] { "场地1", "场地2", "场地10" }, choices.Select(choice => Assert.IsType<TextBlock>(choice.Content).Text));
            Assert.False(dialog.FindControl<Button>("AcceptButton")!.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(dialog.FindControl<TextBlock>("ValidationText")!.Text));
            choices[1].IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.FindControl<Button>("AcceptButton")!.IsEnabled);
            dialog.FindControl<CheckBox>("AllCourtsToggle")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.All(choices, choice => Assert.False(choice.IsEffectivelyEnabled));
            Assert.True(dialog.FindControl<Button>("AcceptButton")!.IsEnabled);
            dialog.FindControl<CheckBox>("AllCourtsToggle")!.IsChecked = false;
            choices[1].IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.FindControl<Button>("AcceptButton")!.IsEnabled);
            Click(dialog, "AcceptButton");
            Assert.True(dialog.IsVisible);
        }
        finally { dialog.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task ChangedCourtsRequireExplicitConfirmationOfAffectedUnavailableWindows() => ui.Dispatch(() =>
    {
        var model = new VenueCourtSelectionViewModel(["原场1", "原场2"], [new("上午停用", ["原场1"])]);
        var dialog = new VenueCourtSelectionDialog(model);
        try
        {
            dialog.Show();
            dialog.FindControl<ComboBox>("VenueSelector")!.SelectedIndex = 3;
            dialog.FindControl<TextBox>("CustomCourtsInput")!.Text = "新场1";
            Dispatcher.UIThread.RunJobs();
            var confirm = dialog.FindControl<CheckBox>("ConfirmAdjustUnavailableToggle")!;
            Assert.True(confirm.IsEffectivelyVisible);
            Assert.False(confirm.IsChecked);
            Assert.Contains("上午停用", dialog.FindControl<TextBlock>("AffectedWindowsText")!.Text);
            Assert.False(dialog.FindControl<Button>("AcceptButton")!.IsEnabled);
            Click(dialog, "AcceptButton");
            Assert.True(dialog.IsVisible);
            confirm.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.FindControl<Button>("AcceptButton")!.IsEnabled);
            Assert.True(model.TryCreateSelection(out var courts));
            Assert.Equal(new[] { "新场1" }, courts);
        }
        finally { dialog.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LongUnknownCourtWarningsKeepTheBodyAndActionsUsable(bool dark) => ui.Dispatch(() =>
    {
        var unknownCourts = Enumerable.Range(1, 32)
            .Select(index => $"原场馆中尚未纳入当天清单的旧羽毛球场地编号{index}号，需由组织者重新核对预约信息")
            .ToArray();
        var model = new UnavailableCourtSelectionViewModel(["当天1号场", "当天2号场"], unknownCourts);
        var dialog = new UnavailableCourtSelectionDialog(model)
        {
            Width = 440, Height = 400,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        try
        {
            dialog.Show();
            Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            Assert.Contains(unknownCourts[31], dialog.FindControl<TextBlock>("ValidationText")!.Text);
            Assert.True(dialog.FindControl<ScrollViewer>("SettingsScroll")!.Viewport.Height >= 80);
            foreach (var name in new[] { "CancelButton", "AcceptButton" })
            {
                var button = dialog.FindControl<Button>(name)!;
                var position = button.TranslatePoint(default, dialog)!.Value;
                Assert.True(position.Y + button.Bounds.Height <= dialog.ClientSize.Height);
            }
            var warningScroll = dialog.FindControl<ScrollViewer>("ValidationScroll")!;
            Assert.True(warningScroll.Extent.Height > warningScroll.Viewport.Height);
            warningScroll.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            Assert.True(warningScroll.Offset.Y > 0);
            Assert.False(dialog.FindControl<Button>("AcceptButton")!.IsEnabled);
            Assert.True(dialog.FindControl<Button>("CancelButton")!.IsFocused);
        }
        finally { dialog.Close(); }
    }, CancellationToken.None);

    private static Window CreateDialog(string dialogName, bool large = false) => dialogName switch
    {
        "VenueCourtSelectionDialog" => new VenueCourtSelectionDialog(),
        "UnavailableCourtSelectionDialog" => new UnavailableCourtSelectionDialog(new UnavailableCourtSelectionViewModel(
            large ? Enumerable.Range(1, 32).Select(index => $"场地{index}").ToArray() : ["场地1", "场地2", "场地10"], [])),
        _ => throw new ArgumentOutOfRangeException(nameof(dialogName))
    };

    private static void Click(Window dialog, string name)
    {
        var button = dialog.FindControl<Button>(name)!;
        if (button.Command is not null) Activate(dialog, button);
        else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Activate(Window dialog, Button button)
    {
        Assert.True(button.Focus());
        dialog.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        dialog.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();
    }

    public void Dispose() => ui.Dispose();
}
