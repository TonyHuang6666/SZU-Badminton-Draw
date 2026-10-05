using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Workflows.Templates;

namespace BadmintonDraw.Desktop.Views;

public partial class TemplateExportWindow : Window
{
    public TemplateExportWindow() : this(null, null) { }

    public TemplateExportWindow(Func<Task<string?>>? outputPicker,
        Func<IReadOnlyList<string>, Task<bool>>? overwriteConfirmation, ITemplateExportWriter? writer = null)
    {
        InitializeComponent();
        var viewModel = new TemplateExportViewModel(outputPicker ?? PickOutputFolderAsync,
            overwriteConfirmation ?? ConfirmOverwriteAsync, writer);
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    private async Task<string?> PickOutputFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "选择模板保存文件夹", AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private Task<bool> ConfirmOverwriteAsync(IReadOnlyList<string> paths) =>
        new ExportOverwriteDialog(paths).ShowDialog<bool>(this);
}
