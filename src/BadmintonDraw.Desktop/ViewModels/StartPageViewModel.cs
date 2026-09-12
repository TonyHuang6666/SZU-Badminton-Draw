using System.Collections.ObjectModel;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class StartPageViewModel(AppShellViewModel shell) : ViewModelBase
{
    public DelegateCommand NewCommand => shell.NewCommand;
    public AsyncCommand OpenCommand => shell.OpenCommand;
    public ObservableCollection<RecentWorkspaceViewModel> RecentWorkspaces { get; } = [];
    public bool HasRecentWorkspaces => RecentWorkspaces.Count > 0;
    public void UpdateRecentWorkspaces(IEnumerable<string> paths)
    {
        RecentWorkspaces.Clear();
        foreach (var path in paths) RecentWorkspaces.Add(new(path, shell));
        OnPropertyChanged(nameof(HasRecentWorkspaces));
    }
    public void RefreshAvailability()
    {
        foreach (var recent in RecentWorkspaces) recent.OpenCommand.NotifyCanExecuteChanged();
    }
}

public sealed class RecentWorkspaceViewModel
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public AsyncCommand OpenCommand { get; }
    public RecentWorkspaceViewModel(string path, AppShellViewModel shell)
    {
        Path = path;
        OpenCommand = new(async () => await shell.OpenWorkspaceAsync(path), () => !shell.IsBusy, shell.ReportError);
    }
}
