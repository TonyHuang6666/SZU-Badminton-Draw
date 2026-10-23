using Avalonia.Controls;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;

namespace BadmintonDraw.Desktop;

public partial class AppShellWindow
{
    private OnsiteRecordWindow? onsiteRecordWindow;
    private bool allowCloseWithOnsiteRecords;

    private void ConfigureOnsiteRecordWindow(AppShellViewModel shell)
    {
        shell.OnsiteRecordWindowRequested += ShowOnsiteRecords;
        shell.OnsiteRecordWindowInvalidated += CloseOnsiteRecords;
        Closing += OnShellClosingForOnsiteRecords;
        Closed += (_, _) =>
        {
            shell.OnsiteRecordWindowRequested -= ShowOnsiteRecords;
            shell.OnsiteRecordWindowInvalidated -= CloseOnsiteRecords;
            Closing -= OnShellClosingForOnsiteRecords;
            CloseOnsiteRecords();
        };
    }

    private void ShowOnsiteRecords(OnsiteRecordViewModel model)
    {
        if (onsiteRecordWindow is { } current)
        {
            if (current.WindowState == WindowState.Minimized) current.WindowState = WindowState.Normal;
            current.Activate();
            return;
        }
        var window = new OnsiteRecordWindow { DataContext = model, Icon = Icon };
        onsiteRecordWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(onsiteRecordWindow, window)) onsiteRecordWindow = null;
            // The shell retains the model and filters for reopening within this tournament.
            window.DataContext = null;
        };
        window.Show(this);
    }

    private void CloseOnsiteRecords() => onsiteRecordWindow?.CloseForInvalidation();

    private async void OnShellClosingForOnsiteRecords(object? sender, WindowClosingEventArgs args)
    {
        if (allowCloseWithOnsiteRecords || onsiteRecordWindow is not { } window || !window.NeedsFlush) return;
        args.Cancel = true;
        if (!await window.FlushForCloseAsync()) return;
        allowCloseWithOnsiteRecords = true;
        Close();
    }
}
