using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class ProjectDrawViewModel : ViewModelBase
{
    private readonly PublicDrawPageViewModel page;
    private TournamentProject project;
    private TournamentProject editorBaseline;
    private DrawSettings? settingsBaseline;
    private readonly string initialRandomSeed = Guid.NewGuid().ToString("N");
    private string groupCountText = "1", randomSeed = "", reopenReason = "";
    private int knockoutGoalIndex, placementPlayoffIndex;
    private bool editorConflict, acknowledgeInvalidation;
    public Guid ProjectId => project.Id;
    public string Name => project.DisplayName;
    public string DisplayLabel => WorkspaceProjectDisplay.Label(page.Session.Workspace, project);
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
    public bool HasUnsavedSettings
    {
        get { try { return settingsBaseline != BuildSettings(); } catch (Workflows.Tournaments.WorkspaceCommandException) { return true; } }
    }
    public bool ShowDiscardSettings => HasUnsavedSettings && !HasEditorConflict;
    public bool IsConfirmed => project.Draw?.ConfirmedAt is not null;
    public bool HasPreview => project.Draw is not null;
    public string PendingDrawLabel => $"{DisplayLabel} · {(HasPreview ? "待确认" : "未抽签")}";
    public bool IsPreviewPending => HasPreview && !IsConfirmed;
    public bool ShowPreviewAction => !IsConfirmed && (!HasPreview || !MatchesPreview || editorConflict);
    public bool ShowConfirmAction => IsPreviewPending && MatchesPreview && !editorConflict;
    public string PreviewActionLabel => HasPreview ? "按当前设置重新抽签" : "开始公开抽签";
    public string PreparationSummary => $"{project.Roster?.Participants.Count ?? 0} 个参赛方 · {project.Roster?.Participants.Count(p => p.IsSeed) ?? 0} 个种子 · {Mode}";
    public string StageHint => IsConfirmed ? "结果已确认。可以导出并结束本次工作，也可以继续安排比赛时间。"
        : HasPreview ? "请向参赛者展示下方结果，核对分组与种子位置，再确认本项目抽签。"
        : "核对参赛人数和分组方式，准备好后点击「开始公开抽签」。";
    public bool CanEditSettings => page.Shell.CanMutate && !IsConfirmed && !editorConflict && page.Session.Workspace.Results.Count == 0;
    public string Status => IsConfirmed ? "✓ 抽签已确认" : HasPreview ? "待确认抽签结果" : "准备开始抽签";
    public string EditHint => editorConflict ? "名单或抽签设置已更新，旧输入已保留。请放弃旧输入并使用最新设置，再检查抽签或确认。"
        : HasPreview && !MatchesPreview ? "设置已修改，但抽签结果尚未改变。请按当前设置重新抽签，或点击「放弃未保存的修改」恢复这份结果使用的设置后确认；导出仍使用已保存的抽签结果。"
        : "导入、查看和导出均不会自动抽签。请在公开会议中点击「开始公开抽签」，核对结果后再确认。";
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
    public DelegateCommand ReviewDrawCommand { get; }

    public ProjectDrawViewModel(PublicDrawPageViewModel page, TournamentProject project)
    {
        this.page = page; this.project = project; editorBaseline = project;
        ReviewDrawCommand = new(() => page.SelectedProject = this,
            () => page.CanReviewPendingDraws && page.Projects.Contains(this) && !IsConfirmed);
        PreviewDrawCommand = new(async () =>
        {
            var expected = page.Session; var id = ProjectId; var settings = BuildSettings();
            if (await page.Shell.RunWorkspaceCommandAsync(expected, (workflow, revision) => workflow.PreviewDraw(id, settings, revision),
                "抽签结果已保存，请核对后确认；尚未安排比赛时间。")) LoadSettings();
        }, () => CanEditSettings && page.Session.Workspace.Stage >= TournamentStage.RostersReady, page.Shell.ReportError);
        ConfirmDrawCommand = new(async () =>
        {
            var expected = page.Session; var id = ProjectId;
            if (await page.Shell.RunWorkspaceCommandAsync(expected, (workflow, revision) => workflow.ConfirmDraw(id, revision),
                "此项目抽签已确认，比赛结构已保存；尚未生成赛程。")) LoadSettings();
        }, () => CanEditSettings && HasPreview && MatchesPreview, page.Shell.ReportError);
        ExportPreviewCommand = new(() => page.ExportAsync(ProjectId, Workflows.Tournaments.DrawExportState.Preview),
            () => page.CanStartExport && HasPreview && !IsConfirmed, page.Shell.ReportError);
        ExportConfirmedCommand = new(() => page.ExportAsync(ProjectId, Workflows.Tournaments.DrawExportState.Confirmed),
            () => page.CanStartExport && IsConfirmed, page.Shell.ReportError);
        ReopenCommand = new(async () =>
        {
            var expected = page.Session; var id = ProjectId; var reason = ReopenReason.Trim();
            if (await page.Shell.RunWorkspaceCommandAsync(expected, (workflow, revision) => workflow.ReopenDraw(id, reason, revision),
                "此项目已解除抽签确认，其比赛结构与全局赛程已作废；请重新审核名单和抽签。"))
            { AcknowledgeInvalidation = false; ReopenReason = ""; LoadSettings(); }
        }, () => page.Shell.CanMutate && CanReopen && AcknowledgeInvalidation && !string.IsNullOrWhiteSpace(ReopenReason), page.Shell.ReportError);
        ResetSettingsCommand = new(LoadSettings, () => !page.Shell.IsBusy && (HasUnsavedSettings || HasEditorConflict));
        GenerateSeedCommand = new(() => RandomSeed = Guid.NewGuid().ToString("N"), () => CanEditSettings);
        LoadSettings();
    }
    // Round-robin has no goal editor: preserve its stored, inert knockout flag instead of requiring a new draw.
    private KnockoutGoal EffectiveGoal => !IsKnockout ? settingsBaseline?.KnockoutGoal ?? project.Draw?.Result.Settings.KnockoutGoal ?? KnockoutGoal.Champion
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
    private void Edited() => RefreshAvailability();
    private void LoadSettings()
    {
        editorBaseline = project; editorConflict = false; settingsBaseline = null;
        var settings = project.Draw?.Result.Settings;
        groupCountText = settings?.GroupCount.ToString() ?? "1";
        randomSeed = settings?.RandomSeed ?? initialRandomSeed;
        knockoutGoalIndex = settings?.KnockoutGoal == KnockoutGoal.Champion ? 1 : 0;
        placementPlayoffIndex = settings?.PlacementPlayoff switch { PlacementPlayoff.ThirdPlace => 1, PlacementPlayoff.ThirdToEighth => 2, _ => 0 };
        settingsBaseline = settings ?? BuildSettings();
        foreach (var name in new[] { nameof(GroupCountText), nameof(RandomSeed), nameof(KnockoutGoalIndex), nameof(PlacementPlayoffIndex) }) OnPropertyChanged(name);
        RefreshAvailability();
    }
    internal void Refresh(TournamentProject next)
    {
        var changed = !ProjectEditorBaseline.SameDraw(editorBaseline, next);
        var edited = HasUnsavedSettings;
        if (edited && changed) editorConflict = true;
        project = next;
        if (changed && !edited && !editorConflict) LoadSettings();
        foreach (var name in new[] { nameof(Name), nameof(DisplayLabel), nameof(Mode), nameof(IsKnockout), nameof(DrawAudit), nameof(Groups), nameof(Status), nameof(IsConfirmed), nameof(HasPreview), nameof(IsPreviewPending), nameof(PreparationSummary), nameof(StageHint), nameof(PreviewActionLabel), nameof(PendingDrawLabel) }) OnPropertyChanged(name);
        RefreshAvailability();
    }
    internal void RefreshAvailability()
    {
        foreach (var name in new[] { nameof(CanEditSettings), nameof(HasEditorConflict), nameof(HasUnsavedSettings), nameof(ShowDiscardSettings), nameof(EditHint), nameof(ShowKnockoutGoal), nameof(ShowPlacementPlayoff), nameof(GoalHint), nameof(CanReopen), nameof(ShowPreviewAction), nameof(ShowConfirmAction) }) OnPropertyChanged(name);
        PreviewDrawCommand?.NotifyCanExecuteChanged(); ConfirmDrawCommand?.NotifyCanExecuteChanged(); ExportPreviewCommand?.NotifyCanExecuteChanged();
        ExportConfirmedCommand?.NotifyCanExecuteChanged(); ReopenCommand?.NotifyCanExecuteChanged(); ResetSettingsCommand?.NotifyCanExecuteChanged(); GenerateSeedCommand?.NotifyCanExecuteChanged();
        ReviewDrawCommand?.NotifyCanExecuteChanged();
    }
    private static string Format(DrawParticipant participant) => participant.DisplayName + (participant.IsSeed ? participant.SeedRank is { } rank ? $"（{rank} 号种子）" : "（种子）" : "");
    private static string FormatGroup(DrawGroup? group) => group is { Count: > 0 } ? string.Join("、", group.Participants.Select(Format)) : "无";
}

public sealed record DrawGroupDisplay(string Title, string Participants, string Advancement);
