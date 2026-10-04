using System.Text.Json;
using System.ComponentModel;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Controls;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.Scheduling;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class ScheduleBoardPageViewModel : WorkspacePageViewModel, IDisposable
{
    private readonly AppShellViewModel shell;
    private WorkspaceScheduleBoard board;
    private WorkspaceBoardCard? selectedMatch;
    private ScheduleEditBaseline? baseline;
    private string sourceIdentity, targetDay = "", targetTimeText = "", targetCourt = "", previewMessage = "";
    private string? selectedDay;
    private double zoom = 1;
    private bool edited, conflict, disposed, canUndo, initializePending, initializing, isMoveEditorExpanded, isAdjusting, isCompact = true;
    private long editorEpoch;
    private ScheduleEditPreview? preview;
    private WorkspaceSession? previewSession;
    private PlayerEntriesViewModel? playerEntries;
    public WorkspaceScheduleBoard Board { get => board; private set => SetProperty(ref board, value); }
    public string WindowTitle => Session.Workspace.Name + " · 赛程板";
    public string Summary => $"{Board.Days.Count} 个比赛日 · {Board.Cards.Count} 场比赛 · {Board.Cards.Select(c => c.Key.ProjectId).Distinct().Count()} 个项目";
    public IReadOnlyList<ScheduleBoardDaySummary> DaySummaries => Board.Days.Select(day => new ScheduleBoardDaySummary(
        day.DayLabel, day.Courts.Count, Board.Cards.Count(card => card.Placement.DayLabel == day.DayLabel))).ToArray();
    public string StatusSummary => shell.Status.Contains(" 备份：", StringComparison.Ordinal) ? shell.Status[..shell.Status.IndexOf(" 备份：", StringComparison.Ordinal)] : shell.Status;
    public string StatusDetails => shell.Status;
    public bool IsCompact { get => isCompact; set => SetProperty(ref isCompact, value); }
    public bool IsAdjusting { get => isAdjusting; set => SetProperty(ref isAdjusting, value); }
    public IReadOnlyList<WorkspaceBoardCard> Matches => Board.Cards;
    public IReadOnlyList<string> DayLabels => Board.Days.Select(d => d.DayLabel).ToArray();
    public IReadOnlyList<string> TargetCourts => Board.Days.FirstOrDefault(d => d.DayLabel == TargetDay)?.Courts ?? [];
    public WorkspaceBoardCard? SelectedMatch
    {
        get => selectedMatch;
        set
        {
            // A ComboBox may transiently clear SelectedItem while its immutable ItemsSource is replaced.
            // There is no user-facing empty selection; do not erase a retained manual editor in that transition.
            if (value is null && Matches.Count > 0) return;
            if (SetProperty(ref selectedMatch, value)) { IsAdjusting = false; LoadTarget(); RefreshAvailability(); }
        }
    }
    public string? SelectedDay { get => selectedDay; set => SetProperty(ref selectedDay, value); }
    public double Zoom { get => zoom; set => SetProperty(ref zoom, WorkspaceBoardInteraction.ClampZoom(value)); }
    public bool IsMoveEditorExpanded { get => isMoveEditorExpanded; set => SetProperty(ref isMoveEditorExpanded, value); }
    public string TargetDay { get => targetDay; set { if (SetProperty(ref targetDay, value)) { TargetEdited(); OnPropertyChanged(nameof(TargetCourts)); } } }
    public string TargetTimeText { get => targetTimeText; set { if (SetProperty(ref targetTimeText, value)) TargetEdited(); } }
    public string TargetCourt { get => targetCourt; set { if (SetProperty(ref targetCourt, value)) TargetEdited(); } }
    public bool HasEditorConflict => conflict;
    public bool NeedsPositionRefresh => baseline is null || conflict;
    public bool CanEdit => !disposed && shell.CanMutate && baseline is not null && !conflict && Session.Workspace.Stage is TournamentStage.ScheduleReady or TournamentStage.InProgress;
    public string EditHint => conflict ? "赛程、资源或赛果已改变。目标输入仍保留，但必须载入最新位置后重新查看调整方案。" : baseline is null
        ? initializing || initializePending ? "正在载入移动基准，请稍候…" : "请点击载入最新位置以启用移动。"
        : "普通合法拖动自动保存；手动和连锁移动先查看调整方案、再确认。已完成场次锁定；撤销也会按当前赛果重新校验。";
    public string PreviewMessage { get => previewMessage; private set { if (SetProperty(ref previewMessage, value)) OnPropertyChanged(nameof(HasPreviewMessage)); } }
    public bool HasPreviewMessage => !string.IsNullOrWhiteSpace(PreviewMessage);
    public bool IsCascadePreview => preview?.IsCascade == true;
    public IReadOnlyList<ScheduleEditChange> PreviewChanges => preview?.Changes ?? [];
    public IReadOnlyList<ScheduleEditChangeDisplay> ChangeRows => PreviewChanges.Select(c => new ScheduleEditChangeDisplay(c)).ToArray();
    public IReadOnlyList<SchedulingViolation> PreviewViolations => preview?.Violations ?? [];
    public IReadOnlyList<ScheduleBoardIssueViewModel> Issues => PreviewViolations.Select(v => new ScheduleBoardIssueViewModel(v.Message,
        Find(v.MatchId), Find(v.RelatedMatchId), key => FocusRequested?.Invoke(key))).ToArray();
    public AsyncCommand ResetEditorCommand { get; }
    public AsyncCommand PreviewMoveCommand { get; }
    public AsyncCommand PreviewCascadeCommand { get; }
    public AsyncCommand ConfirmMoveCommand { get; }
    public DelegateCommand CancelPreviewCommand { get; }
    public AsyncCommand UndoCommand { get; }
    public DelegateCommand FocusSelectedCommand { get; }
    public DelegateCommand SetupCommand { get; }
    public DelegateCommand OpenWindowCommand { get; }
    public DelegateCommand OperationsCommand { get; }
    public DelegateCommand ShowDetailsCommand { get; }
    public DelegateCommand EditSelectedCommand { get; }
    public DelegateCommand ShowPlayerEntriesCommand { get; }
    public DelegateCommand ToggleThemeCommand => shell.ToggleThemeCommand;
    public event Action<WorkspaceMatchKey>? FocusRequested;
    public ScheduleBoardPageViewModel(AppShellViewModel shell, WorkspaceSession session) : base(session)
    {
        this.shell = shell; board = WorkspaceScheduleBoard.Build(session); sourceIdentity = Source(session);
        selectedDay = board.Days.FirstOrDefault()?.DayLabel; selectedMatch = board.Cards.FirstOrDefault();
        ResetEditorCommand = new(ResetEditorAsync, () => !disposed && !shell.IsBusy, shell.ReportError);
        PreviewMoveCommand = new(() => PreviewAsync(false), CanPreview, shell.ReportError);
        PreviewCascadeCommand = new(() => PreviewAsync(true), CanPreview, shell.ReportError);
        ConfirmMoveCommand = new(ConfirmAsync, () => CanEdit && preview is { CanApply: true, HasChanges: true } && ReferenceEquals(previewSession, Session), shell.ReportError);
        CancelPreviewCommand = new(ClearPreview, () => !disposed && !shell.IsBusy);
        UndoCommand = new(async () =>
        {
            if (await shell.RunWorkspaceCommandAsync(Session, (workflow, revision) => workflow.UndoLastScheduleEdit(revision), "已撤销最近一次赛程编辑并保存。")) await ResetEditorAsync();
        }, () => !disposed && shell.CanMutate && canUndo, shell.ReportError);
        FocusSelectedCommand = new(() => { if (SelectedMatch is { } match) FocusRequested?.Invoke(match.Key); }, () => !disposed && SelectedMatch is not null);
        SetupCommand = new(() => shell.Navigate(WorkspaceRoute.ScheduleSetup), () => !disposed && shell.CanNavigate(WorkspaceRoute.ScheduleSetup));
        OpenWindowCommand = new(() => shell.RequestBoardWindow(this), () => !disposed && shell.CanNavigate(WorkspaceRoute.ScheduleBoard));
        OperationsCommand = new(() => shell.Navigate(WorkspaceRoute.Operations), () => !disposed && shell.CanNavigate(WorkspaceRoute.Operations));
        ShowDetailsCommand = new(() => IsMoveEditorExpanded = true, () => !disposed && SelectedMatch is not null);
        EditSelectedCommand = new(() => { IsMoveEditorExpanded = true; IsAdjusting = true; }, CanPreview);
        ShowPlayerEntriesCommand = new(() =>
        {
            playerEntries ??= new PlayerEntriesViewModel(Session, key =>
            {
                if (!shell.CanNavigate(WorkspaceRoute.ScheduleBoard)) return;
                // Select before attaching the view so its initial editor load captures the right match.
                SelectMatch(key); shell.RequestBoardWindow(this); FocusRequested?.Invoke(key);
            });
            shell.RequestPlayerEntriesWindow(this, playerEntries);
        }, () => !disposed && shell.CanOpenPlayerEntries);
        shell.PropertyChanged += ShellPropertyChanged;
        LoadTarget();
    }
    private void ShellPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppShellViewModel.Status) or nameof(AppShellViewModel.StatusSummary))
        { OnPropertyChanged(nameof(StatusSummary)); OnPropertyChanged(nameof(StatusDetails)); }
    }
    public async Task InitializeAsync()
    {
        if (disposed || baseline is not null || initializing) return;
        if (shell.IsBusy) { initializePending = true; OnPropertyChanged(nameof(EditHint)); return; }
        initializePending = false; initializing = true;
        try { await ResetEditorAsync(); }
        finally { initializing = false; RefreshAvailability(); }
    }
    private bool CanPreview() => CanEdit && SelectedMatch is { IsLocked: false };
    private async Task ResetEditorAsync()
    {
        if (disposed) return;
        var expected = Session; var resetEpoch = editorEpoch;
        var result = await shell.RunWorkspaceQueryAsync(expected, (workflow, _) => new EditorContext(workflow.CaptureScheduleEditBaseline(), workflow.CanUndoScheduleEdit), showStatus: false);
        if (disposed || resetEpoch != editorEpoch || !ReferenceEquals(expected, Session) || !result.Succeeded || result.Value is null) return;
        baseline = result.Value.Baseline; canUndo = result.Value.CanUndo; sourceIdentity = Source(Session); conflict = false; edited = false;
        LoadTarget(); RefreshAvailability();
    }
    private MoveMatchRequest BuildRequest()
    {
        if (!CanPreview() || baseline is null || SelectedMatch is not { } match) throw ScheduleEditorInput.Error("请先选择未完成比赛并载入最新移动基准。");
        if (string.IsNullOrWhiteSpace(TargetDay) || string.IsNullOrWhiteSpace(TargetCourt)) throw ScheduleEditorInput.Error("请选择目标比赛日和场地。");
        return new(match.Key, TargetDay, ScheduleEditorInput.Time(TargetTimeText, "目标时间"), TargetCourt, baseline);
    }
    private async Task PreviewAsync(bool cascade)
    {
        IsMoveEditorExpanded = true;
        IsAdjusting = true;
        var request = BuildRequest(); var expected = Session; ClearPreview(); var requestEpoch = editorEpoch;
        var result = await shell.RunWorkspaceQueryAsync(expected, (workflow, revision) => cascade ? workflow.PreviewCascade(request, revision) : workflow.PreviewMove(request, revision));
        if (disposed || requestEpoch != editorEpoch || !ReferenceEquals(expected, Session)) return;
        if (!result.Succeeded || result.Value is null) { PreviewMessage = result.Error?.Message ?? "未能完成检查。"; return; }
        preview = result.Value; previewSession = expected;
        PreviewMessage = preview.CanApply ? preview.HasChanges ? preview.Changes.Count > 1
            ? $"移动这场比赛还会影响另外 {preview.Changes.Count - 1} 场比赛。以上调整尚未保存，请核对下方变化后确认。"
            : "只调整这场比赛，尚未保存。请核对下方原位置与新位置后确认。" : "目标位置没有变化，无需保存。"
            : "这个位置暂时不能使用，赛程尚未改变。请根据下方原因调整位置，或查看连锁调整方案。";
        NotifyPreview();
    }
    private async Task ConfirmAsync()
    {
        if (preview is not { CanApply: true, HasChanges: true } proposal || !ReferenceEquals(previewSession, Session)) return;
        var expected = Session;
        if (await shell.RunWorkspaceCommandAsync(expected, (workflow, revision) => proposal.IsCascade ? workflow.CascadeMove(proposal, revision) : workflow.MoveMatch(proposal.Root, revision), "赛程移动已保存。"))
            await ResetEditorAsync();
    }
    public async Task RequestMoveAsync(WorkspaceBoardMoveIntent intent)
    {
        if (disposed || !CanEdit) return;
        var match = Matches.FirstOrDefault(c => c.Key == intent.Key);
        if (match is null || match.IsLocked) return;
        SelectedMatch = match;
        TargetDay = intent.DayLabel; TargetTimeText = ScheduleEditorInput.TimeText(intent.StartTime); TargetCourt = intent.Court;
        await PreviewAsync(false);
        // A legal ordinary drag retains v4's autosave gesture; blocked drops only open the read-only explanation.
        if (preview is { CanApply: true, HasChanges: true, IsCascade: false } && ReferenceEquals(previewSession, Session)) await ConfirmAsync();
    }
    public async Task<BoardHoverFeedback> PreviewHoverAsync(WorkspaceBoardMoveIntent intent)
    {
        if (disposed || !CanEdit || baseline is null) return new(false, "当前不能移动，请载入最新位置。");
        var expected = Session; var request = new MoveMatchRequest(intent.Key, intent.DayLabel, intent.StartTime, intent.Court, baseline);
        var result = await shell.RunWorkspaceQueryAsync(expected, (workflow, revision) => workflow.PreviewMove(request, revision), background: true);
        if (disposed || !ReferenceEquals(expected, Session)) return new(false, "工作区已更新，请重新拖动。");
        return result.Succeeded && result.Value is { } value ? new(value.CanApply, value.CanApply ? "可移动；松开后再次校验并自动保存。" :
            "不能直接移动；松开查看原因或查看连锁调整方案。" + string.Join("；", value.Violations.Select(v => v.Message))) : new(false, result.Error?.Message ?? "未能检查目标位置。");
    }
    public void SelectMatch(WorkspaceMatchKey key)
    {
        if (Matches.FirstOrDefault(c => c.Key == key) is not { } match) return;
        SelectedMatch = match;
        IsMoveEditorExpanded = true;
    }
    public void ReportError(Exception exception) { if (!disposed) shell.ReportError(exception); }
    private WorkspaceMatchKey? Find(Guid? matchId) => matchId is { } id ? Matches.FirstOrDefault(c => c.Key.MatchId == id)?.Key : null;
    private void TargetEdited() { edited = true; ClearPreview(); }
    private void LoadTarget()
    {
        if (SelectedMatch is { } match) { targetDay = match.Placement.DayLabel; targetTimeText = ScheduleEditorInput.TimeText(match.Placement.StartTime); targetCourt = match.Placement.Court; }
        else { targetDay = ""; targetTimeText = ""; targetCourt = ""; }
        foreach (var property in new[] { nameof(TargetDay), nameof(TargetTimeText), nameof(TargetCourt), nameof(TargetCourts) }) OnPropertyChanged(property);
        ClearPreview();
    }
    private void ClearPreview() { ++editorEpoch; preview = null; previewSession = null; PreviewMessage = ""; NotifyPreview(); }
    private void NotifyPreview()
    {
        foreach (var property in new[] { nameof(PreviewChanges), nameof(ChangeRows), nameof(PreviewViolations), nameof(IsCascadePreview), nameof(Issues) }) OnPropertyChanged(property);
        ConfirmMoveCommand?.NotifyCanExecuteChanged();
    }
    private static string Source(WorkspaceSession session) => JsonSerializer.Serialize(new { session.Workspace.Projects, session.Workspace.Resources, session.Workspace.Schedule,
        Results = session.Workspace.Results.Values.OrderBy(r => r.Key.ProjectId).ThenBy(r => r.Key.MatchId).ToArray() });
    public override void RefreshSession(WorkspaceSession next)
    {
        var changed = sourceIdentity != Source(next); var selectedKey = SelectedMatch?.Key;
        if (changed) { conflict = true; canUndo = false; }
        base.RefreshSession(next); Board = WorkspaceScheduleBoard.Build(next);
        playerEntries?.RefreshSession(next);
        selectedMatch = Matches.FirstOrDefault(c => c.Key == selectedKey) ?? Matches.FirstOrDefault();
        if (!edited && !changed) LoadTarget();
        ClearPreview();
        if (!DayLabels.Contains(SelectedDay)) SelectedDay = DayLabels.FirstOrDefault();
        foreach (var property in new[] { nameof(Matches), nameof(SelectedMatch), nameof(DayLabels), nameof(TargetCourts) }) OnPropertyChanged(property);
        foreach (var property in new[] { nameof(WindowTitle), nameof(Summary), nameof(DaySummaries) }) OnPropertyChanged(property);
        RefreshAvailability();
    }
    public override void RefreshAvailability()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(HasEditorConflict)); OnPropertyChanged(nameof(NeedsPositionRefresh)); OnPropertyChanged(nameof(EditHint));
        foreach (var command in new[] { ResetEditorCommand, PreviewMoveCommand, PreviewCascadeCommand, ConfirmMoveCommand, UndoCommand }) command?.NotifyCanExecuteChanged();
        CancelPreviewCommand?.NotifyCanExecuteChanged(); FocusSelectedCommand?.NotifyCanExecuteChanged(); SetupCommand?.NotifyCanExecuteChanged();
        OpenWindowCommand?.NotifyCanExecuteChanged(); OperationsCommand?.NotifyCanExecuteChanged();
        ShowDetailsCommand?.NotifyCanExecuteChanged(); EditSelectedCommand?.NotifyCanExecuteChanged();
        ShowPlayerEntriesCommand?.NotifyCanExecuteChanged();
        // Native attachment can occur inside Open's busy ApplySession. Retry once when that operation releases busy.
        if (initializePending && !disposed && !shell.IsBusy) _ = InitializeAsync();
    }
    public void Dispose() { disposed = true; shell.PropertyChanged -= ShellPropertyChanged; baseline = null; ClearPreview(); playerEntries?.Dispose(); FocusRequested = null; RefreshAvailability(); }
    private sealed record EditorContext(ScheduleEditBaseline Baseline, bool CanUndo);
}

public sealed record ScheduleBoardDaySummary(string DayLabel, int CourtCount, int MatchCount)
{
    public string Details => $"{CourtCount} 片场地 · {MatchCount} 场比赛";
}

public sealed record ScheduleEditChangeDisplay(ScheduleEditChange Change)
{
    public string Title => Change.ProjectName + " · " + Change.MatchName + (Change.Depth > 0 ? $"（后续 {Change.Depth} 级）" : "");
    public string Before => $"原：{Change.Before.DayLabel} {ScheduleEditorInput.TimeText(Change.Before.StartTime)}–{ScheduleEditorInput.TimeText(Change.Before.EndTime)} · {Change.Before.Court}";
    public string After => $"新：{Change.After.DayLabel} {ScheduleEditorInput.TimeText(Change.After.StartTime)}–{ScheduleEditorInput.TimeText(Change.After.EndTime)} · {Change.After.Court}";
}

public sealed class ScheduleBoardIssueViewModel
{
    public string Message { get; }
    public DelegateCommand FocusCommand { get; }
    public DelegateCommand FocusRelatedCommand { get; }
    public ScheduleBoardIssueViewModel(string message, WorkspaceMatchKey? key, WorkspaceMatchKey? related, Action<WorkspaceMatchKey> focus)
    {
        Message = message; FocusCommand = new(() => { if (key is { } value) focus(value); }, () => key.HasValue);
        FocusRelatedCommand = new(() => { if (related is { } value) focus(value); }, () => related.HasValue);
    }
}
