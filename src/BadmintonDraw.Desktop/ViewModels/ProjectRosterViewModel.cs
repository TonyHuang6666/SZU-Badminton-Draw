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
    private bool overwriteTemplate;
    private string exportDetails = "";
    public Guid ProjectId => project.Id;
    public string Name => project.DisplayName;
    public string SourceFileName => project.Roster?.SourceFileName ?? "尚未导入";
    public string SourceHash => project.Roster?.ContentHash ?? "";
    public string Summary => project.Roster is null ? "待导入名单" : $"{project.Roster.Participants.Count} 组参赛方 · {(project.Draw?.ConfirmedAt is not null ? "抽签已确认，名单锁定" : "可审核种子设置")}";
    public string Warnings => string.Join(Environment.NewLine, project.Roster?.Warnings.Select(w => w.Message) ?? []);
    public bool IsSeedEditing { get => isSeedEditing; private set { SetProperty(ref isSeedEditing, value); RefreshAvailability(); } }
    public bool HasEditorConflict => editorConflict;
    public bool CanEdit => page.Shell.CanMutate && project.Draw?.ConfirmedAt is null && page.Session.Workspace.Results.Count == 0;
    public bool CanEditSeedFields => CanEdit && IsSeedEditing && !editorConflict;
    public bool OverwriteTemplate { get => overwriteTemplate; set => SetProperty(ref overwriteTemplate, value); }
    public string ExportDetails { get => exportDetails; private set => SetProperty(ref exportDetails, value); }
    public string EditHint => editorConflict ? "名单或项目设置已更新，旧输入已保留但不能保存。请载入最新种子设置后重新审核。"
        : project.Draw?.ConfirmedAt is not null ? "抽签已确认。需要更改时，请到公开抽签页明确解除确认。"
        : "只有种子标记及序号可编辑；姓名、学号和搭档资料请通过替换名单更正。原文件哈希只证明导入来源。";
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
        ResetSeedsCommand = new(LoadRows, () => !page.Shell.IsBusy && this.project.Roster is not null);
        CancelSeedEditCommand = new(() => { IsSeedEditing = false; LoadRows(); }, () => !page.Shell.IsBusy && IsSeedEditing);
        SaveSeedsCommand = new(async () =>
        {
            var expected = page.Session; var projectId = ProjectId;
            var edits = Rows.Select(row => row.ToEdit()).ToArray();
            if (await page.Shell.RunWorkspaceCommandAsync(expected, (workflow, revision) => workflow.UpdateRosterSeeds(projectId, edits, revision),
                "种子设置已保存，原有未确认抽签预览已清除。")) { IsSeedEditing = false; LoadRows(); }
        }, () => CanEditSeedFields, page.Shell.ReportError);
        LoadRows();
    }
    private async Task ExportTemplateAsync()
    {
        var expected = page.Session; var projectId = ProjectId; var name = Name;
        var overwrite = OverwriteTemplate; OverwriteTemplate = false;
        var path = await page.TemplatePicker(name + "_名单模板");
        if (path is null) return;
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
            foreach (var (participant, index) in roster.Participants.Select((p, i) => (p, i))) Rows.Add(new(index, participant));
        RefreshAvailability();
    }
    internal void Refresh(TournamentProject next)
    {
        if (IsSeedEditing && editorBaseline is not null && !ProjectEditorBaseline.SameRoster(editorBaseline, next)) editorConflict = true;
        project = next;
        if (!IsSeedEditing) LoadRows();
        foreach (var property in new[] { nameof(Name), nameof(SourceFileName), nameof(SourceHash), nameof(Summary), nameof(Warnings) }) OnPropertyChanged(property);
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

public sealed class RosterSeedRowViewModel(int sourceIndex, DrawParticipant participant) : ViewModelBase
{
    private bool canEdit;
    private bool isSeed = participant.IsSeed;
    private string seedRankText = participant.SeedRank?.ToString() ?? "";
    public int SourceIndex { get; } = sourceIndex;
    public string Order => (SourceIndex + 1).ToString();
    public string Name => participant.PrimaryName ?? participant.DisplayName;
    public string Identity => string.Join(" · ", new[] { participant.PrimaryStudentId, participant.TeamName }.Where(s => !string.IsNullOrWhiteSpace(s)));
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
