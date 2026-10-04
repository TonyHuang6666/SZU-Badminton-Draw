using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using Avalonia.Controls;

namespace BadmintonDraw.Desktop;

public partial class AppShellWindow
{
    private ScheduleBoardWindow? boardWindow;
    private PlayerEntriesWindow? entriesWindow;

    private void ConfigureScheduleBoardWindow(AppShellViewModel shell)
    {
        shell.BoardWindowRequested += ShowScheduleBoard;
        shell.PlayerEntriesWindowRequested += ShowPlayerEntries;
        shell.BoardWindowInvalidated += CloseScheduleBoard;
        shell.PlayerEntriesWindowInvalidated += ClosePlayerEntries;
        Closed += (_, _) =>
        {
            shell.BoardWindowRequested -= ShowScheduleBoard;
            shell.PlayerEntriesWindowRequested -= ShowPlayerEntries;
            shell.BoardWindowInvalidated -= CloseScheduleBoard;
            shell.PlayerEntriesWindowInvalidated -= ClosePlayerEntries;
            CloseScheduleBoard(); ClosePlayerEntries();
        };
    }
    private void ShowScheduleBoard(ScheduleBoardPageViewModel model)
    {
        if (boardWindow is { } current)
        {
            if (current.WindowState == WindowState.Minimized) current.WindowState = WindowState.Normal;
            current.Activate();
            return;
        }
        var window = new ScheduleBoardWindow { DataContext = model, Icon = Icon };
        boardWindow = window;
        window.Closed += (_, _) =>
        {
            // The shell owns the live model. Closing this view must not dispose it.
            if (ReferenceEquals(boardWindow, window)) boardWindow = null;
            window.DataContext = null;
        };
        window.Show(this);
    }
    private void ShowPlayerEntries(PlayerEntriesViewModel model)
    {
        if (entriesWindow is { } current)
        {
            if (current.WindowState == WindowState.Minimized) current.WindowState = WindowState.Normal;
            current.Activate(); return;
        }
        var window = new PlayerEntriesWindow { DataContext = model, Icon = Icon };
        entriesWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(entriesWindow, window)) entriesWindow = null;
            window.DataContext = null;
        };
        window.Show(this);
    }
    private void CloseScheduleBoard() => boardWindow?.Close();
    private void ClosePlayerEntries() => entriesWindow?.Close();
}
