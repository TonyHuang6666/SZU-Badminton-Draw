using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class ProjectDrawViewModel : ViewModelBase
{
    private readonly PublicDrawPageViewModel page;
    private TournamentProject project;
    private TournamentProject editorBaseline;
    private string groupCountText = "1", randomSeed = "", reopenReason = "";
    private int knockoutGoalIndex, placementPlayoffIndex;
    private bool edited, editorConflict, acknowledgeInvalidation;
    public Guid ProjectId => project.Id;
    public string Name => project.DisplayName;
    public string Mode => project.CompetitionMode is CompetitionMode.TeamKnockout or CompetitionMode.SinglesKnockout ? "分组淘汰赛" : "分组循环赛";
    public bool IsKnockout => project.CompetitionMode is CompetitionMode.TeamKnockout or CompetitionMode.SinglesKnockout;
    public string GroupCountText { get => groupCountText; set { if (SetProperty(ref groupCountText, value)) Edited(); } }
    public string RandomSeed { get => randomSeed; set { if (SetProperty(ref randomSeed, value)) Edited(); } }
    public IReadOnlyList<string> KnockoutGoals { get; } = ["每组决出一名出线者", "继续决出总冠军"];
    public int KnockoutGoalIndex { get => knockoutGoalIndex; set { if (SetProperty(ref knockoutGoalIndex, value)) Edited(); } }
    public IReadOnlyList<string> PlacementPlayoffs { get; } = ["不加赛名次", "三、四名加赛", "第三至第八名排位赛"];
    public int PlacementPlayoffIndex { get => placementPlayoffIndex; set { if (SetProperty(ref placementPlayoffIndex, value)) Edited(); } }
    public bool ShowKnockoutGoal => IsKnockout && int.TryParse(GroupCountText, out var groups) && groups > 1 && (groups & (groups - 1)) == 0;
    public bool ShowPlacementPlayoff => IsKnockout && EffectiveGoal == KnockoutGoal.Champion;
    public string GoalHint => !IsKnockout ? "循环赛按组生成全部对阵。" : ShowKnockoutGoal ? "可选择仅小组出线，或继续决出总冠军。"
        : int.TryParse(GroupCountText, out var count) && count == 1 ? "单组淘汰赛决出总冠军。" : "非二的幂次分组仅决出各组出线者。";
    public bool HasEditorConflict => editorConflict;
    public bool IsConfirmed => project.Draw?.ConfirmedAt is not null;
    public bool HasPreview => project.Draw is not null;
    public bool CanEditSettings => page.Shell.CanMutate && !IsConfirmed && !editorConflict && page.Session.Workspace.Results.Count == 0;
    public string Status => IsConfirmed ? "已确认抽签结果（只读）" : HasPreview ? "未确认抽签预览" : "尚未抽签";
    public string EditHint => editorConflict ? "名单或抽签设置已更新，旧输入已保留。请载入最新设置后再抽签或确认。"
        : HasPreview && !MatchesPreview ? "当前输入不同于已保存预览。请明确重新预览，或载入预览设置后确认；导出仍使用已保存预览。"
        : "导入、查看和导出均不会自动抽签。请在公开会议中主动点击生成预览，检查后再确认。";
    public string DrawAudit => project.Draw is not { } draw ? "尚未生成抽签审计。原始名单文件 SHA-256：" + project.Roster?.ContentHash
        : $"随机种子：{draw.Result.Audit.RandomSeed}\n当前名单 SHA-256：{draw.Result.Audit.InputHash}\n算法：{draw.Result.Audit.AlgorithmVersion}\n生成时间：{draw.Result.Audit.GeneratedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n参赛方：{draw.Result.Audit.ParticipantCount} · 种子：{draw.Result.Audit.SeedCount} · 分组：{draw.Result.Audit.GroupCount}\n确认时间：{(draw.ConfirmedAt is { } confirmed ? confirmed.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : "尚未确认")}";
    public IReadOnlyList<DrawGroupDisplay> Groups => project.Draw is not { } draw ? [] : draw.Result.Groups.Select(group =>
        new DrawGroupDisplay($"第 {group.Number} 组 · {group.Count} 组参赛方", string.Join(Environment.NewLine, group.Participants.Select((p, i) => $"{i + 1}. {Format(p)}")),
            IsKnockout ? "首轮参赛：" + FormatGroup(draw.Result.RoundOneGroups.FirstOrDefault(g => g.Number == group.Number)) +
                "\n直接晋级：" + FormatGroup(draw.Result.ByeGroups.FirstOrDefault(g => g.Number == group.Number)) : "组内进行单循环赛")).ToArray();
    public string ReopenReason { get => reopenReason; set { if (SetProperty(ref reopenReason, value)) RefreshAvailability(); } }
    public bool AcknowledgeInvalidation { get => acknowledgeInvalidation; set { if (SetProperty(ref acknowledgeInvalidation, value)) RefreshAvailability(); } }
    public bool CanReopen => IsConfirmed && page.Session.Workspace.Results.Count == 0;
    public AsyncCommand PreviewDrawCommand { get; }
    public AsyncCommand ConfirmDrawCommand { get; }
    public AsyncCommand ExportPreviewCommand { get; }
    public AsyncCommand ExportConfirmedCommand { get; }
    public AsyncCommand ReopenCommand { get; }
    public DelegateCommand ResetSettingsCommand { get; }
    public DelegateCommand GenerateSeedCommand { get; }

    public ProjectDrawViewModel(PublicDrawPageViewModel page, TournamentProject project)
    {
        this.page = page; this.project = project; editorBaseline = project;
        PreviewDrawCommand = new(async () =>
        {
            var expected = page.Session; var id = ProjectId; var settings = BuildSettings();
            if (await page.Shell.RunWorkspaceCommandAsync(expected, (workflow, revision) => workflow.PreviewDraw(id, settings, revision),
                "抽签预览已保存，尚未确认或排程。")) LoadSettings();
        }, () => CanEditSettings && page.Session.Workspace.Stage >= TournamentStage.RostersReady, page.Shell.ReportError);
        ConfirmDrawCommand = new(async () =>
        {
            var expected = page.Session; var id = ProjectId;
            if (await page.Shell.RunWorkspaceCommandAsync(expected, (workflow, revision) => workflow.ConfirmDraw(id, revision),
                "此项目抽签已确认，比赛结构已保存；尚未生成赛程。")) LoadSettings();
        }, () => CanEditSettings && HasPreview && MatchesPreview, page.Shell.ReportError);
        ExportPreviewCommand = new(() => page.ExportAsync(ProjectId, Workflows.Tournaments.DrawExportState.Preview),
            () => page.Shell.CanMutate && HasPreview && !IsConfirmed, page.Shell.ReportError);
        ExportConfirmedCommand = new(() => page.ExportAsync(ProjectId, Workflows.Tournaments.DrawExportState.Confirmed),
            () => page.Shell.CanMutate && IsConfirmed, page.Shell.ReportError);
        ReopenCommand = new(async () =>
        {
            var expected = page.Session; var id = ProjectId; var reason = ReopenReason.Trim();
            if (await page.Shell.RunWorkspaceCommandAsync(expected, (workflow, revision) => workflow.ReopenDraw(id, reason, revision),
                "此项目已解除抽签确认，其比赛结构与全局赛程已作废；请重新审核名单和抽签。"))
            { AcknowledgeInvalidation = false; ReopenReason = ""; LoadSettings(); }
        }, () => page.Shell.CanMutate && CanReopen && AcknowledgeInvalidation && !string.IsNullOrWhiteSpace(ReopenReason), page.Shell.ReportError);
        ResetSettingsCommand = new(LoadSettings, () => !page.Shell.IsBusy);
        GenerateSeedCommand = new(() => RandomSeed = Guid.NewGuid().ToString("N"), () => CanEditSettings);
        LoadSettings();
    }
    // Round-robin has no goal editor: preserve its stored, inert knockout flag instead of requiring a new draw.
    private KnockoutGoal EffectiveGoal => !IsKnockout ? project.Draw?.Result.Settings.KnockoutGoal ?? KnockoutGoal.Champion
        : int.TryParse(GroupCountText, out var count) && count == 1 ? KnockoutGoal.Champion
        : ShowKnockoutGoal && KnockoutGoalIndex == 1 ? KnockoutGoal.Champion : KnockoutGoal.OneQualifierPerGroup;
    private DrawSettings BuildSettings()
    {
        if (!int.TryParse(GroupCountText, out var count) || count < 1) throw new Workflows.Tournaments.WorkspaceCommandException(new("draw.group-count", "分组数量应为正整数。"));
        var kind = project.Discipline switch { EventDiscipline.Team => EventKind.Team,
            EventDiscipline.MenDoubles or EventDiscipline.WomenDoubles or EventDiscipline.MixedDoubles => EventKind.Doubles, _ => EventKind.Singles };
        var placement = ShowPlacementPlayoff ? PlacementPlayoffIndex switch { 1 => PlacementPlayoff.ThirdPlace, 2 => PlacementPlayoff.ThirdToEighth, _ => PlacementPlayoff.None } : PlacementPlayoff.None;
        return new(project.CompetitionMode, kind, count, RandomSeed.Trim(), KnockoutGoal: EffectiveGoal, PlacementPlayoff: placement);
    }
    private bool MatchesPreview
    {
        get { try { return project.Draw?.Result.Settings == BuildSettings(); } catch (Workflows.Tournaments.WorkspaceCommandException) { return false; } }
    }
    private void Edited() { edited = true; RefreshAvailability(); }
    private void LoadSettings()
    {
        editorBaseline = project; editorConflict = false; edited = false;
        var settings = project.Draw?.Result.Settings;
        groupCountText = settings?.GroupCount.ToString() ?? "1";
        randomSeed = settings?.RandomSeed ?? Guid.NewGuid().ToString("N");
        knockoutGoalIndex = settings?.KnockoutGoal == KnockoutGoal.Champion ? 1 : 0;
        placementPlayoffIndex = settings?.PlacementPlayoff switch { PlacementPlayoff.ThirdPlace => 1, PlacementPlayoff.ThirdToEighth => 2, _ => 0 };
        foreach (var name in new[] { nameof(GroupCountText), nameof(RandomSeed), nameof(KnockoutGoalIndex), nameof(PlacementPlayoffIndex) }) OnPropertyChanged(name);
        RefreshAvailability();
    }
    internal void Refresh(TournamentProject next)
    {
        var changed = !ProjectEditorBaseline.SameDraw(editorBaseline, next);
        if (edited && changed) editorConflict = true;
        project = next;
        if (changed && !edited && !editorConflict) LoadSettings();
        foreach (var name in new[] { nameof(Name), nameof(Mode), nameof(IsKnockout), nameof(DrawAudit), nameof(Groups), nameof(Status), nameof(IsConfirmed), nameof(HasPreview) }) OnPropertyChanged(name);
        RefreshAvailability();
    }
    internal void RefreshAvailability()
    {
        foreach (var name in new[] { nameof(CanEditSettings), nameof(HasEditorConflict), nameof(EditHint), nameof(ShowKnockoutGoal), nameof(ShowPlacementPlayoff), nameof(GoalHint), nameof(CanReopen) }) OnPropertyChanged(name);
        PreviewDrawCommand?.NotifyCanExecuteChanged(); ConfirmDrawCommand?.NotifyCanExecuteChanged(); ExportPreviewCommand?.NotifyCanExecuteChanged();
        ExportConfirmedCommand?.NotifyCanExecuteChanged(); ReopenCommand?.NotifyCanExecuteChanged(); ResetSettingsCommand?.NotifyCanExecuteChanged(); GenerateSeedCommand?.NotifyCanExecuteChanged();
    }
    private static string Format(DrawParticipant participant) => participant.DisplayName + (participant.IsSeed ? participant.SeedRank is { } rank ? $"（{rank} 号种子）" : "（种子）" : "");
    private static string FormatGroup(DrawGroup? group) => group is { Count: > 0 } ? string.Join("、", group.Participants.Select(Format)) : "无";
}

public sealed record DrawGroupDisplay(string Title, string Participants, string Advancement);
