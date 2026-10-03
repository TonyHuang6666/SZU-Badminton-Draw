using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed partial class AppShellViewModel
{
    public bool ShowWorkspaceContext => HasWorkspace && CurrentPage is WorkspacePageViewModel;
    public bool NeedsReload => ShowWorkspaceContext && (CurrentSession?.RequiresReload == true || LastError?.Code == "RevisionConflict");
    public bool ShowDetails => LastError is not null || ShowWorkspaceContext;
    public string SaveStateText => IsBusy ? "正在处理…" : NeedsReload ? "需要重新读取" : LastError is not null ? "请核对操作结果" : "赛事已保存";
    public string StatusSymbol => LastError is not null ? "!" : IsBusy ? "○" : "✓";
    public string StatusSummary => LastError is { } error
        ? error.Committed ? "文件已经保存，但后续读取或确认未完成。请重新读取，并查看操作详情。"
        : error.Code switch
        {
            "InvalidWorkspace" => "无法打开这个赛事文件。请检查文件位置，或从备份恢复。",
            "UnsupportedWorkspaceVersion" => "这个文件来自不兼容的版本，请用原版本打开。详情中保留了版本信息。",
            "RevisionConflict" => "赛事已被另一个窗口更新。请重新读取后再继续。",
            "desktop.operation-failed" => "这次操作未能完成。请查看操作详情，核对文件位置和访问权限。",
            _ => error.Message
        }
        : !ShowWorkspaceContext && !IsBusy ? Navigator.CurrentRoute == WorkspaceRoute.NewWorkspace
            ? "先创建赛事文件，再准备名单和公开抽签。" : "所有赛事数据保存在本机，随时可以继续。"
        : Status.Contains(" 备份：", StringComparison.Ordinal) ? Status[..Status.IndexOf(" 备份：", StringComparison.Ordinal)] : Status;

    public DelegateCommand ToggleThemeCommand { get; }
    public DelegateCommand NextActionCommand { get; }
    private WorkspaceRoute? NextActionRoute => ShowWorkspaceContext && CurrentSession is { } session ? Navigator.CurrentRoute switch
    {
        WorkspaceRoute.ScheduleBoard => WorkspaceRoute.Operations,
        _ => null
    } : null;
    public bool HasNextAction => NextActionRoute is not null;
    public string NextActionLabel => NextActionRoute switch
    {
        WorkspaceRoute.Operations => "准备现场比赛材料 →",
        _ => ""
    };
    public string NextActionHint => NextActionRoute switch
    {
        WorkspaceRoute.Operations => "赛程已保存。下一步准备计分表和每日比赛安排。",
        _ => ""
    };
    private void RefreshPresentation()
    {
        foreach (var name in new[] { nameof(ShowWorkspaceContext), nameof(Title), nameof(StageText), nameof(NeedsReload), nameof(ShowDetails), nameof(SaveStateText), nameof(StatusSymbol), nameof(StatusSummary), nameof(HasNextAction), nameof(NextActionLabel), nameof(NextActionHint) }) OnPropertyChanged(name);
    }

    public void RemoveRecentWorkspace(string path)
    {
        if (IsBusy || disposed) return;
        var remaining = recentPaths.Where(p => p != path).ToArray();
        try
        {
            recentStore.Save(remaining);
            recentPaths.Clear(); recentPaths.AddRange(remaining);
            StartPage.UpdateRecentWorkspaces(recentPaths);
        }
        catch (Exception exception) { ReportError(exception); }
    }
}
