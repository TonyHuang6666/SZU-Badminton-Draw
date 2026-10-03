using System.Collections.ObjectModel;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class StartPageViewModel(AppShellViewModel shell) : ViewModelBase
{
    public DelegateCommand NewCommand => shell.NewCommand;
    public AsyncCommand OpenCommand => shell.OpenCommand;
    public DelegateCommand OpenRecoveryCommand => shell.OpenRecoveryCommand;
    public ObservableCollection<RecentWorkspaceViewModel> RecentWorkspaces { get; } = [];
    public bool HasRecentWorkspaces => RecentWorkspaces.Count > 0;
    public bool HasNoRecentWorkspaces => !HasRecentWorkspaces;
    public void UpdateRecentWorkspaces(IEnumerable<string> paths)
    {
        RecentWorkspaces.Clear();
        foreach (var path in paths) RecentWorkspaces.Add(new(path, shell));
        OnPropertyChanged(nameof(HasRecentWorkspaces));
        OnPropertyChanged(nameof(HasNoRecentWorkspaces));
    }
    public void RefreshAvailability()
    {
        foreach (var recent in RecentWorkspaces) { recent.OpenCommand.NotifyCanExecuteChanged(); recent.RemoveCommand.NotifyCanExecuteChanged(); }
    }
}

public sealed class RecentWorkspaceViewModel
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public AsyncCommand OpenCommand { get; }
    public DelegateCommand RemoveCommand { get; }
    public string Location => System.IO.Path.GetDirectoryName(Path) ?? Path;
    public bool IsMissing => !File.Exists(Path);
    public string Availability => IsMissing ? "文件已移动或暂时不可用" : "本地赛事文件 · 点击继续";
    public RecentWorkspaceViewModel(string path, AppShellViewModel shell)
    {
        Path = path;
        OpenCommand = new(async () => await shell.OpenWorkspaceAsync(path), () => !shell.IsBusy, shell.ReportError);
        RemoveCommand = new(() => shell.RemoveRecentWorkspace(path), () => !shell.IsBusy);
    }
}
