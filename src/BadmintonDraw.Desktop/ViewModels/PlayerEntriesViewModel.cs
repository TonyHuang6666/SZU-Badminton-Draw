using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Scheduling;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

/// <summary>Read-only view of the live workspace; never owns an editor or saves a tournament.</summary>
public sealed class PlayerEntriesViewModel : ViewModelBase, IDisposable
{
    private WorkspaceSession session;
    private readonly Action<WorkspaceMatchKey> focus;
    private bool disposed;
    private bool hasSchedule;
    private string searchText = "";
    private string? selectedIdentity;
    private int selectedSortIndex;
    private PlayerEntryRowViewModel? selectedPlayer;
    private IReadOnlyList<PlayerEntrySummary> entries = [];
    private IReadOnlyList<PlayerEntryRowViewModel> players = [];
    private IReadOnlyList<PlayerAppearanceViewModel> confirmedMatches = [], potentialMatches = [];

    public PlayerEntriesViewModel(WorkspaceSession session, Action<WorkspaceMatchKey> focus)
    {
        this.session = session; this.focus = focus;
        Rebuild();
    }
    public string WindowTitle => session.Workspace.Name + " · 选手兼项";
    public string Header => entries.Any(p => p.IdentityKey.StartsWith("team:", StringComparison.Ordinal))
        ? $"兼项报名身份 {entries.Count} 个 · 按报名身份汇总" : $"兼项选手 {entries.Count} 人 · 按报名身份汇总";
    public bool HasSchedule => hasSchedule;
    public bool HasNoSchedule => !HasSchedule;
    public bool CanSortByRest => HasSchedule;
    public string NoScheduleGuidance => "尚未生成赛程；生成后将展示场次、冲突和休息间隔，并可定位到赛程板。";
    public string SearchText
    {
        get => searchText;
        set { if (SetProperty(ref searchText, value ?? "")) ApplySort(); }
    }
    public bool HasNoSearchResults => entries.Count > 0 && Players.Count == 0;
    public IReadOnlyList<PlayerEntryRegistration> Registrations => SelectedPlayer?.Entry.Registrations ?? [];
    public string IdentityWarningText => string.Join("\n", SelectedPlayer?.Entry.IdentityWarnings ?? []);
    public bool HasIdentityWarnings => SelectedPlayer?.Entry.IdentityWarnings.Count > 0;
    public string RegistrationScopeText => SelectedPlayer?.Entry.RegistrationScopeText ?? "";
    public bool HasRegistrationScopeText => RegistrationScopeText.Length > 0;
    public IReadOnlyList<string> SortOptions => HasSchedule
        ? ["默认排序", "休息时间从短到长", "休息时间从长到短"] : ["默认排序"];
    public IReadOnlyList<PlayerEntryRowViewModel> Players => players;
    public bool HasPlayers => entries.Count > 0;
    public bool HasNoPlayers => !HasPlayers;
    public string EmptyTitle => session.Workspace.Kind == TournamentKind.Team ? "暂无兼项队伍" : "暂无兼项选手";
    public string EmptyGuidance => session.Workspace.Kind == TournamentKind.Team
        ? "队伍按报名身份匹配；只报名一个项目的队伍不会列在这里。"
        : "选手按报名身份匹配；只报名一个项目的选手不会列在这里。";
    public bool HasSelection => SelectedPlayer is not null;
    public bool IsStale => disposed || session.RequiresReload;
    public int SelectedSortIndex
    {
        get => selectedSortIndex;
        set { if (SetProperty(ref selectedSortIndex, HasSchedule ? Math.Clamp(value, 0, 2) : 0)) ApplySort(); }
    }
    public PlayerEntryRowViewModel? SelectedPlayer
    {
        get => selectedPlayer;
        set
        {
            if (SetProperty(ref selectedPlayer, value))
            {
                if (value is not null) selectedIdentity = value.IdentityKey;
                RefreshDetails();
            }
        }
    }
    public string DetailTitle => SelectedPlayer?.DisplayName ?? "选择一位选手";
    public string ProjectText => SelectedPlayer?.ProjectText ?? "";
    public string SelectedSummary => SelectedPlayer?.Entry.Summary ?? "";
    public string RestSummary => SelectedPlayer?.RestSummary ?? "";
    public string RiskSummary => SelectedPlayer?.Entry.RiskSummary ?? "";
    public IReadOnlyList<PlayerAppearanceViewModel> ConfirmedMatches => confirmedMatches;
    public IReadOnlyList<PlayerAppearanceViewModel> PotentialMatches => potentialMatches;
    public bool HasNoConfirmedMatches => HasSchedule && HasSelection && confirmedMatches.Count == 0;
    public bool HasPotentialMatches => potentialMatches.Count > 0;
    public string PotentialHeading => $"后续可能参加 · {PotentialMatches.Count} 场（结果未定）";

    public void RefreshSession(WorkspaceSession next)
    {
        if (disposed) return;
        if (session.Workspace.Id != next.Workspace.Id || session.WorkspacePath != next.WorkspacePath)
        {
            Dispose(); entries = []; hasSchedule = false; selectedIdentity = null; ApplySort(); return;
        }
        session = next;
        if (next.RequiresReload)
        {
            OnPropertyChanged(nameof(IsStale));
            foreach (var match in confirmedMatches.Concat(potentialMatches)) match.FocusCommand.NotifyCanExecuteChanged();
            return;
        }
        Rebuild();
    }
    private void Rebuild()
    {
        entries = WorkspacePlayerEntries.Build(session);
        hasSchedule = session.Workspace.Schedule is not null;
        if (!HasSchedule) selectedSortIndex = 0;
        foreach (var property in new[] { nameof(HasSchedule), nameof(HasNoSchedule), nameof(CanSortByRest),
            nameof(SortOptions), nameof(SelectedSortIndex) }) OnPropertyChanged(property);
        ApplySort(); OnPropertyChanged(nameof(WindowTitle)); OnPropertyChanged(nameof(IsStale));
    }
    private void ApplySort()
    {
        var identity = SelectedPlayer?.IdentityKey ?? selectedIdentity;
        var query = SearchText.Trim();
        var visibleEntries = entries.Where(entry => query.Length == 0 || entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            entry.StudentId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            entry.Registrations.Any(registration => registration.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                registration.StudentId.Contains(query, StringComparison.OrdinalIgnoreCase)));
        var ordered = SelectedSortIndex switch
        {
            1 when HasSchedule => visibleEntries.OrderBy(p => p.MinimumRestMinutes is null).ThenBy(p => p.MinimumRestMinutes),
            2 when HasSchedule => visibleEntries.OrderBy(p => p.MinimumRestMinutes is null).ThenByDescending(p => p.MinimumRestMinutes),
            _ when HasSchedule => visibleEntries.OrderByDescending(p => p.ConflictCount).ThenByDescending(p => p.RestWarningCount).ThenByDescending(p => p.ProjectCount),
            _ => visibleEntries.OrderByDescending(p => p.ProjectCount)
        };
        var nextPlayers = ordered.ThenBy(p => p.Name, StringComparer.CurrentCulture).ThenBy(p => p.IdentityKey, StringComparer.Ordinal)
            .Select(p => new PlayerEntryRowViewModel(p)).ToArray();
        var nextSelection = nextPlayers.FirstOrDefault(p => p.IdentityKey == identity) ?? nextPlayers.FirstOrDefault();
        // Clear the bound item before replacing its source. Otherwise the list's null feedback
        // can cache the replacement row before the final selection notification reaches it.
        selectedPlayer = null;
        OnPropertyChanged(nameof(SelectedPlayer));
        players = nextPlayers;
        OnPropertyChanged(nameof(Players));
        selectedPlayer = nextSelection;
        if (selectedPlayer is not null) selectedIdentity = selectedPlayer.IdentityKey;
        OnPropertyChanged(nameof(SelectedPlayer));
        OnPropertyChanged(nameof(Header)); OnPropertyChanged(nameof(HasPlayers)); OnPropertyChanged(nameof(HasNoPlayers));
        OnPropertyChanged(nameof(HasNoSearchResults));
        RefreshDetails();
    }
    private void RefreshDetails()
    {
        // Retained commands also consult this model's live session and disposal state.
        foreach (var old in confirmedMatches.Concat(potentialMatches)) old.FocusCommand.NotifyCanExecuteChanged();
        PlayerAppearanceViewModel Wrap(PlayerEntryAppearance appearance) => new(appearance,
            new DelegateCommand(() => focus(appearance.Key), () => CanFocus(appearance.Key)));
        confirmedMatches = SelectedPlayer?.Entry.ConfirmedMatches.Select(Wrap).ToArray() ?? [];
        potentialMatches = SelectedPlayer?.Entry.PotentialMatches.Select(Wrap).ToArray() ?? [];
        foreach (var property in new[] { nameof(HasSelection), nameof(DetailTitle), nameof(ProjectText), nameof(SelectedSummary),
            nameof(RestSummary), nameof(RiskSummary), nameof(ConfirmedMatches), nameof(PotentialMatches),
            nameof(HasNoConfirmedMatches), nameof(HasPotentialMatches), nameof(PotentialHeading), nameof(Registrations),
            nameof(IdentityWarningText), nameof(HasIdentityWarnings), nameof(RegistrationScopeText), nameof(HasRegistrationScopeText) }) OnPropertyChanged(property);
    }
    private bool CanFocus(WorkspaceMatchKey key) => !IsStale && session.Workspace.Schedule?.Placements.ContainsKey(key.MatchId) == true &&
        session.Workspace.Projects.Any(p => p.Id == key.ProjectId && p.MatchGraph?.Matches.Any(m => m.Id == key.MatchId) == true);
    public void Dispose()
    {
        disposed = true; OnPropertyChanged(nameof(IsStale));
        foreach (var match in confirmedMatches.Concat(potentialMatches)) match.FocusCommand.NotifyCanExecuteChanged();
    }
}

public sealed record PlayerEntryRowViewModel(PlayerEntrySummary Entry)
{
    public string IdentityKey => Entry.IdentityKey;
    public string DisplayName => Entry.DisplayName;
    public string ProjectText => string.Join("、", Entry.ProjectNames);
    public bool HasSchedule => Entry.HasSchedule;
    public string ShortSummary => HasSchedule ? $"{Entry.ProjectCount} 项 · 确定 {Entry.ConfirmedMatches.Count} 场 · 待比赛 {Entry.PendingCount} 场" : $"{Entry.ProjectCount} 个项目";
    public string RiskCounts => HasSchedule ? $"确定比赛：冲突 {Entry.ConflictCount} 对 · 休息不足 {Entry.RestWarningCount} 对" : "";
    public string RestSummary => !HasSchedule ? "" : Entry.MinimumRestMinutes switch
    {
        null => "同日最短排定间隔：暂无可比较的相邻比赛",
        < 0 => $"同日最短排定间隔：{Entry.MinimumRestMinutes:0.#} 分钟（存在重叠）",
        var minutes => $"同日最短排定间隔：{minutes:0.#} 分钟"
    };
}

public sealed record PlayerAppearanceViewModel(PlayerEntryAppearance Appearance, DelegateCommand FocusCommand)
{
    public string Title => Appearance.Position + " · " + Appearance.ProjectName;
    public string MatchSummary => Appearance.MatchName + " · " + Appearance.Status;
    public string Players => "本方：" + Appearance.OwnSide + "    对方：" + Appearance.Opponent;
    public string WarningText => string.Join("\n", Appearance.Warnings);
    public bool HasWarnings => Appearance.Warnings.Count > 0;
}
