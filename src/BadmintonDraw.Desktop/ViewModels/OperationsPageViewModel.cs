using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class OperationsPageViewModel : WorkspacePageViewModel, IDisposable
{
    private bool disposed;
    private int selectedTabIndex;
    public WorkspaceResultImportViewModel ResultImport { get; }
    public WorkspaceOperationalExportViewModel Materials { get; }
    public WorkspaceOperationsHistory History { get; private set; }
    public DelegateCommand OpenRecoveryCommand { get; }
    public int SelectedTabIndex { get => selectedTabIndex; set => SetProperty(ref selectedTabIndex, value); }
    public int CompletedMatchCount => Session.Workspace.Results.Count;
    public int TotalMatchCount => Session.Workspace.Projects.Sum(p => p.MatchGraph?.Matches.Count(n => n.IsPlayable) ?? 0);
    public string Header => $"{Session.Workspace.Name} · {AppShellViewModel.StageName(Session.Workspace.Stage)}\n{Session.WorkspacePath}\n" +
        $"工作区：{Session.Workspace.Id:D}；修订：{Session.Workspace.Revision}；已记录赛果：{CompletedMatchCount}/{TotalMatchCount}" +
        (Session.RequiresReload ? "\n此为最后已知快照；保存后重读失败，请重新载入，不能继续写入。" : "");
    public string ProjectSummary => string.Join("\n", Session.Workspace.Projects.Select(p =>
        $"{p.DisplayName} [{p.Id:D}]：{Session.Workspace.Results.Count(r => r.Key.ProjectId == p.Id)}/{p.MatchGraph?.Matches.Count(n => n.IsPlayable) ?? 0}"));
    public OperationsPageViewModel(AppShellViewModel shell, WorkspaceSession session,
        Func<Task<IReadOnlyList<string>?>> pickResultFiles, Func<Task<string?>> pickOutputDirectory) : base(session)
    {
        ResultImport = new(shell, session, pickResultFiles); Materials = new(shell, session, pickOutputDirectory);
        History = new(session.Workspace); OpenRecoveryCommand = shell.OpenRecoveryCommand;
    }
    public override void RefreshSession(WorkspaceSession next)
    {
        if (disposed || ReferenceEquals(Session, next)) return;
        base.RefreshSession(next);
        ResultImport.RefreshSession(next); Materials.RefreshSession(next); History = new(next.Workspace);
        foreach (var name in new[] { nameof(History), nameof(Header), nameof(ProjectSummary), nameof(CompletedMatchCount), nameof(TotalMatchCount) }) OnPropertyChanged(name);
        RefreshAvailability();
    }
    public override void RefreshAvailability()
    {
        if (disposed) return;
        ResultImport?.RefreshAvailability(); Materials?.RefreshAvailability();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; ResultImport.Dispose(); Materials.Dispose();
    }
}
