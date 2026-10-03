using Avalonia.Controls;
using Avalonia.Interactivity;
using BadmintonDraw.Desktop.ViewModels;

namespace BadmintonDraw.Desktop.Views;

public partial class DrawExportOptionsDialog : Window
{
    public DrawExportOptionsDialog() : this(new DrawExportOptionsViewModel([])) { }

    public DrawExportOptionsDialog(DrawExportOptionsViewModel model)
    {
        InitializeComponent();
        ScopeSelector.ContainerPrepared += (_, args) =>
        {
            if (args.Index >= 0 && args.Index < model.Scopes.Count)
                args.Container.IsEnabled = model.Scopes[args.Index].CanExport;
        };
        DataContext = model;
        Opened += (_, _) => CancelButton.Focus();
    }

    private void CancelClicked(object? sender, RoutedEventArgs args) => Close(false);

    private void ExportClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is DrawExportOptionsViewModel model && model.TryCreateSelection(out _)) Close(true);
    }
}
