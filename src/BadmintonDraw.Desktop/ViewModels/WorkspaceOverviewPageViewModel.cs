using System.Collections.ObjectModel;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class WorkspaceOverviewPageViewModel : WorkspacePageViewModel
{
    private readonly AppShellViewModel shell;
    private string editableName = "";
    private bool isEditing;
    public string EditableName { get => editableName; set => SetProperty(ref editableName, value); }
    public bool IsEditing { get => isEditing; private set { SetProperty(ref isEditing, value); RefreshAvailability(); } }
    public bool CanEditConfiguration => Session.Workspace.Projects.All(p => p.Draw?.ConfirmedAt is null) && Session.Workspace.Results.Count == 0 && !Session.RequiresReload;
    public bool CanEditFields => IsEditing && CanEditConfiguration && shell.CanMutate;
    public bool CanChangeProjects => CanEditFields && Session.Workspace.Kind == TournamentKind.Individual;
    public bool CanUpgrade => Session.Workspace.Purpose == TournamentPurpose.PublicDrawOnly;
    public string Name => Session.Workspace.Name;
    public string Stage => AppShellViewModel.StageName(Session.Workspace.Stage);
    public string Purpose => Session.Workspace.Purpose == TournamentPurpose.PublicDrawOnly ? "仅公开抽签" : "完整赛事筹备";
    public string Kind => Session.Workspace.Kind == TournamentKind.Team ? "团体赛" : "单项赛";
    public string Revision => $"修订 {Session.Workspace.Revision} · 修改自动保存";
    public string EditHint => Session.RequiresReload ? "请重新载入工作区后再修改。" : CanEditConfiguration ? "确认抽签前可修改名称、项目和赛制。" : "已有确认抽签，项目配置已锁定。";
    public string NextStep => Session.Workspace.Stage switch
    {
        TournamentStage.Draft => "下一步：准备各项目名单。",
        TournamentStage.RostersReady => "下一步：逐项目公开抽签，检查后确认。",
        TournamentStage.DrawsConfirmed when CanUpgrade => "公开抽签已完成，可回看材料，或继续筹备完整赛事。",
        TournamentStage.DrawsConfirmed => "下一步：统一设置比赛日、场地和全赛事策略。",
        TournamentStage.ScheduleReady => "赛程已就绪，可查看赛程板并准备现场材料。",
        TournamentStage.InProgress => "在现场执行中导入赛果、检查更正并导出后续材料。",
        _ => "赛事已完成，可回看赛果与材料记录。"
    };
    public ObservableCollection<WorkspaceProjectOptionViewModel> Projects { get; } = [];
    public IReadOnlyList<WorkspaceProjectProgress> ProjectProgress => Session.Workspace.Projects.OrderBy(p => p.SortOrder)
        .Select(p => new WorkspaceProjectProgress(p.DisplayName, p.Roster is null ? "待导入名单" : $"名单 {p.Roster.Participants.Count} 组",
            p.Draw?.ConfirmedAt is not null ? "抽签已确认" : p.Draw is not null ? "抽签待确认" : "待抽签")).ToArray();
    public DelegateCommand BeginEditCommand { get; }
    public DelegateCommand CancelEditCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand UpgradeCommand { get; }
    public DelegateCommand NextCommand { get; }
    public WorkspaceOverviewPageViewModel(AppShellViewModel shell, WorkspaceSession session) : base(session)
    {
        this.shell = shell;
        BeginEditCommand = new(() => { LoadConfiguration(); IsEditing = true; }, () => shell.CanMutate && CanEditConfiguration && !IsEditing);
        CancelEditCommand = new(() => { IsEditing = false; LoadConfiguration(); }, () => !shell.IsBusy && IsEditing);
        SaveCommand = new(async () =>
        {
            var request = new UpdateWorkspaceConfigurationRequest(EditableName, Projects.Where(p => p.IsSelected).Select(p => p.BuildRequest()).ToArray());
            if (await shell.RunWorkspaceCommandAsync(Session, (workflow, revision) => workflow.UpdateConfiguration(request, revision), "赛事信息已保存。"))
            { IsEditing = false; LoadConfiguration(); }
        }, () => CanEditFields, shell.ReportError);
        UpgradeCommand = new(async () => await shell.RunWorkspaceCommandAsync(Session,
            (workflow, revision) => workflow.UpgradeToFullTournament(revision), "已升级为完整赛事筹备，抽签内容保持不变。"),
            () => shell.CanMutate && CanUpgrade, shell.ReportError);
        NextCommand = new(() => shell.Navigate(NextRoute), () => shell.CanNavigate(NextRoute));
        LoadConfiguration();
    }
    private WorkspaceRoute NextRoute => Session.Workspace.Stage == TournamentStage.Draft ? WorkspaceRoute.Rosters :
        WorkspaceNavigator.PreferredRoute(Session.Workspace.Stage, Session.Workspace.Purpose);
    private void LoadConfiguration()
    {
        EditableName = Session.Workspace.Name;
        Projects.Clear();
        var existing = Session.Workspace.Projects.OrderBy(p => p.SortOrder).ToArray();
        var disciplines = existing.Select(p => p.Discipline).Concat(Session.Workspace.Kind == TournamentKind.Team ? [] :
            Enum.GetValues<EventDiscipline>().Where(d => d != EventDiscipline.Team && existing.All(p => p.Discipline != d)));
        foreach (var discipline in disciplines)
        {
            var project = existing.SingleOrDefault(p => p.Discipline == discipline);
            Projects.Add(new(discipline, project?.Id)
            {
                IsSelected = project is not null,
                DisplayName = project?.DisplayName ?? WorkspaceProjectOptionViewModel.DisciplineName(discipline),
                CompetitionModeIndex = project?.CompetitionMode is CompetitionMode.SinglesRoundRobin or CompetitionMode.TeamRoundRobin ? 1 : 0
            });
        }
    }
    public override void RefreshSession(WorkspaceSession next)
    {
        base.RefreshSession(next);
        if (!IsEditing) LoadConfiguration();
        foreach (var name in new[] { nameof(Name), nameof(Stage), nameof(Purpose), nameof(Kind), nameof(Revision), nameof(NextStep), nameof(ProjectProgress) }) OnPropertyChanged(name);
    }
    public override void RefreshAvailability()
    {
        foreach (var name in new[] { nameof(CanEditConfiguration), nameof(CanEditFields), nameof(CanChangeProjects), nameof(CanUpgrade), nameof(EditHint) }) OnPropertyChanged(name);
        BeginEditCommand?.NotifyCanExecuteChanged(); CancelEditCommand?.NotifyCanExecuteChanged();
        SaveCommand?.NotifyCanExecuteChanged(); UpgradeCommand?.NotifyCanExecuteChanged(); NextCommand?.NotifyCanExecuteChanged();
    }
}

public sealed record WorkspaceProjectProgress(string Name, string RosterStatus, string DrawStatus);
