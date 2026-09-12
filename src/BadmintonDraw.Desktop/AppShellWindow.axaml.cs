using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop;

public partial class AppShellWindow : Window
{
    private static readonly FilePickerFileType WorkspaceFileType = new("v5 赛事工作区") { Patterns = ["*.szbd"] };
    public AppShellWindow()
    {
        InitializeComponent();
        var shell = new AppShellViewModel(new TournamentWorkspaceWorkflow(), PickOpenPathAsync, PickSavePathAsync,
            new RecentWorkspaceStore(RecentWorkspaceStore.DefaultPath), action => Dispatcher.UIThread.Post(action));
        DataContext = shell;
        Closed += (_, _) => shell.Dispose();
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://BadmintonDraw.Desktop/Assets/szuba-app-icon.ico"));
            Icon = new WindowIcon(stream);
        }
        catch (IOException) { /* The application bundle retains its platform icon. */ }
    }
    private async Task<string?> PickOpenPathAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开 v5 赛事工作区", AllowMultiple = false, FileTypeFilter = [WorkspaceFileType]
        });
        return LocalPath(files.FirstOrDefault());
    }
    private async Task<string?> PickSavePathAsync(string name)
    {
        var safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存新的赛事工作区", SuggestedFileName = safeName + ".szbd", DefaultExtension = "szbd",
            FileTypeChoices = [WorkspaceFileType]
        });
        return LocalPath(file);
    }
    private static string? LocalPath(IStorageItem? item) => item is null ? null : item.TryGetLocalPath()
        ?? throw new IOException("请选择本地文件位置。");
}
