using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class AppShellViewModel : ViewModelBase, IDisposable
{
    private readonly TournamentWorkspaceWorkflow workflow;
    private readonly Func<Task<string?>> openPicker;
    private readonly Func<string, Task<string?>> savePicker;
    private readonly RecentWorkspaceStore recentStore;
    private readonly Action<Action> post;
    private readonly Dictionary<WorkspaceRoute, Func<WorkspaceSession, WorkspacePageViewModel>> factories = [];
    private readonly List<string> recentPaths = [];
    private int busy;
    private bool disposed;
    private string status = "新建或打开赛事工作区。";
    private ViewModelBase currentPage;
    private WorkspaceSession? currentSession;
    private WorkspaceError? lastError;
    public WorkspaceNavigator Navigator { get; } = new();
    public StartPageViewModel StartPage { get; }
    public IReadOnlyList<WorkspaceNavigationItemViewModel> NavigationItems { get; }
    public ViewModelBase CurrentPage { get => currentPage; private set => SetProperty(ref currentPage, value); }
    public WorkspaceSession? CurrentSession { get => currentSession; private set => SetProperty(ref currentSession, value); }
    public WorkspaceError? LastError { get => lastError; private set => SetProperty(ref lastError, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public bool IsBusy => Volatile.Read(ref busy) != 0;
    public bool HasWorkspace => CurrentSession is not null;
    public bool CanMutate => !IsBusy && CurrentSession is { RequiresReload: false };
    public string Title => CurrentSession?.Workspace.Name ?? "深大羽协赛事工作区";
    public string StageText => CurrentSession is { } session ? StageName(session.Workspace.Stage) : "开始";
    public string WorkspacePath => CurrentSession?.WorkspacePath ?? "";
    public DelegateCommand NewCommand { get; }
    public DelegateCommand HomeCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand ReloadCommand { get; }

    public AppShellViewModel(TournamentWorkspaceWorkflow workflow, Func<Task<string?>> openPicker,
        Func<string, Task<string?>> savePicker, RecentWorkspaceStore recentStore, Action<Action> post)
    {
        this.workflow = workflow; this.openPicker = openPicker; this.savePicker = savePicker;
        this.recentStore = recentStore; this.post = post;
        NewCommand = new(StartNewWorkspace, () => !IsBusy);
        HomeCommand = new(() => Navigate(WorkspaceRoute.Start), () => !IsBusy);
        OpenCommand = new(async () => await RunCommandAsync(async () =>
        {
            var path = await this.openPicker();
            return path is null ? null : await Task.Run(() => workflow.OpenWorkspace(path));
        }, "已打开工作区。", remember: true, ensureWorkspacePage: true), () => !IsBusy, ReportError);
        ReloadCommand = new(async () => { if (CurrentSession is { } session) await OpenWorkspaceAsync(session.WorkspacePath); },
            () => !IsBusy && HasWorkspace, ReportError);
        StartPage = new(this); currentPage = StartPage;
        factories[WorkspaceRoute.Overview] = session => new WorkspaceOverviewPageViewModel(this, session);
        NavigationItems = new[]
        {
            new WorkspaceNavigationItemViewModel(this, WorkspaceRoute.Overview, "赛事信息"),
            new WorkspaceNavigationItemViewModel(this, WorkspaceRoute.Rosters, "各项目名单"),
            new WorkspaceNavigationItemViewModel(this, WorkspaceRoute.PublicDraw, "公开抽签"),
            new WorkspaceNavigationItemViewModel(this, WorkspaceRoute.ScheduleSetup, "全局赛程设置"),
            new WorkspaceNavigationItemViewModel(this, WorkspaceRoute.ScheduleBoard, "赛程板"),
            new WorkspaceNavigationItemViewModel(this, WorkspaceRoute.Operations, "现场执行 / 完成")
        };
        try { recentPaths.AddRange(recentStore.Read()); }
        catch (Exception exception) { Status = "最近工作区列表无法读取，可继续新建或打开：" + exception.Message; }
        StartPage.UpdateRecentWorkspaces(recentPaths);
        workflow.SessionChanged += OnSessionChanged;
        if (workflow.CurrentSession is { } session) ApplySession(session);
    }

    public void RegisterPageFactory(WorkspaceRoute route, Func<WorkspaceSession, WorkspacePageViewModel> factory)
    {
        if (route is WorkspaceRoute.Start or WorkspaceRoute.NewWorkspace) throw new ArgumentException("启动页和向导由 Shell 管理。");
        factories[route] = factory;
        RefreshAvailability();
    }
    public bool CanNavigate(WorkspaceRoute route) => !IsBusy && Navigator.CanNavigate(route) &&
        (route is WorkspaceRoute.Start or WorkspaceRoute.NewWorkspace || factories.ContainsKey(route));
    public bool Navigate(WorkspaceRoute route)
    {
        if (!CanNavigate(route)) return false;
        if (route == WorkspaceRoute.NewWorkspace) { StartNewWorkspace(); return true; }
        Navigator.Navigate(route);
        if (CurrentPage is IDisposable disposable) disposable.Dispose();
        CurrentPage = route == WorkspaceRoute.Start ? StartPage : factories[route](CurrentSession!);
        RefreshAvailability();
        return true;
    }
    public void StartNewWorkspace()
    {
        if (IsBusy) return;
        Navigator.Navigate(WorkspaceRoute.NewWorkspace);
        if (CurrentPage is IDisposable disposable) disposable.Dispose();
        CurrentPage = new NewWorkspaceWizardViewModel(async request => await CreateWorkspaceAsync(request),
            savePicker, () => !IsBusy, ReportError);
        RefreshAvailability();
    }
    public Task<bool> CreateWorkspaceAsync(CreateWorkspaceRequest request) => RunCommandAsync(
        async () => await Task.Run(() => workflow.CreateWorkspace(request)), "已创建赛事工作区，下一步准备各项目名单。", remember: true, forceOverview: true);
    public Task<bool> OpenWorkspaceAsync(string path) => RunCommandAsync(
        async () => await Task.Run(() => workflow.OpenWorkspace(path)), "已打开工作区。", remember: true, ensureWorkspacePage: true);
    public Task<bool> RunWorkspaceCommandAsync(WorkspaceSession expectedSession,
        Func<TournamentWorkspaceWorkflow, long, WorkspaceCommandResult> command, string successMessage) => RunCommandAsync(async () =>
        await Task.Run(() =>
        {
            if (!ReferenceEquals(workflow.CurrentSession, expectedSession))
                throw new WorkspaceCommandException(new("workspace.session-changed", "工作区已更新或切换，请检查当前页面后重新操作。"));
            if (expectedSession.RequiresReload)
                throw new WorkspaceCommandException(new("workspace.reload-required", "工作区已保存但无法重新读取，请点击重新载入后再操作。"));
            return command(workflow, expectedSession.Workspace.Revision);
        }), successMessage);

    /// <summary>Read-only work has no save result. Background hover checks keep dragging enabled; quiet internal refreshes retain status but still own busy and report errors.</summary>
    public async Task<WorkspaceQueryResult<T>> RunWorkspaceQueryAsync<T>(WorkspaceSession expectedSession,
        Func<TournamentWorkspaceWorkflow, long, T> query, bool background = false, bool showStatus = true) where T : class
    {
        var ownsBusy = !background && !disposed && Interlocked.CompareExchange(ref busy, 1, 0) == 0;
        if (disposed || (!background && !ownsBusy) || (background && IsBusy))
            return new(false, null, new("desktop.query-busy", "当前操作尚未完成。"));
        if (ownsBusy) { LastError = null; if (showStatus) Status = "正在检查，尚未保存…"; RefreshAvailability(); }
        try
        {
            void CheckSession()
            {
                if (disposed || !ReferenceEquals(workflow.CurrentSession, expectedSession))
                    throw new WorkspaceCommandException(new("workspace.session-changed", "工作区已更新或切换，请重新检查目标位置。"));
                if (expectedSession.RequiresReload)
                    throw new WorkspaceCommandException(new("workspace.reload-required", "请重新载入工作区后再操作。"));
            }
            var value = await Task.Run(() => { CheckSession(); var result = query(workflow, expectedSession.Workspace.Revision); CheckSession(); return result; });
            CheckSession();
            if (ownsBusy && showStatus) Status = "检查完成，尚未保存任何修改。";
            return new(true, value, null);
        }
        catch (Exception exception)
        {
            var error = exception is WorkspaceCommandException command ? command.Error : new WorkspaceError("desktop.query-failed", "检查失败：" + exception.Message);
            if (ownsBusy && !disposed) ReportError(new WorkspaceCommandException(error, exception));
            return new(false, null, error);
        }
        finally { if (ownsBusy) { Interlocked.Exchange(ref busy, 0); if (!disposed) RefreshAvailability(); } }
    }

    private async Task<bool> RunCommandAsync(Func<Task<WorkspaceCommandResult?>> command, string successMessage,
        bool remember = false, bool forceOverview = false, bool ensureWorkspacePage = false)
    {
        if (disposed || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        LastError = null; Status = "正在处理，请稍候…"; RefreshAvailability();
        var succeeded = false;
        try
        {
            var result = await command();
            if (result is null) { Status = "已取消选择。"; return false; }
            if (workflow.CurrentSession is { } session) ApplySession(session);
            Status = successMessage;
            if (result.BackupPath is { } backup) Status += " 备份：" + backup;
            if (remember)
            {
                recentPaths.Remove(result.WorkspacePath); recentPaths.Insert(0, result.WorkspacePath);
                if (recentPaths.Count > 10) recentPaths.RemoveRange(10, recentPaths.Count - 10);
                StartPage.UpdateRecentWorkspaces(recentPaths);
                try { await Task.Run(() => recentStore.Save(recentPaths.ToArray())); }
                catch (Exception exception) { Status += " 最近工作区列表未能保存，可直接打开此文件：" + exception.Message; }
            }
            succeeded = true;
            return true;
        }
        catch (Exception exception) { ReportError(exception); return false; }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
            if (succeeded && forceOverview && Navigator.CurrentRoute != WorkspaceRoute.Overview) Navigate(WorkspaceRoute.Overview);
            else if (succeeded && ensureWorkspacePage && CurrentPage is not WorkspacePageViewModel && CurrentSession is { } session)
            {
                var route = WorkspaceNavigator.PreferredRoute(session.Workspace.Stage, session.Workspace.Purpose);
                Navigate(factories.ContainsKey(route) ? route : WorkspaceRoute.Overview);
            }
            RefreshAvailability();
        }
    }
    public void ReportError(Exception exception)
    {
        LastError = exception is WorkspaceCommandException command ? command.Error : new("desktop.operation-failed", "操作失败：" + exception.Message);
        Status = LastError.Message + " [" + LastError.Code + "]";
        if (LastError.Committed) Status += " 文件已保存；请检查当前快照，必要时重新载入。";
        if (LastError.CandidatePath is { } candidate) Status += " 候选文件：" + candidate;
        if (LastError.BackupPath is { } backup) Status += " 备份：" + backup;
        if (LastError.Code == "RevisionConflict") Status += " 请点击重新载入，不会自动覆盖其他窗口的修改。";
    }
    private void OnSessionChanged(object? sender, WorkspaceSession session) => post(() =>
    {
        if (!disposed && ReferenceEquals(workflow.CurrentSession, session)) ApplySession(session);
    });
    private void ApplySession(WorkspaceSession session)
    {
        if (disposed || !ReferenceEquals(workflow.CurrentSession, session)) return;
        var sameWorkspace = CurrentSession?.Workspace.Id == session.Workspace.Id && CurrentSession.WorkspacePath == session.WorkspacePath;
        CurrentSession = session;
        Navigator.UpdateWorkspace(session.Workspace.Stage, session.Workspace.Purpose);
        if (sameWorkspace && CurrentPage is WorkspacePageViewModel page && Navigator.CanNavigate(Navigator.CurrentRoute)) page.RefreshSession(session);
        else if (!sameWorkspace || !Navigator.CanNavigate(Navigator.CurrentRoute))
        {
            var target = WorkspaceNavigator.PreferredRoute(session.Workspace.Stage, session.Workspace.Purpose);
            if (!factories.ContainsKey(target)) target = WorkspaceRoute.Overview;
            Navigator.Navigate(target);
            if (CurrentPage is IDisposable disposable) disposable.Dispose();
            CurrentPage = factories[target](session);
        }
        OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(StageText)); OnPropertyChanged(nameof(WorkspacePath));
        RefreshAvailability();
    }
    public void RefreshAvailability()
    {
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanMutate)); OnPropertyChanged(nameof(HasWorkspace));
        NewCommand.NotifyCanExecuteChanged(); OpenCommand.NotifyCanExecuteChanged(); HomeCommand.NotifyCanExecuteChanged(); ReloadCommand.NotifyCanExecuteChanged();
        foreach (var item in NavigationItems) item.Refresh();
        StartPage.RefreshAvailability();
        if (CurrentPage is NewWorkspaceWizardViewModel wizard) wizard.RefreshCommands();
        if (CurrentPage is WorkspacePageViewModel page) page.RefreshAvailability();
    }
    public static string StageName(TournamentStage stage) => stage switch
    {
        TournamentStage.Draft => "赛事草稿", TournamentStage.RostersReady => "名单已就绪", TournamentStage.DrawsConfirmed => "抽签已确认",
        TournamentStage.ScheduleReady => "赛程已就绪", TournamentStage.InProgress => "赛事进行中", TournamentStage.Completed => "赛事已完成", _ => "未知阶段"
    };
    public void Dispose()
    {
        disposed = true; workflow.SessionChanged -= OnSessionChanged;
        if (CurrentPage is IDisposable disposable) disposable.Dispose();
    }
}

public sealed record WorkspaceQueryResult<T>(bool Succeeded, T? Value, WorkspaceError? Error) where T : class;

public sealed class WorkspaceNavigationItemViewModel : ViewModelBase
{
    private readonly AppShellViewModel shell;
    public WorkspaceRoute Route { get; }
    public string Label { get; }
    public DelegateCommand Command { get; }
    public bool IsCurrent => shell.Navigator.CurrentRoute == Route;
    public WorkspaceNavigationItemViewModel(AppShellViewModel shell, WorkspaceRoute route, string label)
    {
        this.shell = shell; Route = route; Label = label;
        Command = new(() => shell.Navigate(route), () => shell.CanNavigate(route));
    }
    public void Refresh() { Command.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(IsCurrent)); }
}
