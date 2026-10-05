using System.Collections.ObjectModel;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class StartPageViewModel(AppShellViewModel shell) : ViewModelBase
{
    public DelegateCommand NewCommand => shell.NewCommand;
    public AsyncCommand OpenCommand => shell.OpenCommand;
    public ObservableCollection<RecentWorkspaceViewModel> RecentWorkspaces { get; } = [];
    public bool HasRecentWorkspaces => RecentWorkspaces.Count > 0;
    public bool HasNoRecentWorkspaces => !HasRecentWorkspaces;
    public void UpdateRecentWorkspaces(IEnumerable<RecentWorkspaceEntry> entries)
    {
        RecentWorkspaces.Clear();
        foreach (var entry in entries) RecentWorkspaces.Add(new(entry, shell));
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
    private readonly DateTimeOffset? lastOpenedAt;
    private readonly DateTimeOffset? lastSavedAt;
    public string Path { get; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public AsyncCommand OpenCommand { get; }
    public DelegateCommand RemoveCommand { get; }
    public string Location => System.IO.Path.GetDirectoryName(Path) ?? Path;
    public bool IsMissing => !File.Exists(Path);
    public string Availability => IsMissing ? "文件已移动或暂时不可用" : LatestActivity() switch
    {
        ("上次打开", { } time) => $"上次打开 · {FormatRelative(time)}",
        ("上次保存", { } time) => $"上次保存 · {FormatRelative(time)}",
        _ => "本地赛事文件 · 时间未记录"
    };
    public string Details
    {
        get
        {
            var lines = new List<string> { Path };
            if (lastOpenedAt is { } opened) lines.Add("上次打开：" + FormatFull(opened));
            if (lastSavedAt is { } saved) lines.Add("上次保存：" + FormatFull(saved));
            return string.Join(Environment.NewLine, lines);
        }
    }
    public RecentWorkspaceViewModel(RecentWorkspaceEntry entry, AppShellViewModel shell)
    {
        Path = entry.Path; lastOpenedAt = entry.LastOpenedAt; lastSavedAt = entry.LastSavedAt;
        OpenCommand = new(async () => await shell.OpenWorkspaceAsync(Path), () => !shell.IsBusy, shell.ReportError);
        RemoveCommand = new(() => shell.RemoveRecentWorkspace(Path), () => !shell.IsBusy);
    }
    public RecentWorkspaceViewModel(string path, AppShellViewModel shell) : this(new RecentWorkspaceEntry(path), shell) { }
    private (string? Label, DateTimeOffset? Time) LatestActivity() =>
        lastOpenedAt is null ? (lastSavedAt is null ? (null, null) : ("上次保存", lastSavedAt)) :
        lastSavedAt is null || lastOpenedAt >= lastSavedAt ? ("上次打开", lastOpenedAt) : ("上次保存", lastSavedAt);
    private static string FormatRelative(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (local.Date == now.Date) return $"今天 {local:HH:mm}";
        if (local.Date == now.Date.AddDays(-1)) return $"昨天 {local:HH:mm}";
        return local.Year == now.Year ? $"{local:M月d日 HH:mm}" : $"{local:yyyy年M月d日 HH:mm}";
    }
    private static string FormatFull(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
}
