using System.Collections.ObjectModel;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class NewWorkspaceWizardViewModel : ViewModelBase
{
    private string name = "";
    private string workspacePath = "";
    private int step = 1;
    private TournamentKind? kind;
    private TournamentPurpose? purpose;
    public ObservableCollection<WorkspaceProjectOptionViewModel> Projects { get; } = [];
    public string Name { get => name; set { if (SetProperty(ref name, value)) RefreshCommands(); } }
    public string WorkspacePath { get => workspacePath; set { if (SetProperty(ref workspacePath, value)) RefreshCommands(); } }
    public int Step { get => step; private set { SetProperty(ref step, value); RefreshCommands(); } }
    public bool IsStepOne => Step == 1;
    public bool IsStepTwo => Step == 2;
    public bool IsStepThree => Step == 3;
    public string StepTitle => Step switch { 1 => "这是什么比赛？", 2 => "要进行哪些项目？", _ => "把赛事文件保存在哪里？" };
    public string StepCaption => $"第 {Step} 步，共 3 步";
    public string NextLabel => Step == 1 ? "下一步：选择项目" : "下一步：选择保存位置";
    public string StepHint => Step switch
    {
        1 => BasicsValid ? "已准备好，接下来选择参赛项目。" : "请填写比赛名称，并选择比赛类型和本次目标。",
        2 => Projects.Any(p => p.IsSelected) ? $"已选择 {Projects.Count(p => p.IsSelected)} 个项目，可为每个项目选择赛制。" : "至少选择一个项目，再继续下一步。",
        _ => RequestValid ? "创建后就可以开始准备参赛名单。" : "请选择一个本地保存位置，文件以 .szbd 结尾。"
    };
    public bool IsTeam { get => kind == TournamentKind.Team; set { if (value) SelectKind(TournamentKind.Team); } }
    public bool IsIndividual { get => kind == TournamentKind.Individual; set { if (value) SelectKind(TournamentKind.Individual); } }
    public bool IsDrawOnly { get => purpose == TournamentPurpose.PublicDrawOnly; set { if (value) SelectPurpose(TournamentPurpose.PublicDrawOnly); } }
    public bool IsFullTournament { get => purpose == TournamentPurpose.FullTournament; set { if (value) SelectPurpose(TournamentPurpose.FullTournament); } }
    public string Summary => $"{Name} · {(IsTeam ? "团体赛" : "单项赛")} · {(IsDrawOnly ? "先完成公开抽签" : "筹备整场比赛")}\n" +
        string.Join("、", Projects.Where(p => p.IsSelected).Select(p => $"{p.DisplayName}（{p.CompetitionModes[p.CompetitionModeIndex]}）"));
    public CreateWorkspaceRequest? CreateRequest { get; private set; }
    public DelegateCommand NextCommand { get; }
    public DelegateCommand BackCommand { get; }
    public AsyncCommand BrowsePathCommand { get; }
    public AsyncCommand CreateCommand { get; }
    private readonly Func<bool> canAct;

    public NewWorkspaceWizardViewModel(Func<CreateWorkspaceRequest, Task>? create = null,
        Func<string, Task<string?>>? pickPath = null, Func<bool>? canAct = null, Action<Exception>? onError = null)
    {
        this.canAct = canAct ?? (() => true);
        NextCommand = new(() => Step++, () => this.canAct() && Step < 3 && (Step == 1 ? BasicsValid : Projects.Any(p => p.IsSelected)));
        BackCommand = new(() => Step--, () => this.canAct() && Step > 1);
        BrowsePathCommand = new(async () =>
        {
            if (pickPath is not null && await pickPath(Name) is { } path) WorkspacePath = path;
        }, () => this.canAct(), onError);
        CreateCommand = new(async () =>
        {
            CreateRequest = BuildRequest(Name, WorkspacePath);
            if (create is not null) await create(CreateRequest);
        }, () => this.canAct() && Step == 3 && RequestValid, onError);
    }
    private bool BasicsValid => !string.IsNullOrWhiteSpace(Name) && kind is not null && purpose is not null;
    private bool RequestValid
    {
        get { try { BuildRequest(Name, WorkspacePath); return true; } catch (ArgumentException) { return false; } }
    }
    public void SelectKind(TournamentKind value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentException("请选择团体赛或单项赛。");
        if (kind == value) return;
        kind = value;
        Projects.Clear();
        foreach (var discipline in value == TournamentKind.Team ? new[] { EventDiscipline.Team } : Enum.GetValues<EventDiscipline>().Where(d => d != EventDiscipline.Team))
        {
            var project = new WorkspaceProjectOptionViewModel(discipline) { IsSelected = value == TournamentKind.Team };
            project.PropertyChanged += (_, _) => RefreshCommands();
            Projects.Add(project);
        }
        OnPropertyChanged(nameof(IsTeam)); OnPropertyChanged(nameof(IsIndividual)); RefreshCommands();
    }
    public void SelectPurpose(TournamentPurpose value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentException("请选择工作目标。");
        purpose = value;
        OnPropertyChanged(nameof(IsDrawOnly)); OnPropertyChanged(nameof(IsFullTournament)); RefreshCommands();
    }
    public void SetDisciplines(IEnumerable<EventDiscipline> disciplines)
    {
        var values = disciplines.ToArray();
        if (values.Distinct().Count() != values.Length || values.Any(d => Projects.All(p => p.Discipline != d)) ||
            (IsTeam && !values.SequenceEqual(new[] { EventDiscipline.Team })))
            throw new ArgumentException("项目不可重复，团体赛与单项赛不能混合。");
        foreach (var project in Projects) project.IsSelected = values.Contains(project.Discipline);
    }
    public CreateWorkspaceRequest BuildRequest(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请填写赛事名称。");
        if (kind is null || purpose is null) throw new ArgumentException("请选择赛事类型和工作目标。");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".szbd", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择本地赛事文件的保存位置，文件以 .szbd 结尾。");
        var selected = Projects.Where(p => p.IsSelected).ToArray();
        if (selected.Length == 0 || (IsTeam && (selected.Length != 1 || selected[0].Discipline != EventDiscipline.Team)))
            throw new ArgumentException("请选择至少一个项目；团体赛必须保留一个团体项目。");
        return new(name.Trim(), kind.Value, purpose.Value, selected.Select(p => p.BuildRequest()).ToArray(), path);
    }
    public void RefreshCommands()
    {
        NextCommand?.NotifyCanExecuteChanged(); BackCommand?.NotifyCanExecuteChanged();
        BrowsePathCommand?.NotifyCanExecuteChanged(); CreateCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsStepOne)); OnPropertyChanged(nameof(IsStepTwo)); OnPropertyChanged(nameof(IsStepThree)); OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(StepTitle)); OnPropertyChanged(nameof(StepCaption)); OnPropertyChanged(nameof(NextLabel)); OnPropertyChanged(nameof(StepHint));
    }
}
