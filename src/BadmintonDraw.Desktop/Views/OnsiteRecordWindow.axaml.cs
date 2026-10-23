using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.ViewModels;

namespace BadmintonDraw.Desktop.Views;

public partial class OnsiteRecordWindow : Window
{
    private bool allowClose;
    private Task<bool>? closingFlush;
    private bool openingDayConfirmation;
    private OnsiteRecordViewModel? Model => DataContext as OnsiteRecordViewModel;
    public bool NeedsFlush => Model is { HasDrafts: true } or { IsWorking: true };

    public OnsiteRecordWindow()
    {
        InitializeComponent();
        AddHandler(InputElement.LostFocusEvent, OnEditorLostFocus, RoutingStrategies.Bubble, true);
        AddHandler(InputElement.KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Bubble, true);
        Deactivated += OnWindowDeactivated;
        Closing += OnClosing;
    }

    private void OnEditorLostFocus(object? sender, RoutedEventArgs args)
    {
        if (FindRow(args.Source) is not { HasDraft: true } row) return;
        // Focus can move to another field or a ComboBox popup within the same row.
        // Wait until the new focus is known before deciding that the row is complete.
        Dispatcher.UIThread.Post(async () =>
        {
            if (allowClose || !IsVisible || Model is not { } model || HasOpenDropdown(row) ||
                ReferenceEquals(FindRow(FocusManager?.GetFocusedElement()), row)) return;
            await model.SaveRowAsync(row);
        }, DispatcherPriority.Background);
    }

    private async void OnEditorKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || HasOpenDropdown() || FindRow(args.Source) is not { } row || Model is not { } model) return;
        args.Handled = true;
        await model.SaveRowAsync(row);
    }

    private async void OnWindowDeactivated(object? sender, EventArgs args)
    {
        if (!allowClose && !HasOpenDropdown() && Model is { } model && NeedsFlush)
            await model.FlushAsync();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (allowClose || !NeedsFlush) return;
        args.Cancel = true;
        if (!await FlushForCloseAsync()) return;
        allowClose = true;
        Close();
    }

    public async Task<bool> FlushForCloseAsync()
    {
        if (closingFlush is { } pending) return await pending;
        if (Model is not { } model) return true;
        closingFlush = model.FlushAsync();
        try
        {
            var saved = await closingFlush && !model.HasDrafts && !model.IsWorking;
            this.FindControl<TextBlock>("OnsiteCloseWarning")!.IsVisible = !saved;
            if (!saved) Activate();
            return saved;
        }
        finally { closingFlush = null; }
    }

    internal void CloseForInvalidation()
    {
        allowClose = true;
        DataContext = null;
        Close();
    }

    private void OnDiscardDrafts(object? sender, RoutedEventArgs args)
    {
        Model?.DiscardDrafts();
        this.FindControl<TextBlock>("OnsiteCloseWarning")!.IsVisible = false;
    }

    private void OnDiscardRow(object? sender, RoutedEventArgs args)
    {
        if (FindRow(sender) is { } row) Model?.DiscardRow(row);
        if (Model is { HasDrafts: false }) this.FindControl<TextBlock>("OnsiteCloseWarning")!.IsVisible = false;
    }

    private async void OnRetrySave(object? sender, RoutedEventArgs args)
    {
        if (Model is { } model && await model.FlushAsync())
            this.FindControl<TextBlock>("OnsiteCloseWarning")!.IsVisible = false;
    }

    private async void OnConfirmDay(object? sender, RoutedEventArgs args)
    {
        if (openingDayConfirmation || Model is not { IsWorking: false } model) return;
        openingDayConfirmation = true;
        try
        {
            if (await model.PrepareDayConfirmationAsync() is { } confirmation)
                await new OnsiteDayConfirmationWindow { DataContext = confirmation }.ShowDialog(this);
        }
        finally { openingDayConfirmation = false; }
    }

    private bool HasOpenDropdown(OnsiteRecordRowViewModel? row = null) =>
        this.GetVisualDescendants().OfType<ComboBox>().Any(combo => combo.IsDropDownOpen &&
            (row is null || ReferenceEquals(combo.DataContext, row)));

    private static OnsiteRecordRowViewModel? FindRow(object? source)
    {
        if (source is StyledElement { DataContext: OnsiteRecordRowViewModel row }) return row;
        return (source as Visual)?.GetVisualAncestors().OfType<StyledElement>()
            .Select(element => element.DataContext).OfType<OnsiteRecordRowViewModel>().FirstOrDefault();
    }
}
