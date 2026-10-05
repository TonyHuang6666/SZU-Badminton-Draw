using System.Collections.ObjectModel;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class ProjectRosterViewModel : ViewModelBase
{
    private readonly RostersPageViewModel page;
    private TournamentProject project;
    private TournamentProject? editorBaseline;
    private bool isSeedEditing;
    private bool editorConflict;
    private string exportDetails = "";
    public Guid ProjectId => project.Id;
    public string Name => project.DisplayName;
    public string DisplayLabel => WorkspaceProjectDisplay.Label(page.Session.Workspace, project);
    public string SourceFileName => project.Roster?.SourceFileName ?? "尚未导入";
    public string SourceHash => project.Roster?.ContentHash ?? "";
    public bool HasRoster => project.Roster is not null;
    public bool HasWarnings => project.Roster?.Warnings.Count > 0;
    public string ImportLabel => HasRoster ? "替换名单…" : "导入参赛名单…";
    public string Summary => project.Roster is null ? "尚未导入名单" : $"已导入 {project.Roster.Participants.Count} {ParticipantUnit} · {(project.Draw?.ConfirmedAt is not null ? "抽签已确认" : "待核对")}";
    public string CheckSummary => !HasRoster ? "使用模板准备名单，再导入这里。" : HasWarnings ? $"有 {project.Roster!.Warnings.Count} 条提醒，请在下方检查。" : "未发现导入提醒，请核对姓名和种子。";
    public string SeedSummary => !HasRoster ? "导入后可设置种子" : $"已设置 {project.Roster!.Participants.Count(p => p.IsSeed)} 个种子";
    private string ParticipantUnit => project.Discipline == EventDiscipline.Team ? "支队伍" : project.Discipline is EventDiscipline.MenDoubles or EventDiscipline.WomenDoubles or EventDiscipline.MixedDoubles ? "对组合" : "位选手";
    public string Warnings => string.Join(Environment.NewLine, project.Roster?.Warnings.Select(w => w.Message) ?? []);
    public bool IsSeedEditing
    {
        get => isSeedEditing;
        private set { if (SetProperty(ref isSeedEditing, value)) page.SeedEditingChanged(); RefreshAvailability(); }
    }
    public bool HasEditorConflict => editorConflict;
    public bool CanEdit => page.Shell.CanMutate && project.Draw?.ConfirmedAt is null && page.Session.Workspace.Results.Count == 0;
    public bool CanEditSeedFields => CanEdit && IsSeedEditing && !editorConflict;
    public string ExportDetails { get => exportDetails; private set => SetProperty(ref exportDetails, value); }
    public string EditHint => editorConflict ? "编辑期间，名单或项目设置发生了变化。为避免覆盖新资料，暂时不能保存。点击“重新载入名单后编辑”会放弃这次未保存的种子修改；也可以取消编辑。"
        : project.Draw?.ConfirmedAt is not null ? "抽签已确认。需要更改时，请到公开抽签页明确解除确认。"
        : "种子是需要按实力分开放置的参赛方；不设种子也可以抽签。姓名、学号或搭档有误时，请修改原文件并替换名单。";
    public ObservableCollection<RosterSeedRowViewModel> Rows { get; } = [];
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand ExportTemplateCommand { get; }
    public DelegateCommand BeginSeedEditCommand { get; }
    public DelegateCommand ResetSeedsCommand { get; }
    public DelegateCommand CancelSeedEditCommand { get; }
    public AsyncCommand SaveSeedsCommand { get; }

    public ProjectRosterViewModel(RostersPageViewModel page, TournamentProject project)
    {
        this.page = page; this.project = project;
        ImportCommand = new(async () =>
        {
            var expected = page.Session; var projectId = ProjectId;
            var path = await page.ImportPicker();
            if (path is not null && await page.Shell.RunWorkspaceCommandAsync(expected,
                (workflow, revision) => workflow.ImportRoster(projectId, path, revision), "名单已导入并保存；尚未抽签。"))
            { IsSeedEditing = false; LoadRows(); }
        }, () => CanEdit && !IsSeedEditing, page.Shell.ReportError);
        ExportTemplateCommand = new(ExportTemplateAsync, () => page.Shell.CanMutate, page.Shell.ReportError);
        BeginSeedEditCommand = new(() => { LoadRows(); IsSeedEditing = true; }, () => CanEdit && this.project.Roster is not null && !IsSeedEditing);
        ResetSeedsCommand = new(LoadRows, () => IsSeedEditing && HasEditorConflict && !page.Shell.IsBusy && this.project.Roster is not null);
        CancelSeedEditCommand = new(() => { IsSeedEditing = false; LoadRows(); }, () => !page.Shell.IsBusy && IsSeedEditing);
        SaveSeedsCommand = new(async () =>
        {
            var expected = page.Session; var projectId = ProjectId;
            var edits = Rows.Select(row => row.ToEdit()).ToArray();
            if (await page.Shell.RunWorkspaceCommandAsync(expected, (workflow, revision) => workflow.UpdateRosterSeeds(projectId, edits, revision),
                "种子设置已保存，原有待确认的抽签结果已清除。")) { IsSeedEditing = false; LoadRows(); }
        }, () => CanEditSeedFields, page.Shell.ReportError);
        LoadRows();
    }
    private async Task ExportTemplateAsync()
    {
        var expected = page.Session; var projectId = ProjectId; var name = Name;
        var path = await page.TemplatePicker(name + "_名单模板");
        if (path is null) return;
        // The save picker confirms replacement before returning an existing path.
        // New destinations still use create-only publication if a file appears later.
        var overwrite = File.Exists(path);
        DrawPackageExportException? failure = null;
        var success = await page.Shell.RunWorkspaceCommandAsync(expected, (workflow, revision) =>
        {
            try { return workflow.ExportRosterTemplate(projectId, path, revision, overwrite); }
            catch (DrawPackageExportException exception) { failure = exception; throw new WorkspaceCommandException(exception.Error, exception); }
        }, "名单模板已导出，导出记录已保存。" );
        ExportDetails = success ? "名单模板：" + path : failure is not null ? DrawExportFeedback.Describe(failure) : "导出未完成，请查看下方错误详情。";
    }
    private void LoadRows()
    {
        editorBaseline = project; editorConflict = false;
        Rows.Clear();
        if (project.Roster is { } roster)
            foreach (var (participant, index) in roster.Participants.Select((p, i) => (p, i))) Rows.Add(new(index, participant, project.Discipline == EventDiscipline.Team));
        RefreshAvailability();
    }
    internal void Refresh(TournamentProject next)
    {
        if (IsSeedEditing && editorBaseline is not null && !ProjectEditorBaseline.SameRoster(editorBaseline, next)) editorConflict = true;
        project = next;
        if (!IsSeedEditing) LoadRows();
        foreach (var property in new[] { nameof(Name), nameof(DisplayLabel), nameof(SourceFileName), nameof(SourceHash), nameof(Summary), nameof(Warnings), nameof(HasRoster), nameof(HasWarnings), nameof(ImportLabel), nameof(CheckSummary), nameof(SeedSummary) }) OnPropertyChanged(property);
        RefreshAvailability();
    }
    internal void RefreshAvailability()
    {
        foreach (var row in Rows) row.SetEditable(CanEditSeedFields);
        foreach (var property in new[] { nameof(CanEdit), nameof(CanEditSeedFields), nameof(HasEditorConflict), nameof(EditHint) }) OnPropertyChanged(property);
        ImportCommand?.NotifyCanExecuteChanged(); ExportTemplateCommand?.NotifyCanExecuteChanged(); BeginSeedEditCommand?.NotifyCanExecuteChanged();
        ResetSeedsCommand?.NotifyCanExecuteChanged(); CancelSeedEditCommand?.NotifyCanExecuteChanged(); SaveSeedsCommand?.NotifyCanExecuteChanged();
    }
}

public sealed class RosterSeedRowViewModel(int sourceIndex, DrawParticipant participant, bool isTeam) : ViewModelBase
{
    private bool canEdit;
    private bool isSeed = participant.IsSeed;
    private string seedRankText = participant.SeedRank?.ToString() ?? "";
    public int SourceIndex { get; } = sourceIndex;
    public string Order => (SourceIndex + 1).ToString();
    public string Name => isTeam || string.IsNullOrWhiteSpace(participant.PrimaryName) ? participant.DisplayName : participant.PrimaryName;
    public string Identity => isTeam ? "" : string.Join(" · ", new[] { participant.PrimaryStudentId, participant.TeamName }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public bool HasIdentity => !string.IsNullOrWhiteSpace(Identity);
    public string Partner => string.Join(" · ", new[] { participant.PartnerName, participant.PartnerStudentId, participant.PartnerTeamName }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string Note => participant.Note ?? "";
    public bool CanEdit => canEdit;
    internal void SetEditable(bool value) { if (canEdit != value) { canEdit = value; OnPropertyChanged(nameof(CanEdit)); } }
    public bool IsSeed { get => isSeed; set { if (SetProperty(ref isSeed, value)) OnPropertyChanged(nameof(SeedIssue)); } }
    public string SeedRankText { get => seedRankText; set { if (SetProperty(ref seedRankText, value)) OnPropertyChanged(nameof(SeedIssue)); } }
    public string SeedIssue => !IsSeed && !string.IsNullOrWhiteSpace(SeedRankText) ? "取消种子时请同时清空序号。" : "";
    internal RosterSeedEdit ToEdit()
    {
        int? rank = null;
        if (!string.IsNullOrWhiteSpace(SeedRankText))
        {
            if (!int.TryParse(SeedRankText, out var value) || value <= 0) throw new WorkspaceCommandException(new("roster.seed-rank", $"第 {Order} 行种子序号应为正整数或留空。"));
            rank = value;
        }
        return new(SourceIndex, IsSeed, rank);
    }
}

internal static class ProjectEditorBaseline
{
    internal static bool SameRoster(TournamentProject baseline, TournamentProject current) => baseline.Id == current.Id &&
        baseline.Discipline == current.Discipline && baseline.CompetitionMode == current.CompetitionMode &&
        baseline.Roster?.SourceFileName == current.Roster?.SourceFileName && baseline.Roster?.ContentHash == current.Roster?.ContentHash &&
        (baseline.Roster?.Participants ?? []).SequenceEqual(current.Roster?.Participants ?? []);
    internal static bool SameDraw(TournamentProject baseline, TournamentProject current) => SameRoster(baseline, current) &&
        baseline.Draw?.ConfirmedAt == current.Draw?.ConfirmedAt && baseline.Draw?.Result.Settings == current.Draw?.Result.Settings &&
        baseline.Draw?.Result.Audit == current.Draw?.Result.Audit;
}
