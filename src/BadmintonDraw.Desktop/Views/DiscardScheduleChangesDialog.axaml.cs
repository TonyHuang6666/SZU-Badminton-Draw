using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BadmintonDraw.Desktop.Views;

public partial class DiscardScheduleChangesDialog : Window
{
    public DiscardScheduleChangesDialog()
    {
        InitializeComponent();
        Opened += (_, _) => ContinueButton.Focus();
    }

    private void ContinueClicked(object? sender, RoutedEventArgs args) => Close(false);
    private void DiscardClicked(object? sender, RoutedEventArgs args) => Close(true);
}
