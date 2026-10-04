using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class WorkspaceArchivePageViewModel : WorkspacePageViewModel
{
    private readonly AppShellViewModel shell;
    public bool IsDrawOnly => Session.Workspace.Purpose == TournamentPurpose.PublicDrawOnly;
    public bool IsCompleted => Session.Workspace.Stage == TournamentStage.Completed;
    public int TotalMatches => Session.Workspace.Projects.Sum(p => p.MatchGraph?.Matches.Count(n => n.IsPlayable) ?? 0);
    public int RecordedMatches => Session.Workspace.Results.Count;
    public int PendingMatches => TotalMatches - RecordedMatches;
    public string Heading => IsDrawOnly ? "抽签告一段落，成果妥善保存" : IsCompleted ? "比赛圆满结束，留住精彩" : "先检查进度，再安心归档";
    public string Guidance => IsDrawOnly ? "正式抽签结果已经确认。导出公示材料并创建备份后，就可以放心结束本次工作。以后仍可继续安排比赛。"
        : IsCompleted ? "全部比赛都已记录赛果。请导出最后一版材料，并创建一份赛事备份，方便交接和复查。"
        : $"还有 {PendingMatches} 场比赛尚未记录赛果。现在可以备份已有进度；赛事尚未完成。";
    public string ProgressLabel => IsDrawOnly ? $"{Session.Workspace.Projects.Count} 个项目已确认抽签" : $"{RecordedMatches} / {TotalMatches} 场已记录赛果";
    public IReadOnlyList<ArchiveProjectSummary> Projects => Session.Workspace.Projects.OrderBy(p => p.SortOrder).Select(p =>
    {
        var count = p.MatchGraph?.Matches.Count(n => n.IsPlayable) ?? 0;
        var recorded = Session.Workspace.Results.Count(r => r.Key.ProjectId == p.Id);
        return new ArchiveProjectSummary(WorkspaceProjectDisplay.Label(Session.Workspace, p), IsDrawOnly ? "抽签已确认" : $"已记录 {recorded} / {count} 场", IsDrawOnly ? "可导出正式抽签材料" : count == recorded ? "赛果已齐全" : $"尚有 {count - recorded} 场待记录");
    }).ToArray();
    public string FilePath => Session.WorkspacePath;
    public string RevisionText => $"保存版本 {Session.Workspace.Revision} · {Session.Workspace.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
    public string BackupSummary => Session.Workspace.AuditEvents.Where(e => e.Action == "WorkspaceBackupCreated")
        .OrderByDescending(e => e.OccurredAt).FirstOrDefault() is { } backup
        ? $"最近一次手动备份记录：{backup.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm}。备份不会自动同步后续修改；恢复前请重新核对文件。"
        : "还没有已记录的手动备份。建议在正式抽签后、比赛结束后各保留一份。";
    public DelegateCommand BackupCommand => shell.OpenRecoveryCommand;
    public DelegateCommand MaterialsCommand { get; }
    public string MaterialsLabel => IsDrawOnly ? "查看并导出抽签结果" : "前往导出最终材料";
    public WorkspaceArchivePageViewModel(AppShellViewModel shell, WorkspaceSession session) : base(session)
    {
        this.shell = shell;
        MaterialsCommand = new(() => shell.Navigate(IsDrawOnly ? WorkspaceRoute.PublicDraw : WorkspaceRoute.Operations),
            () => shell.CanNavigate(IsDrawOnly ? WorkspaceRoute.PublicDraw : WorkspaceRoute.Operations));
    }
    public override void RefreshSession(WorkspaceSession next)
    {
        base.RefreshSession(next);
        foreach (var name in new[] { nameof(IsDrawOnly), nameof(IsCompleted), nameof(TotalMatches), nameof(RecordedMatches), nameof(PendingMatches), nameof(Heading), nameof(Guidance), nameof(ProgressLabel), nameof(Projects), nameof(FilePath), nameof(RevisionText), nameof(BackupSummary), nameof(MaterialsLabel) }) OnPropertyChanged(name);
    }
    public override void RefreshAvailability() => MaterialsCommand?.NotifyCanExecuteChanged();
}

public sealed record ArchiveProjectSummary(string Name, string Progress, string Detail);
