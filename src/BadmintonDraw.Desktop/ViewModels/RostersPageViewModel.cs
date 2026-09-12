using System.Collections.ObjectModel;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class RostersPageViewModel : WorkspacePageViewModel
{
    internal AppShellViewModel Shell { get; }
    internal Func<Task<string?>> ImportPicker { get; }
    internal Func<string, Task<string?>> TemplatePicker { get; }
    private ProjectRosterViewModel? selectedProject;
    public ObservableCollection<ProjectRosterViewModel> Projects { get; } = [];
    public ProjectRosterViewModel? SelectedProject { get => selectedProject; set => SetProperty(ref selectedProject, value); }
    public string Readiness => $"{Session.Workspace.Projects.Count(p => p.Roster is not null)} / {Session.Workspace.Projects.Count} 个项目名单已就绪；导入不会自动抽签。";
    public DelegateCommand NextCommand { get; }

    public RostersPageViewModel(AppShellViewModel shell, WorkspaceSession session,
        Func<Task<string?>> importPicker, Func<string, Task<string?>> templatePicker) : base(session)
    {
        Shell = shell; ImportPicker = importPicker; TemplatePicker = templatePicker;
        NextCommand = new(() => shell.Navigate(WorkspaceRoute.PublicDraw), () => shell.CanNavigate(WorkspaceRoute.PublicDraw));
        RefreshProjects();
    }
    public override void RefreshSession(WorkspaceSession next)
    {
        base.RefreshSession(next); RefreshProjects(); OnPropertyChanged(nameof(Readiness));
    }
    private void RefreshProjects()
    {
        var selectedId = SelectedProject?.ProjectId;
        var retained = Projects.ToDictionary(p => p.ProjectId);
        var next = Session.Workspace.Projects.OrderBy(p => p.SortOrder).Select(project =>
        {
            if (retained.TryGetValue(project.Id, out var existing)) { existing.Refresh(project); return existing; }
            return new ProjectRosterViewModel(this, project);
        }).ToArray();
        Projects.Clear(); foreach (var item in next) Projects.Add(item);
        SelectedProject = Projects.FirstOrDefault(p => p.ProjectId == selectedId) ?? Projects.FirstOrDefault();
        RefreshAvailability();
    }
    public override void RefreshAvailability()
    {
        foreach (var project in Projects) project.RefreshAvailability();
        NextCommand?.NotifyCanExecuteChanged();
    }
}
