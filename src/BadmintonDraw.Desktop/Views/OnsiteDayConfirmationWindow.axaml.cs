using Avalonia.Controls;
using Avalonia.Interactivity;
using BadmintonDraw.Desktop.ViewModels;

namespace BadmintonDraw.Desktop.Views;

public partial class OnsiteDayConfirmationWindow : Window
{
    private bool committing;
    private OnsiteDayConfirmationViewModel? Model => DataContext as OnsiteDayConfirmationViewModel;

    public OnsiteDayConfirmationWindow()
    {
        InitializeComponent();
        Closing += (_, args) => args.Cancel = committing || Model is { IsWorking: true };
    }

    private async void OnPreview(object? sender, RoutedEventArgs args)
    {
        if (Model is { CanPreview: true, IsWorking: false } model) await model.PreviewAsync();
    }

    private async void OnConfirm(object? sender, RoutedEventArgs args)
    {
        if (committing || Model is not { CanConfirm: true, IsWorking: false } model) return;
        committing = true;
        try { await model.ConfirmAsync(); }
        finally { committing = false; }
        if (model.IsCompleted) Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs args)
    {
        if (!committing && Model is not { IsWorking: true }) Close();
    }
}
