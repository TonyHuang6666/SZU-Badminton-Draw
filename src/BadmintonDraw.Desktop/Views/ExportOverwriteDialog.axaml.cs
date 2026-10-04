using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BadmintonDraw.Desktop.Views;

public partial class ExportOverwriteDialog : Window
{
    public ExportOverwriteDialog() : this(Array.Empty<string>()) { }

    public ExportOverwriteDialog(IReadOnlyList<string> paths)
    {
        InitializeComponent();
        FileCount.Text = $"导出位置中已有 {paths.Count} 个文件：";
        ExistingFiles.ItemsSource = paths.ToArray();
        Opened += (_, _) => CancelButton.Focus();
    }

    private void CancelClicked(object? sender, RoutedEventArgs args) => Close(false);

    private void ReplaceClicked(object? sender, RoutedEventArgs args) => Close(true);
}
