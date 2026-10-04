using Avalonia.Controls;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.ViewModels;

namespace BadmintonDraw.Desktop.Views;

public partial class ScheduleBoardWindow : Window
{
    private ScheduleBoardPageViewModel? page;
    public ScheduleBoardWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindPage();
        Closed += (_, _) => UnbindPage();
    }
    private void BindPage()
    {
        UnbindPage(); page = DataContext as ScheduleBoardPageViewModel;
        if (page is null) return;
        page.FocusRequested += ActivateBoard;
    }
    private void UnbindPage()
    {
        if (page is not null)
        {
            page.FocusRequested -= ActivateBoard;
        }
        page = null;
    }
    private void ActivateBoard(WorkspaceMatchKey key)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
