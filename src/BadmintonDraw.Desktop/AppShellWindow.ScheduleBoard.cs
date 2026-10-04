using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using Avalonia.Controls;

namespace BadmintonDraw.Desktop;

public partial class AppShellWindow
{
    private ScheduleBoardWindow? boardWindow;

    private void ConfigureScheduleBoardWindow(AppShellViewModel shell)
    {
        shell.BoardWindowRequested += ShowScheduleBoard;
        shell.BoardWindowInvalidated += CloseScheduleBoard;
        Closed += (_, _) =>
        {
            shell.BoardWindowRequested -= ShowScheduleBoard;
            shell.BoardWindowInvalidated -= CloseScheduleBoard;
            CloseScheduleBoard();
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
    private void CloseScheduleBoard() => boardWindow?.Close();
}
