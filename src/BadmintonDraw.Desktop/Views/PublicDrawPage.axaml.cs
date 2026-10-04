using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BadmintonDraw.Desktop.ViewModels;
namespace BadmintonDraw.Desktop.Views;
public partial class PublicDrawPage : UserControl
{
    public PublicDrawPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => PendingDrawSummaryButton.Flyout?.Hide();
        DetachedFromVisualTree += (_, _) => PendingDrawSummaryButton.Flyout?.Hide();
        PendingDrawSummaryButton.PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && !PendingDrawSummaryButton.IsVisible)
                PendingDrawSummaryButton.Flyout?.Hide();
        };
    }

    private void ReviewPendingDrawClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: ProjectDrawViewModel project } || DataContext is not PublicDrawPageViewModel page) return;
        // The Click event precedes command execution. Scroll after the command selected this project,
        // and ignore stale events from a page or project that has since been replaced.
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(DataContext, page) && ReferenceEquals(page.SelectedProject, project) && project.ReviewDrawCommand.CanExecute(null))
            {
                PendingDrawSummaryButton.Flyout?.Hide();
                DrawContent.Offset = default;
            }
        });
    }
}
