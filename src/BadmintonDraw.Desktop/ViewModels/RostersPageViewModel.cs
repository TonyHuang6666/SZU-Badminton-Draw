using System.Collections.ObjectModel;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class RostersPageViewModel : WorkspacePageViewModel
{
    internal AppShellViewModel Shell { get; }
    internal Func<Task<string?>> ImportPicker { get; }
    // Returns null on cancellation; existing paths require the save dialog's replacement confirmation.
    internal Func<string, Task<string?>> TemplatePicker { get; }
    private ProjectRosterViewModel? selectedProject;
    public ObservableCollection<ProjectRosterViewModel> Projects { get; } = [];
    public ProjectRosterViewModel? SelectedProject { get => selectedProject; set => SetProperty(ref selectedProject, value); }
    public bool AllRostersImported => Session.Workspace.Projects.Count > 0 && Session.Workspace.Projects.All(p => p.Roster is not null);
    public string Readiness => $"{Session.Workspace.Projects.Count(p => p.Roster is not null)} / {Session.Workspace.Projects.Count} 个项目已导入名单";
    private ProjectRosterViewModel? EditingProject => Projects.FirstOrDefault(p => p.IsSeedEditing);
    public string NextHint => EditingProject is { } editing ? $"“{editing.DisplayLabel}”的种子设置尚在编辑。请先点击“保存种子设置”或“取消编辑”，再离开名单页。"
        : AllRostersImported ? "请核对名单、检查提醒和种子设置；确认无误后前往公开抽签。" : "先为每个项目导入名单，完成后即可进行公开抽签。";
    public string NextLabel => EditingProject is not null ? "先处理种子编辑" : AllRostersImported && Session.Workspace.Projects.All(p => p.Draw?.ConfirmedAt is not null)
        ? "查看公开抽签" : AllRostersImported ? "名单检查无误，开始公开抽签" : "下一步：公开抽签";
    public DelegateCommand NextCommand { get; }
    public DelegateCommand PlayerEntriesCommand { get; }
    public bool ShowPlayerEntries => Shell.HasMultipleImportedProjects;
    public string PlayerEntriesHint => AllRostersImported ? "核对选手报名的项目与双打搭档，无需先抽签。" : "请先导入全部项目名单。";

    public RostersPageViewModel(AppShellViewModel shell, WorkspaceSession session,
        Func<Task<string?>> importPicker, Func<string, Task<string?>> templatePicker) : base(session)
    {
        Shell = shell; ImportPicker = importPicker; TemplatePicker = templatePicker;
        NextCommand = new(() => shell.Navigate(WorkspaceRoute.PublicDraw), () => shell.CanNavigate(WorkspaceRoute.PublicDraw));
        PlayerEntriesCommand = new(shell.OpenPlayerEntries, () => shell.CanOpenPlayerEntries &&
            shell.CurrentSession?.Workspace.Id == Session.Workspace.Id && shell.CurrentSession.WorkspacePath == Session.WorkspacePath);
        RefreshProjects();
    }
    public override void RefreshSession(WorkspaceSession next)
    {
        base.RefreshSession(next); RefreshProjects();
        foreach (var property in new[] { nameof(Readiness), nameof(AllRostersImported), nameof(NextHint), nameof(NextLabel), nameof(PlayerEntriesHint) }) OnPropertyChanged(property);
    }
    public override bool TryLeave()
    {
        if (EditingProject is not { } editing) return true;
        SelectedProject = editing;
        // Keep a failed save's detailed recovery evidence; the page hint still explains the navigation block.
        if (Shell.LastError is null || Shell.LastError.Code == AppShellViewModel.PendingEditsErrorCode)
            Shell.ReportError(new WorkspaceCommandException(new(AppShellViewModel.PendingEditsErrorCode, NextHint)));
        return false;
    }
    internal void SeedEditingChanged()
    {
        OnPropertyChanged(nameof(NextHint)); OnPropertyChanged(nameof(NextLabel));
        Shell.ClearPageLeaveWarning(NextHint);
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
        OnPropertyChanged(nameof(ShowPlayerEntries));
        foreach (var project in Projects) project.RefreshAvailability();
        NextCommand?.NotifyCanExecuteChanged();
        PlayerEntriesCommand?.NotifyCanExecuteChanged();
    }
}
