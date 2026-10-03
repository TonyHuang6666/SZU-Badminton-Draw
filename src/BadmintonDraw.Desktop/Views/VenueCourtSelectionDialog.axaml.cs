using Avalonia.Controls;
using Avalonia.Interactivity;
using BadmintonDraw.Desktop.ViewModels;

namespace BadmintonDraw.Desktop.Views;

public partial class VenueCourtSelectionDialog : Window
{
    public VenueCourtSelectionDialog() : this(new VenueCourtSelectionViewModel([])) { }

    public VenueCourtSelectionDialog(VenueCourtSelectionViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        Opened += (_, _) => CancelButton.Focus();
    }

    private void CancelClicked(object? sender, RoutedEventArgs args) => Close(false);

    private void AcceptClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is VenueCourtSelectionViewModel model && model.TryCreateSelection(out _)) Close(true);
    }
}
