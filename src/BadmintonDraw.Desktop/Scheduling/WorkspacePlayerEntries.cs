using System.Globalization;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.Scheduling;

public sealed record PlayerEntryAppearance(WorkspaceMatchKey Key, string ProjectName, string MatchName,
    string Phase, string Position, string Sides, string OwnSide, string Opponent, MatchPlacement Placement,
    bool IsPotential, bool IsCompleted, int ConflictCount, int RestWarningCount, IReadOnlyList<string> Warnings)
{
    public string Status => IsPotential ? "后续可能参加（结果未定）" : IsCompleted ? "已完成" : "待比赛";
}

public sealed record PlayerEntrySummary(string IdentityKey, string Name, string StudentId, string DisplayName,
    IReadOnlyList<string> ProjectNames, IReadOnlyList<PlayerEntryAppearance> ConfirmedMatches,
    IReadOnlyList<PlayerEntryAppearance> PotentialMatches, int ConflictCount, int RestWarningCount,
    double? MinimumRestMinutes)
{
    public int ProjectCount => ProjectNames.Count;
    public int CompletedCount => ConfirmedMatches.Count(match => match.IsCompleted);
    public int PendingCount => ConfirmedMatches.Count(match => !match.IsCompleted);
    public bool HasPotentialMatches => PotentialMatches.Count > 0;
    public string Summary => $"{ProjectCount} 项 · 确定 {ConfirmedMatches.Count} 场 · 已完成 {CompletedCount} 场 · 待比赛 {PendingCount} 场 · 潜在 {PotentialMatches.Count} 场";
    public string RiskSummary => $"确定比赛：冲突 {ConflictCount} 对 · 休息不足 {RestWarningCount} 对" +
        (HasPotentialMatches ? "；潜在比赛须待赛果确定后核验冲突与休息，尚不能判定无风险。" : "") +
        (IdentityKey.StartsWith("team:", StringComparison.Ordinal) ? "；团体按队伍身份展示，未包含队员个人兼项信息。" : "");
}

/// <summary>A read-only roster and current graph-outcome projection, separate from scheduling decisions.</summary>
public static class WorkspacePlayerEntries
{
    public static IReadOnlyList<PlayerEntrySummary> Build(WorkspaceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var workspace = session.Workspace;
        var schedule = workspace.Schedule ?? throw new WorkspaceCommandException(new("schedule.missing", "尚未生成赛程。"));
        var projects = workspace.Projects.OrderBy(p => p.SortOrder).ToArray();
        var registrations = new Dictionary<string, Registration>(StringComparer.Ordinal);
        foreach (var project in projects)
        foreach (var participant in project.Roster?.Participants ?? [])
        foreach (var player in ProjectEntrantIdentity.Create(project.Discipline, participant).Players)
        {
            if (!registrations.TryGetValue(player.IdentityKey, out var registration))
                registrations.Add(player.IdentityKey, registration = new(player));
            registration.Projects.TryAdd(project.Id, project.DisplayName);
        }

        var graphs = projects.Select(p => p.MatchGraph ?? throw new WorkspaceValidationException("graph.missing", "比赛关系图尚未生成。")).ToArray();
        // Reuse display/validation only. Projection player collections contain unresolved candidates.
        var display = ScheduledMatchProjection.Build(graphs, schedule, workspace.Results).ToDictionary(m => Guid.Parse(m.MatchId));
        var resolvedSides = new Dictionary<Guid, (Resolved A, Resolved B)>();
        Resolved Resolve(Guid projectId, EntrantSource source)
        {
            if (source is EntrantSource.Participant participant) return new(participant.Players, true);
            var parentId = source switch
            {
                EntrantSource.WinnerOf winner => winner.MatchId,
                EntrantSource.LoserOf loser => loser.MatchId,
                _ => throw new WorkspaceValidationException("graph.bye", "比赛关系图不能包含轮空场次。")
            };
            if (workspace.Results.TryGetValue(new(projectId, parentId), out var result))
                return new((source is EntrantSource.WinnerOf ? result.Winner : result.Loser).Players, true);
            var parent = resolvedSides[parentId];
            return new(parent.A.Players.Concat(parent.B.Players).DistinctBy(player => player.IdentityKey, StringComparer.Ordinal).ToArray(), false);
        }

        foreach (var project in projects)
        foreach (var node in project.MatchGraph!.Matches.OrderBy(m => m.Order))
        {
            var a = Resolve(project.Id, node.SideA);
            var b = Resolve(project.Id, node.SideB);
            resolvedSides.Add(node.Id, (a, b));
            var shown = display[node.Id];
            var key = new WorkspaceMatchKey(project.Id, node.Id);
            var placement = schedule.Placements[node.Id];
            // One player can be a candidate on both sides of a later match. Show the match once.
            foreach (var player in a.Players.Concat(b.Players).DistinctBy(p => p.IdentityKey, StringComparer.Ordinal))
            {
                if (!registrations.TryGetValue(player.IdentityKey, out var registration) || registration.Projects.Count < 2) continue;
                var onA = a.Players.Any(p => p.IdentityKey == player.IdentityKey);
                var onB = b.Players.Any(p => p.IdentityKey == player.IdentityKey);
                var certainA = onA && a.IsCertain;
                var certainB = onB && b.IsCertain;
                var potential = !certainA && !certainB;
                var ownSide = certainA || (onA && !onB) ? shown.SideA : certainB || onB && !onA ? shown.SideB : shown.SideA + " / " + shown.SideB;
                var opponent = certainA || (onA && !onB) ? shown.SideB : certainB || onB && !onA ? shown.SideA : "对阵双方均待结果确定";
                var warnings = potential ? new[] { "参赛身份尚未确定；须待赛果确定后核验冲突与休息。" } : Array.Empty<string>();
                var appearance = new PlayerEntryAppearance(key, project.DisplayName, node.DisplayName, node.Phase,
                    $"{placement.DayLabel} {WorkspaceBoardTime.Format(placement.StartTime)}–{WorkspaceBoardTime.Format(placement.EndTime)} · {placement.Court}",
                    shown.SideA + "\nVS\n" + shown.SideB, ownSide, opponent, placement, potential,
                    workspace.Results.ContainsKey(key), 0, 0, Array.AsReadOnly(warnings));
                (potential ? registration.Potential : registration.Confirmed).Add(appearance);
            }
        }

        var dates = schedule.Resources.Days.ToDictionary(day => day.DayLabel, day => day.Date, StringComparer.Ordinal);
        PlayerEntryAppearance[] Ordered(IEnumerable<PlayerEntryAppearance> matches) => matches
            .OrderBy(match => dates[match.Placement.DayLabel]).ThenBy(match => match.Placement.StartTime)
            .ThenBy(match => match.ProjectName, StringComparer.Ordinal).ThenBy(match => match.MatchName, StringComparer.Ordinal).ThenBy(match => match.Key.MatchId).ToArray();
        var summaries = new List<PlayerEntrySummary>();
        foreach (var registration in registrations.Values.Where(r => r.Projects.Count >= 2))
        {
            var confirmed = Ordered(registration.Confirmed);
            var conflicts = new int[confirmed.Length];
            var restWarnings = new int[confirmed.Length];
            var warnings = confirmed.Select(_ => new List<string>()).ToArray();
            var conflictCount = 0;
            var restWarningCount = 0;
            double? minimumRest = null;
            for (var i = 0; i < confirmed.Length; i++)
            {
                var current = confirmed[i];
                var currentStart = dates[current.Placement.DayLabel].ToDateTime(current.Placement.StartTime);
                var currentEnd = dates[current.Placement.DayLabel].ToDateTime(current.Placement.EndTime);
                // Count each confirmed pair once, including non-adjacent matches and date boundaries.
                for (var j = i + 1; j < confirmed.Length; j++)
                {
                    var other = confirmed[j];
                    var otherStart = dates[other.Placement.DayLabel].ToDateTime(other.Placement.StartTime);
                    var otherEnd = dates[other.Placement.DayLabel].ToDateTime(other.Placement.EndTime);
                    if (currentStart < otherEnd && otherStart < currentEnd)
                    {
                        conflictCount++; conflicts[i]++; conflicts[j]++;
                        warnings[i].Add($"与 {other.ProjectName} · {other.MatchName} 时间重叠。");
                        warnings[j].Add($"与 {current.ProjectName} · {current.MatchName} 时间重叠。");
                        continue;
                    }
                    var gap = (otherStart - currentEnd).TotalMinutes;
                    if (gap < 0 || gap >= schedule.Resources.MinimumRestMinutes) continue;
                    restWarningCount++; restWarnings[i]++; restWarnings[j]++;
                    var gapText = gap.ToString("0.#######", CultureInfo.InvariantCulture);
                    warnings[i].Add($"与后场 {other.ProjectName} · {other.MatchName} 排定间隔 {gapText} 分钟，不足 {schedule.Resources.MinimumRestMinutes} 分钟。");
                    warnings[j].Add($"与前场 {current.ProjectName} · {current.MatchName} 排定间隔 {gapText} 分钟，不足 {schedule.Resources.MinimumRestMinutes} 分钟。");
                }
                if (i == 0 || confirmed[i - 1].Placement.DayLabel != current.Placement.DayLabel) continue;
                var previous = confirmed[i - 1];
                // TimeOnly subtraction wraps negative values to the next day; these matches share a date.
                var interval = (current.Placement.StartTime.ToTimeSpan() - previous.Placement.EndTime.ToTimeSpan()).TotalMinutes;
                minimumRest = minimumRest is null ? interval : Math.Min(minimumRest.Value, interval);
            }
            for (var i = 0; i < confirmed.Length; i++)
                confirmed[i] = confirmed[i] with { ConflictCount = conflicts[i], RestWarningCount = restWarnings[i], Warnings = warnings[i].AsReadOnly() };
            var player = registration.Player;
            summaries.Add(new(player.IdentityKey, player.Name.Trim(), player.StudentId.Trim(), player.DisplayName,
                Array.AsReadOnly(registration.Projects.Values.ToArray()), Array.AsReadOnly(confirmed),
                Array.AsReadOnly(Ordered(registration.Potential)), conflictCount, restWarningCount, minimumRest));
        }
        return Array.AsReadOnly(summaries.OrderByDescending(p => p.ProjectCount).ThenBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.IdentityKey, StringComparer.Ordinal).ToArray());
    }

    private sealed record Resolved(IReadOnlyList<CrossEventPlayerIdentity> Players, bool IsCertain);
    private sealed class Registration(CrossEventPlayerIdentity player)
    {
        public CrossEventPlayerIdentity Player { get; } = player;
        public Dictionary<Guid, string> Projects { get; } = [];
        public List<PlayerEntryAppearance> Confirmed { get; } = [];
        public List<PlayerEntryAppearance> Potential { get; } = [];
    }
}
