using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed partial class AppShellViewModel : ViewModelBase, IDisposable
{
    internal const string PendingEditsErrorCode = "desktop.unsaved-edits";
    private readonly TournamentWorkspaceWorkflow workflow;
    private readonly Func<Task<string?>> openPicker;
    private readonly Func<string, Task<string?>> savePicker;
    private readonly RecentWorkspaceStore recentStore;
    private readonly Action<Action> post;
    private readonly Func<IReadOnlyList<string>, Task<bool>> confirmExportOverwrite;
    private readonly Dictionary<WorkspaceRoute, Func<WorkspaceSession, WorkspacePageViewModel>> factories = [];
    private readonly List<string> recentPaths = [];
    private int busy;
    private bool disposed;
    private string status = "准备好了吗？创建一场新比赛，或继续之前的筹备。";
    private ViewModelBase currentPage;
    private WorkspaceSession? currentSession;
    private WorkspaceError? lastError;
    private WorkspaceCommandResult? lastCommandResult;
    // The board may outlive the main page, but never the tournament it belongs to.
    private ScheduleBoardPageViewModel? scheduleBoardPage;
    public event Action<ScheduleBoardPageViewModel>? BoardWindowRequested;
    public event Action? BoardWindowInvalidated;
    public WorkspaceNavigator Navigator { get; } = new();
    public StartPageViewModel StartPage { get; }
    public IReadOnlyList<WorkspaceNavigationItemViewModel> NavigationItems { get; }
    public ViewModelBase CurrentPage { get => currentPage; private set { SetProperty(ref currentPage, value); RefreshPresentation(); } }
    public WorkspaceSession? CurrentSession { get => currentSession; private set => SetProperty(ref currentSession, value); }
    public WorkspaceError? LastError { get => lastError; private set { SetProperty(ref lastError, value); RefreshPresentation(); } }
    public WorkspaceCommandResult? LastCommandResult { get => lastCommandResult; private set => SetProperty(ref lastCommandResult, value); }
    public string Status { get => status; private set { SetProperty(ref status, value); RefreshPresentation(); } }
    public bool IsBusy => Volatile.Read(ref busy) != 0;
    public bool HasWorkspace => CurrentSession is not null;
    public bool CanMutate => !IsBusy && CurrentSession is { RequiresReload: false };
    public string Title => ShowWorkspaceContext ? CurrentSession!.Workspace.Name : "深大羽协 · 赛事助手";
    public string StageText => ShowWorkspaceContext ? StageName(CurrentSession!.Workspace.Stage) : Navigator.CurrentRoute == WorkspaceRoute.NewWorkspace ? "创建比赛 · 从这里开始" : "BADMINTON  /  TOURNAMENT STUDIO";
    public string WorkspacePath => CurrentSession?.WorkspacePath ?? "";
    public DelegateCommand NewCommand { get; }
    public DelegateCommand HomeCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public WorkspaceRecoveryViewModel Recovery { get; }
    public DelegateCommand OpenRecoveryCommand { get; }

    public AppShellViewModel(TournamentWorkspaceWorkflow workflow, Func<Task<string?>> openPicker,
        Func<string, Task<string?>> savePicker, RecentWorkspaceStore recentStore, Action<Action> post,
        Func<Task<string?>>? recoveryTargetPicker = null, Func<Task<string?>>? recoveryBackupPicker = null,
        Func<IReadOnlyList<string>, Task<bool>>? confirmExportOverwrite = null)
    {
        this.workflow = workflow; this.openPicker = openPicker; this.savePicker = savePicker;
        this.recentStore = recentStore; this.post = post;
        this.confirmExportOverwrite = confirmExportOverwrite ?? (_ => Task.FromResult(false));
        Recovery = new(this, recoveryTargetPicker ?? (() => Task.FromResult<string?>(null)),
            recoveryBackupPicker ?? (() => Task.FromResult<string?>(null)));
        OpenRecoveryCommand = new(Recovery.Open, () => !disposed && !IsBusy);
        NewCommand = new(StartNewWorkspace, () => !IsBusy);
        HomeCommand = new(() => Navigate(WorkspaceRoute.Start), () => !IsBusy);
        OpenCommand = new(async () => await RunCommandAsync(async () =>
        {
            var path = await this.openPicker();
            return path is null ? null : await OpenPathAsync(path);
        }, "已打开工作区。", remember: true, ensureWorkspacePage: true, requirePageLeave: true), () => !IsBusy, ReportError);
        // Reload refreshes the current editor and detects conflicts without discarding its draft.
        ReloadCommand = new(async () => { if (CurrentSession is { } session) await RunCommandAsync(
            async () => await OpenPathAsync(session.WorkspacePath), "已打开工作区。", remember: true, ensureWorkspacePage: true); },
            () => !IsBusy && HasWorkspace, ReportError);
        StartPage = new(this); currentPage = StartPage;
        factories[WorkspaceRoute.Overview] = session => new WorkspaceOverviewPageViewModel(this, session);
        factories[WorkspaceRoute.Archive] = session => new WorkspaceArchivePageViewModel(this, session);
        NextActionCommand = new(() => { if (NextActionRoute is { } route) Navigate(route); }, () => NextActionRoute is { } route && CanNavigate(route));
        ToggleThemeCommand = new(() =>
        {
            if (Avalonia.Application.Current is { } app)
                app.RequestedThemeVariant = app.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
                    ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        });
        NavigationItems = new[]
        {
            new WorkspaceNavigationItemViewModel(this, WorkspaceRoute.Overview, "比赛概览", 1),
            new WorkspaceNavigationItemViewModel(this, WorkspaceRoute.Rosters, "准备名单", 2),
            new WorkspaceNavigationItemViewModel(this, WorkspaceRoute.PublicDraw, "公开抽签", 3),
            new WorkspaceNavigationItemViewModel(this, WorkspaceRoute.ScheduleSetup, "安排赛程", 4),
            new WorkspaceNavigationItemViewModel(this, WorkspaceRoute.Operations, "比赛现场", 5),
            new WorkspaceNavigationItemViewModel(this, WorkspaceRoute.Archive, "完成与归档", 6)
        };
        try { recentPaths.AddRange(recentStore.Read()); }
        catch (Exception exception) { Status = "最近工作区列表无法读取，可继续新建或打开：" + exception.Message; }
        StartPage.UpdateRecentWorkspaces(recentPaths);
        workflow.SessionChanged += OnSessionChanged;
        if (workflow.CurrentSession is { } session) ApplySession(session);
    }

    internal Task<bool> ConfirmExportOverwriteAsync(IReadOnlyList<string> paths) =>
        disposed ? Task.FromResult(false) : confirmExportOverwrite(paths);

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
        if (!CanNavigate(route) || !TryLeaveCurrentPage()) return false;
        if (route == WorkspaceRoute.NewWorkspace) { StartNewWorkspace(); return true; }
        if (route == WorkspaceRoute.Start) LastError = null;
        Navigator.Navigate(route);
        DisposeCurrentPage();
        CurrentPage = route == WorkspaceRoute.Start ? StartPage : CreatePage(route, CurrentSession!);
        RefreshAvailability();
        if (CurrentPage is ScheduleBoardPageViewModel board) RequestBoardWindow(board);
        return true;
    }
    public void StartNewWorkspace()
    {
        if (IsBusy || !TryLeaveCurrentPage()) return;
        LastError = null;
        Navigator.Navigate(WorkspaceRoute.NewWorkspace);
        DisposeCurrentPage();
        CurrentPage = new NewWorkspaceWizardViewModel(async request => await CreateWorkspaceAsync(request),
            savePicker, () => !IsBusy, ReportError);
        RefreshAvailability();
    }
    public Task<bool> CreateWorkspaceAsync(CreateWorkspaceRequest request) => RunCommandAsync(
        async () => await Task.Run(() => workflow.CreateWorkspace(request)), "已创建赛事工作区，下一步准备各项目名单。", remember: true, forceOverview: true, requirePageLeave: true);
    public Task<bool> OpenWorkspaceAsync(string path) => RunCommandAsync(
        async () => await OpenPathAsync(path), "已打开工作区。", remember: true, ensureWorkspacePage: true, requirePageLeave: true);
    private bool TryLeaveCurrentPage() => CurrentPage is not WorkspacePageViewModel page || page.TryLeave();

    internal void RequestBoardWindow(ScheduleBoardPageViewModel board)
    {
        if (!disposed && ReferenceEquals(board, scheduleBoardPage) && CanNavigate(WorkspaceRoute.ScheduleBoard))
            BoardWindowRequested?.Invoke(board);
    }
    private WorkspacePageViewModel CreatePage(WorkspaceRoute route, WorkspaceSession session)
    {
        if (route == WorkspaceRoute.ScheduleBoard && scheduleBoardPage is { } existing) return existing;
        var page = factories[route](session);
        if (page is ScheduleBoardPageViewModel board) scheduleBoardPage = board;
        return page;
    }
    private void DisposeCurrentPage()
    {
        if (!ReferenceEquals(CurrentPage, scheduleBoardPage) && CurrentPage is IDisposable disposable) disposable.Dispose();
    }
    private void InvalidateBoardWindow()
    {
        var previous = scheduleBoardPage;
        scheduleBoardPage = null;
        previous?.Dispose();
        BoardWindowInvalidated?.Invoke();
    }
    internal void ClearPageLeaveWarning(string message)
    {
        if (LastError?.Code != PendingEditsErrorCode) return;
        LastError = null; Status = message;
    }
    private async Task<WorkspaceCommandResult> OpenPathAsync(string path)
    {
        try { return await Task.Run(() => workflow.OpenWorkspace(path)); }
        catch (WorkspaceCommandException exception) when (exception.Error.Code == "InvalidWorkspace")
        {
            if (!disposed) { Recovery.TargetPath = path; Recovery.RecoverCorruptTarget = true; }
            throw;
        }
    }
    public Task<bool> RunWorkspaceCommandAsync(WorkspaceSession expectedSession,
        Func<TournamentWorkspaceWorkflow, long, WorkspaceCommandResult> command, string successMessage) => RunCommandAsync(async () =>
        await Task.Run(() =>
        {
            if (!ReferenceEquals(workflow.CurrentSession, expectedSession))
                throw new WorkspaceCommandException(new("workspace.session-changed", "工作区已更新或切换，请检查当前页面后重新操作。"));
            if (expectedSession.RequiresReload)
                throw new WorkspaceCommandException(new("workspace.reload-required", "工作区已保存但无法重新读取，请点击重新载入后再操作。"));
            return command(workflow, expectedSession.Workspace.Revision);
        }), successMessage, expectedSession: expectedSession);

    /// <summary>Read-only work has no save result. Background hover checks keep dragging enabled; quiet internal refreshes retain status but still own busy and report errors.</summary>
    public async Task<WorkspaceQueryResult<T>> RunWorkspaceQueryAsync<T>(WorkspaceSession expectedSession,
        Func<TournamentWorkspaceWorkflow, long, T> query, bool background = false, bool showStatus = true) where T : class
    {
        var ownsBusy = !background && !disposed && Interlocked.CompareExchange(ref busy, 1, 0) == 0;
        if (disposed || (!background && !ownsBusy) || (background && IsBusy))
            return new(false, null, new("desktop.query-busy", "当前操作尚未完成。"));
        if (ownsBusy) { LastError = null; LastCommandResult = null; if (showStatus) Status = "正在检查，尚未保存…"; RefreshAvailability(); }
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
        bool remember = false, bool forceOverview = false, bool ensureWorkspacePage = false, bool requirePageLeave = false,
        WorkspaceSession? expectedSession = null)
    {
        if (disposed || IsBusy || (requirePageLeave && !TryLeaveCurrentPage()) || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        LastError = null; LastCommandResult = null; Status = "正在处理，请稍候…"; RefreshAvailability();
        var succeeded = false;
        try
        {
            var result = await command();
            if (disposed) return false;
            if (result is null) { Status = "已取消选择。"; return false; }
            var session = workflow.CurrentSession;
            // Publish creates a new session, so compare the command's actual saved snapshot, not
            // the pre-command session. Reopening even the same path must not own an older result.
            if (session is null || !ReferenceEquals(session.Workspace, result.Workspace) || session.WorkspacePath != result.WorkspacePath)
            {
                if (session is not null) ApplySession(session);
                Status = "当前工作区已更新或切换；请查看当前工作区。";
                return false;
            }
            ApplySession(session);
            LastCommandResult = result;
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
        catch (Exception exception)
        {
            if (disposed) return false;
            var session = workflow.CurrentSession;
            // A committed read failure legitimately publishes a replacement session. Keep its
            // saved outcome visible, while discarding ordinary failures from a replaced session.
            var ownCommittedFailure = exception is WorkspaceCommandException { Error.Committed: true } &&
                CommittedPublication(exception) is { } published &&
                ReferenceEquals(session, published);
            var staleCommandRejection = exception is WorkspaceCommandException { Error.Code: "workspace.session-changed", Error.SchedulingFailure: null };
            if (expectedSession is not null && !ReferenceEquals(session, expectedSession) && !ownCommittedFailure && !staleCommandRejection)
            {
                if (session is not null) ApplySession(session);
                Status = "当前工作区已更新或切换；请查看当前工作区。";
            }
            else ReportError(exception);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
            if (!disposed && succeeded && forceOverview && Navigator.CurrentRoute != WorkspaceRoute.Overview) Navigate(WorkspaceRoute.Overview);
            else if (!disposed && succeeded && ensureWorkspacePage && CurrentPage is not WorkspacePageViewModel && CurrentSession is { } session)
            {
                var route = WorkspaceNavigator.PreferredRoute(session.Workspace.Stage, session.Workspace.Purpose);
                Navigate(factories.ContainsKey(route) ? route : WorkspaceRoute.Overview);
            }
            if (!disposed) RefreshAvailability();
        }
    }
    private static WorkspaceSession? CommittedPublication(Exception exception)
    {
        // Export/backup commands preserve their precise published session inside
        // a material-specific exception, then the view model wraps its error for
        // the shell. Only follow these known wrappers, never match archive IDs.
        var error = ((WorkspaceCommandException)exception).Error;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is WorkspaceCommandException command && ReferenceEquals(command.Error, error) && command.CommittedSession is { } published)
                return published;
            if (current is not WorkspaceCommandException and not DrawPackageExportException and not WorkspaceBackupException and not OperationalPackageExportException)
                break;
        }
        return null;
    }

    public void ReportError(Exception exception)
    {
        LastCommandResult = null;
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
        if (!sameWorkspace) LastError = null;
        if (LastCommandResult is { } result && (!ReferenceEquals(result.Workspace, session.Workspace) || result.WorkspacePath != session.WorkspacePath))
            LastCommandResult = null;
        CurrentSession = session;
        Recovery.RefreshContext();
        Navigator.UpdateWorkspace(session.Workspace.Stage, session.Workspace.Purpose);
        if (!sameWorkspace || !Navigator.CanNavigate(WorkspaceRoute.ScheduleBoard)) InvalidateBoardWindow();
        else if (scheduleBoardPage is { } board && !ReferenceEquals(board, CurrentPage)) board.RefreshSession(session);
        if (sameWorkspace && CurrentPage is WorkspacePageViewModel page && Navigator.CanNavigate(Navigator.CurrentRoute)) page.RefreshSession(session);
        else if (!sameWorkspace || !Navigator.CanNavigate(Navigator.CurrentRoute))
        {
            var target = WorkspaceNavigator.PreferredRoute(session.Workspace.Stage, session.Workspace.Purpose);
            if (!factories.ContainsKey(target)) target = WorkspaceRoute.Overview;
            Navigator.Navigate(target);
            DisposeCurrentPage();
            CurrentPage = CreatePage(target, session);
        }
        OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(StageText)); OnPropertyChanged(nameof(WorkspacePath));
        RefreshAvailability();
    }
    public void RefreshAvailability()
    {
        RefreshPresentation();
        NextActionCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanMutate)); OnPropertyChanged(nameof(HasWorkspace));
        NewCommand.NotifyCanExecuteChanged(); OpenCommand.NotifyCanExecuteChanged(); HomeCommand.NotifyCanExecuteChanged(); ReloadCommand.NotifyCanExecuteChanged();
        OpenRecoveryCommand.NotifyCanExecuteChanged(); Recovery.RefreshAvailability();
        foreach (var item in NavigationItems) item.Refresh();
        StartPage.RefreshAvailability();
        if (CurrentPage is NewWorkspaceWizardViewModel wizard) wizard.RefreshCommands();
        if (CurrentPage is WorkspacePageViewModel page) page.RefreshAvailability();
        if (scheduleBoardPage is { } board && !ReferenceEquals(board, CurrentPage)) board.RefreshAvailability();
    }
    public static string StageName(TournamentStage stage) => stage switch
    {
        TournamentStage.Draft => "赛事草稿", TournamentStage.RostersReady => "名单已就绪", TournamentStage.DrawsConfirmed => "抽签已确认",
        TournamentStage.ScheduleReady => "赛程已就绪", TournamentStage.InProgress => "赛事进行中", TournamentStage.Completed => "赛事已完成", _ => "未知阶段"
    };
    public void Dispose()
    {
        disposed = true; workflow.SessionChanged -= OnSessionChanged;
        Recovery.Dispose();
        DisposeCurrentPage();
        InvalidateBoardWindow();
        BoardWindowRequested = null; BoardWindowInvalidated = null;
    }
}

public sealed record WorkspaceQueryResult<T>(bool Succeeded, T? Value, WorkspaceError? Error) where T : class;

public sealed class WorkspaceNavigationItemViewModel : ViewModelBase
{
    private readonly AppShellViewModel shell;
    public WorkspaceRoute Route { get; }
    public string Label { get; }
    public int Number { get; }
    public DelegateCommand Command { get; }
    public bool IsCurrent => shell.Navigator.CurrentRoute == Route || Route == WorkspaceRoute.ScheduleSetup && shell.Navigator.CurrentRoute == WorkspaceRoute.ScheduleBoard;
    public bool IsComplete => shell.CurrentSession is { } session && Route switch
    {
        WorkspaceRoute.Overview => true,
        WorkspaceRoute.Rosters => session.Workspace.Stage >= TournamentStage.RostersReady,
        WorkspaceRoute.PublicDraw => session.Workspace.Stage >= TournamentStage.DrawsConfirmed,
        WorkspaceRoute.ScheduleSetup => session.Workspace.Stage >= TournamentStage.ScheduleReady,
        WorkspaceRoute.Operations => session.Workspace.Stage == TournamentStage.Completed,
        _ => false
    };
    public string Marker => IsComplete && !IsCurrent ? "✓" : Number.ToString("00");
    public string Hint => shell.CurrentSession?.RequiresReload == true ? "需要处理 · 重新读取文件" : IsCurrent ? "正在查看" : IsComplete ? "已完成 · 可回看" : shell.CanNavigate(Route) ? "可以开始" : Route switch
    {
        WorkspaceRoute.PublicDraw => "先检查全部项目名单",
        WorkspaceRoute.ScheduleSetup when shell.CurrentSession?.Workspace.Purpose == TournamentPurpose.PublicDrawOnly => "只抽签时无需安排",
        WorkspaceRoute.ScheduleSetup => "先确认全部抽签",
        WorkspaceRoute.Operations => "先生成比赛赛程",
        WorkspaceRoute.Archive => "完成抽签后可查看",
        _ => "未开始"
    };
    public string AccessibleLabel => $"第 {Number} 步，{Label}，{Hint}";
    public WorkspaceNavigationItemViewModel(AppShellViewModel shell, WorkspaceRoute route, string label, int number = 0)
    {
        this.shell = shell; Route = route; Label = label; Number = number;
        Command = new(() => shell.Navigate(route), () => shell.CanNavigate(route));
    }
    public void Refresh()
    {
        Command.NotifyCanExecuteChanged();
        foreach (var name in new[] { nameof(IsCurrent), nameof(IsComplete), nameof(Marker), nameof(Hint), nameof(AccessibleLabel) }) OnPropertyChanged(name);
    }
}
