using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop;

public partial class AppShellWindow : Window
{
    private static readonly FilePickerFileType WorkspaceFileType = new("v5 赛事工作区") { Patterns = ["*.szbd"] };
    private static readonly FilePickerFileType RosterFileType = new("Excel 名单") { Patterns = ["*.xlsx"] };
    public AppShellWindow()
    {
        InitializeComponent();
        var shell = new AppShellViewModel(new TournamentWorkspaceWorkflow(), PickOpenPathAsync, PickSavePathAsync,
            new RecentWorkspaceStore(RecentWorkspaceStore.DefaultPath), action => Dispatcher.UIThread.Post(action));
        shell.RegisterPageFactory(WorkspaceRoute.Rosters, session => new RostersPageViewModel(shell, session, PickRosterPathAsync, PickTemplatePathAsync));
        shell.RegisterPageFactory(WorkspaceRoute.PublicDraw, session => new PublicDrawPageViewModel(shell, session, PickDrawOutputDirectoryAsync));
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
    private async Task<string?> PickRosterPathAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "导入当前项目名单（不会自动抽签）", AllowMultiple = false, FileTypeFilter = [RosterFileType] });
        return LocalPath(files.FirstOrDefault());
    }
    private async Task<string?> PickTemplatePathAsync(string name)
    {
        var safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return LocalPath(await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        { Title = "导出名单模板", SuggestedFileName = safeName + ".xlsx", DefaultExtension = "xlsx", FileTypeChoices = [RosterFileType] }));
    }
    private async Task<string?> PickDrawOutputDirectoryAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "选择抽签材料导出目录", AllowMultiple = false });
        return LocalPath(folders.FirstOrDefault());
    }
}
