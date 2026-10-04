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
    public string Header => $"兼项选手 {Players.Count} 人 · 按报名身份汇总";
    public IReadOnlyList<string> SortOptions { get; } = ["默认排序", "休息时间从短到长", "休息时间从长到短"];
    public IReadOnlyList<PlayerEntryRowViewModel> Players => players;
    public bool HasPlayers => Players.Count > 0;
    public bool HasNoPlayers => !HasPlayers;
    public bool HasSelection => SelectedPlayer is not null;
    public bool IsStale => disposed || session.RequiresReload;
    public int SelectedSortIndex
    {
        get => selectedSortIndex;
        set { if (SetProperty(ref selectedSortIndex, Math.Clamp(value, 0, 2))) ApplySort(); }
    }
    public PlayerEntryRowViewModel? SelectedPlayer
    {
        get => selectedPlayer;
        set
        {
            // Replacing the list can briefly clear the bound selection; preserve its stable identity.
            if (value is null && Players.Count > 0) return;
            if (SetProperty(ref selectedPlayer, value)) RefreshDetails();
        }
    }
    public string DetailTitle => SelectedPlayer?.DisplayName ?? "选择一位选手";
    public string ProjectText => SelectedPlayer?.ProjectText ?? "";
    public string SelectedSummary => SelectedPlayer?.Entry.Summary ?? "";
    public string RestSummary => SelectedPlayer?.RestSummary ?? "";
    public string RiskSummary => SelectedPlayer?.Entry.RiskSummary ?? "";
    public IReadOnlyList<PlayerAppearanceViewModel> ConfirmedMatches => confirmedMatches;
    public IReadOnlyList<PlayerAppearanceViewModel> PotentialMatches => potentialMatches;
    public bool HasNoConfirmedMatches => confirmedMatches.Count == 0;
    public bool HasPotentialMatches => potentialMatches.Count > 0;
    public string PotentialHeading => $"后续可能参加 · {PotentialMatches.Count} 场（结果未定）";

    public void RefreshSession(WorkspaceSession next)
    {
        if (disposed) return;
        if (session.Workspace.Id != next.Workspace.Id || session.WorkspacePath != next.WorkspacePath)
        {
            Dispose(); entries = []; ApplySort(); return;
        }
        session = next; Rebuild();
    }
    private void Rebuild()
    {
        entries = WorkspacePlayerEntries.Build(session);
        ApplySort(); OnPropertyChanged(nameof(WindowTitle)); OnPropertyChanged(nameof(IsStale));
    }
    private void ApplySort()
    {
        var identity = SelectedPlayer?.IdentityKey;
        var ordered = SelectedSortIndex switch
        {
            1 => entries.OrderBy(p => p.MinimumRestMinutes is null).ThenBy(p => p.MinimumRestMinutes),
            2 => entries.OrderBy(p => p.MinimumRestMinutes is null).ThenByDescending(p => p.MinimumRestMinutes),
            _ => entries.OrderByDescending(p => p.ConflictCount).ThenByDescending(p => p.RestWarningCount).ThenByDescending(p => p.ProjectCount)
        };
        players = ordered.ThenBy(p => p.Name, StringComparer.CurrentCulture).ThenBy(p => p.IdentityKey, StringComparer.Ordinal)
            .Select(p => new PlayerEntryRowViewModel(p)).ToArray();
        selectedPlayer = Players.FirstOrDefault(p => p.IdentityKey == identity) ?? Players.FirstOrDefault();
        OnPropertyChanged(nameof(Players)); OnPropertyChanged(nameof(SelectedPlayer));
        OnPropertyChanged(nameof(Header)); OnPropertyChanged(nameof(HasPlayers)); OnPropertyChanged(nameof(HasNoPlayers));
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
            nameof(HasNoConfirmedMatches), nameof(HasPotentialMatches), nameof(PotentialHeading) }) OnPropertyChanged(property);
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
    public string ShortSummary => $"{Entry.ProjectCount} 项 · 确定 {Entry.ConfirmedMatches.Count} 场 · 待比赛 {Entry.PendingCount} 场";
    public string RiskCounts => $"确定比赛：冲突 {Entry.ConflictCount} 对 · 休息不足 {Entry.RestWarningCount} 对";
    public string RestSummary => Entry.MinimumRestMinutes switch
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
