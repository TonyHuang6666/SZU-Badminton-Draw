using System.Collections.Frozen;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Core.Scheduling;

internal sealed class GraphSchedulingCandidates
{
    private readonly SchedulingWorkBudget? budget;
    private void Charge(long units = 1)
    {
        if (budget is not null && !budget.TrySpend(SchedulingRunPhase.Context, units)) throw new SchedulingContextInterruptedException();
    }

    internal static bool TryCreate(TournamentSchedulingRequest request, SchedulingWorkBudget budget, out GraphSchedulingCandidates? context)
    {
        context = null;
        try { context = new(request, budget); return true; }
        catch (SchedulingContextInterruptedException) { return false; }
    }

    private readonly FrozenDictionary<Guid, FrozenSet<Guid>> playerConflicts = FrozenDictionary<Guid, FrozenSet<Guid>>.Empty;
    private readonly Dictionary<Guid, Dictionary<string, ConditionalPlayerPath[]>> pathsByPlayer = [];
    internal TournamentSchedulingRequest Request { get; private set; }
    internal TournamentSchedulingPolicy RequestedPolicy { get; }
    internal int MaxCacheEntries => budget?.MaxCacheEntries ?? TournamentSchedulingOptions.Default.MaxCacheEntries;
    internal void ObserveTimeCache(int count) => budget?.ObserveCache("StateTimeChecks", count);
    // Context paths are immutable during a run, so player identity plus the exact sorted
    // match set identifies every alternative group. Equality compares contents, not hashes.
    private sealed record CachedAppearance(bool? ExceedsCap, AppearanceBounds? Bounds);
    private readonly Dictionary<AppearanceCacheKey, CachedAppearance> appearanceChecks = new(new AppearanceCacheKeyComparer());
    private readonly Queue<AppearanceCacheKey> appearanceOrder = [];
    internal bool TryGetAppearanceBounds(AppearanceCacheKey key, out AppearanceBounds bounds)
    {
        bounds = null!;
        if (!appearanceChecks.TryGetValue(key, out var entry) || entry.Bounds is null) return false;
        bounds = entry.Bounds;
        return true;
    }
    internal void CacheAppearanceBounds(AppearanceCacheKey key, AppearanceBounds bounds)
    {
        if (MaxCacheEntries == 0) return;
        if (appearanceChecks.TryGetValue(key, out var entry))
        {
            if (entry.Bounds is { } prior && (prior.IsExact || bounds.LowerBound < prior.LowerBound || bounds.UpperBound > prior.UpperBound)) return;
            appearanceChecks[key] = entry with { Bounds = bounds };
            return;
        }
        AddAppearanceCache(key, new(null, bounds));
    }
    internal bool TryGetAppearanceCheck(AppearanceCacheKey key, out bool exceeds)
    {
        exceeds = false;
        if (!appearanceChecks.TryGetValue(key, out var entry) || entry.ExceedsCap is not { } cached) return false;
        exceeds = cached;
        return true;
    }
    internal void CacheAppearanceCheck(AppearanceCacheKey key, bool exceeds)
    {
        if (MaxCacheEntries == 0) return;
        if (appearanceChecks.TryGetValue(key, out var entry)) appearanceChecks[key] = entry with { ExceedsCap = exceeds };
        else AddAppearanceCache(key, new(exceeds, null));
    }
    private void AddAppearanceCache(AppearanceCacheKey key, CachedAppearance entry)
    {
        if (appearanceChecks.Count >= MaxCacheEntries) appearanceChecks.Remove(appearanceOrder.Dequeue());
        appearanceChecks.Add(key, entry);
        appearanceOrder.Enqueue(key);
        budget?.ObserveCache("AppearanceChecks", appearanceChecks.Count);
    }
    internal void ApplyEffectivePolicy()
    {
        Charge(SchedulingCapacityArithmetic.Add(1L + Nodes.Count + Days.Length * (long)Days.Length,
            SchedulingCapacityArithmetic.Multiply(Request.Policy.FinalDayRules.Count, Request.MatchGraphs.Count * 4L + 1)));
        foreach (var day in Days)
        {
            Charge(1L + (day.UnavailableCourtWindows?.Count ?? 0));
            Charge(SchedulingCapacityArithmetic.DayIntegrationWork(day));
        }
        Request = Request with { Policy = TournamentPlacementScorer.ResolveEffectivePolicy(this) };
    }
    internal Dictionary<Guid, MatchNode> Nodes { get; } = [];
    internal Dictionary<Guid, IReadOnlyList<ConditionalPlayerPath>> Paths { get; } = [];
    internal Dictionary<Guid, int> Durations { get; } = [];
    internal Dictionary<Guid, int> Depths { get; } = [];
    internal HashSet<Guid> LockedIds { get; } = [];
    internal List<SchedulingViolation> InputViolations { get; } = [];
    internal ScheduleDaySettings[] Days { get; }
    internal IReadOnlyDictionary<string, int> DayIndexes { get; }

    internal GraphSchedulingCandidates(TournamentSchedulingRequest request) : this(request, null) { }

    private GraphSchedulingCandidates(TournamentSchedulingRequest request, SchedulingWorkBudget? budget)
    {
        this.budget = budget;
        Charge(1L + request.Resources.Days.Count * (long)request.Resources.Days.Count);
        Request = request;
        RequestedPolicy = request.Policy;
        Days = request.Resources.Days.OrderBy(d => d.Date).ToArray();
        var dayIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        // Keep duplicate dates in Days for typed input validation below, rather than
        // throwing while preparing the immutable request's repeated day lookups.
        for (var i = 0; i < Days.Length; i++)
            dayIndexes.TryAdd(Days[i].DayLabel, i);
        DayIndexes = dayIndexes;
        void Issue(SchedulingConstraintCode code, string message, MatchNode? n = null, Guid? related = null) =>
            InputViolations.Add(new(code, n?.ProjectId, n?.Id, related, message));
        Charge(request.MatchGraphs.Count);
        var projects = request.MatchGraphs.Select(g => g.ProjectId).ToHashSet();
        if (request.MatchGraphs.Count == 0 || projects.Count != request.MatchGraphs.Count || projects.Contains(Guid.Empty))
            Issue(SchedulingConstraintCode.InvalidGraph, "项目图不能为空或重复。");
        foreach (var graph in request.MatchGraphs)
        {
            Charge(1L + graph.Matches.Count);
            if (string.IsNullOrWhiteSpace(graph.Revision) || graph.Matches.Count == 0 || graph.Matches.Select(n => n.OriginalMatchId).Distinct().Count() != graph.Matches.Count)
                Issue(SchedulingConstraintCode.InvalidGraph, "项目图必须包含版本和可排场次。");
            foreach (var node in graph.Matches)
            {
                Charge(1L + node.Dependencies.Count);
                if (!Nodes.TryAdd(node.Id, node) || node.Id == Guid.Empty || node.ProjectId != graph.ProjectId || !node.IsPlayable || node.ExpectedDurationMinutes is <= 0 or >= 1440 ||
                    string.IsNullOrWhiteSpace(node.OriginalMatchId) || string.IsNullOrWhiteSpace(node.DisplayName) || string.IsNullOrWhiteSpace(node.Phase) || node.Order <= 0 || node.GroupNumber < 0)
                    Issue(SchedulingConstraintCode.InvalidGraph, "比赛身份、项目、参赛来源或时长无效。", node);
                foreach (var side in new[] { node.SideA, node.SideB })
                {
                    Charge(1L + (side is EntrantSource.Participant participant ? participant.Players.Count * 2L : 0));
                    if (side is EntrantSource.Participant entrant ?
                        string.IsNullOrWhiteSpace(entrant.IdentityKey) || string.IsNullOrWhiteSpace(entrant.DisplayName) || entrant.Players.Count == 0 ||
                        entrant.Players.Any(p => string.IsNullOrWhiteSpace(p.Name)) || entrant.Players.Select(p => p.IdentityKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entrant.Players.Count :
                        side is not EntrantSource.WinnerOf and not EntrantSource.LoserOf)
                        Issue(SchedulingConstraintCode.InvalidGraph, "参赛来源必须保留完整有效的选手身份。", node);
                }
                var sources = new[] { node.SideA, node.SideB }.Select(SourceId).OfType<Guid>().ToHashSet();
                if (!sources.SetEquals(node.Dependencies) || node.Dependencies.Count != node.Dependencies.Distinct().Count())
                    Issue(SchedulingConstraintCode.InvalidGraph, "依赖列表必须与显式参赛来源完全一致。", node);
            }
        }
        foreach (var node in Nodes.Values)
        foreach (var dependency in node.Dependencies)
        {
            Charge();
            if (!Nodes.TryGetValue(dependency, out var source)) Issue(SchedulingConstraintCode.MissingDependency, "比赛来源不存在。", node, dependency);
            else if (source.ProjectId != node.ProjectId) Issue(SchedulingConstraintCode.CrossProjectDependency, "晋级来源不能跨项目。", node, dependency);
        }
        Charge(1L + Nodes.Count);
        var remaining = Nodes.Keys.ToHashSet();
        while (remaining.Count > 0)
        {
            Charge(1L + remaining.Count * 4L);
            foreach (var id in remaining) Charge(Nodes[id].Dependencies.Count * 2L);
            var ready = remaining.Where(id => Nodes[id].Dependencies.All(Depths.ContainsKey)).ToArray();
            if (ready.Length == 0) break;
            foreach (var id in ready) { Depths[id] = Nodes[id].Dependencies.Select(d => Depths[d] + 1).DefaultIfEmpty(0).Max(); remaining.Remove(id); }
        }
        if (remaining.Count > 0 && !InputViolations.Any(v => v.Code == SchedulingConstraintCode.MissingDependency))
            foreach (var id in remaining) Issue(SchedulingConstraintCode.CyclicDependency, "比赛依赖包含循环或受循环阻塞。", Nodes[id]);

        if (Days.Length == 0 || Days.Select(d => d.Date).Distinct().Count() != Days.Length || request.Resources.RefereeCount is <= 0 ||
            request.Resources.MinimumRestMinutes < 0 || request.Resources.MaxPlayerMatchesPerDay <= 0)
            Issue(SchedulingConstraintCode.InvalidResources, "资源日期、裁判人数、休息或每日上限无效。");
        foreach (var day in Days)
        {
            Charge(1L + day.Courts.Count * 3L);
            foreach (var block in day.UnavailableCourtWindows ?? []) Charge(1L + block.Courts.Count * (long)Math.Max(1, day.Courts.Count));
            Charge((day.RefereeCapacityWindows?.Count ?? 0) + (long)(day.UnavailableCourtWindows?.Count ?? 0));
            if (day.DayEnd <= day.DayStart || day.Courts.Count == 0 || day.Courts.Any(string.IsNullOrWhiteSpace) || day.Courts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != day.Courts.Count)
                Issue(SchedulingConstraintCode.InvalidResources, "每天必须有有效的时间范围和不重复的场地。");
            if ((day.RefereeCapacityWindows ?? []).Any(w => w.StartTime >= w.EndTime || w.StartTime < day.DayStart || w.EndTime > day.DayEnd || w.RefereeCount < 0) ||
                (day.UnavailableCourtWindows ?? []).Any(w => w.StartTime >= w.EndTime || w.StartTime < day.DayStart || w.EndTime > day.DayEnd || w.Courts.Any(c => !day.Courts.Contains(c, StringComparer.OrdinalIgnoreCase))))
                Issue(SchedulingConstraintCode.InvalidResources, "裁判时段或场地不可用时段无效。");
        }
        var policy = request.Policy;
        Charge(1L + request.ProjectNames.Count + policy.ProjectTimings.Count + policy.DayLoadTargets.Count * 3L +
            policy.StageWaveTargets.Count * (Days.Length + 2L) + policy.FinalDayRules.Count * 3L + Days.Length * 3L);
        var labels = Days.Select(d => d.DayLabel).ToHashSet();
        if (!Enum.IsDefined(policy.Strategy) || request.ScheduleRevision < 0 || request.ProjectNames.Keys.Any(id => !projects.Contains(id)) ||
            policy.ProjectTimings.Any(p => !projects.Contains(p.Key) || p.Value.MatchMinutes is <= 0 or >= 1440 ||
                p.Value.KnockoutTimingBoundaryEntrants is < 2 || p.Value.BeforeBoundaryMinutes is <= 0 or >= 1440 ||
                p.Value.KnockoutTimingBoundaryEntrants.HasValue != p.Value.BeforeBoundaryMinutes.HasValue) ||
            policy.DayLoadTargets.Any(t => !labels.Contains(t.DayLabel) || !Ratio(t.TargetUtilization) || !Ratio(t.WarningUtilization) || t.WarningUtilization < t.TargetUtilization) ||
            policy.DayLoadTargets.Select(t => t.DayLabel).Distinct().Count() != policy.DayLoadTargets.Count ||
            policy.StageWaveTargets.Any(t => !labels.Contains(t.DayLabel) || !Ratio(t.CumulativeProgress)) ||
            policy.StageWaveTargets.Select(t => t.DayLabel).Distinct().Count() != policy.StageWaveTargets.Count ||
            policy.FinalDayRules.Any(r => !projects.Contains(r.ProjectId) || !Enum.IsDefined(r.Category) || !Enum.IsDefined(r.Preference)) ||
            policy.FinalDayRules.Select(r => (r.ProjectId, r.Category)).Distinct().Count() != policy.FinalDayRules.Count)
            Issue(SchedulingConstraintCode.InvalidPolicy, "策略参数、项目时长或规则引用无效。");
        var progress = Days.Select(d => policy.StageWaveTargets.FirstOrDefault(t => t.DayLabel == d.DayLabel)?.CumulativeProgress).OfType<double>().ToArray();
        if (!progress.SequenceEqual(progress.Order())) Issue(SchedulingConstraintCode.InvalidPolicy, "阶段累计进度不能逐日下降。");

        Charge(request.LockedMatchIds.Count + (long)request.Results.Count);
        LockedIds.UnionWith(request.LockedMatchIds);
        LockedIds.UnionWith(request.Results.Keys.Select(k => k.MatchId));
        foreach (var id in LockedIds)
        {
            Charge();
            if (!Nodes.ContainsKey(id) || request.BaselinePlacements is null || !request.BaselinePlacements.ContainsKey(id))
                Issue(SchedulingConstraintCode.LockedPlacement, "已完成或锁定场次必须有真实的原始位置。", Nodes.GetValueOrDefault(id), id);
        }
        if (request.BaselinePlacements is not null)
            foreach (var pair in request.BaselinePlacements)
            {
                Charge();
                if (!Nodes.ContainsKey(pair.Key) || pair.Value.MatchId != pair.Key)
                    Issue(SchedulingConstraintCode.PlacementIdentity, "原始位置引用了未知或不一致的比赛身份。", related: pair.Key);
            }
        foreach (var pair in request.Results)
        {
            Charge(SchedulingCapacityArithmetic.Add(1L + Days.Length + pair.Value.Winner.Players.Count * (long)pair.Value.Winner.Players.Count,
                pair.Value.Loser.Players.Count * (long)pair.Value.Loser.Players.Count));
            if (!Nodes.TryGetValue(pair.Key.MatchId, out var node) || node.ProjectId != pair.Key.ProjectId || pair.Value.Key != pair.Key ||
                pair.Value.Winner.IdentityKey == pair.Value.Loser.IdentityKey ||
                !TournamentResultRules.IsValidValue(pair.Value, Days.Select(d => d.Date)))
            {
                Issue(SchedulingConstraintCode.InvalidResult, "赛果项目、比赛或胜负双方身份无效。", related: pair.Key.MatchId);
                continue;
            }
            EntrantSource.Participant? Resolve(EntrantSource side)
            {
                if (side is EntrantSource.Participant participant) return participant;
                return SourceId(side) is { } id && request.Results.TryGetValue(new(node.ProjectId, id), out var sourceResult)
                    ? side is EntrantSource.WinnerOf ? sourceResult.Winner : sourceResult.Loser : null;
            }
            var a = Resolve(node.SideA); var b = Resolve(node.SideB);
            if (a is null || b is null || !((SameEntrant(pair.Value.Winner, a) && SameEntrant(pair.Value.Loser, b)) ||
                (SameEntrant(pair.Value.Winner, b) && SameEntrant(pair.Value.Loser, a))))
                Issue(SchedulingConstraintCode.InvalidResult, "赛果参赛方与来源身份不符，或缺少上游赛果。", node);
        }
        if (InputViolations.Count != 0) return;
        var resolver = new ConditionalPlayerPaths(Nodes, request.Results, budget);
        Charge(Nodes.Count * (long)Nodes.Count + 1);
        foreach (var node in Nodes.Values.OrderBy(n => Depths[n.Id]))
        {
            Charge();
            var paths = resolver.Resolve(node);
            // Compare the sides before using their union for cross-match conflicts and load.
            // Winner/loser alternatives are allowed only when their same-player paths cannot coexist.
            if (SharesIndexedPlayer(paths.SideA, paths.SideB))
                Issue(SchedulingConstraintCode.PlayerOnBothSides, "比赛双方包含同一选手的兼容参赛路径。", node);
            Paths[node.Id] = paths.All;
            Durations[node.Id] = ScheduleTimingResolver.Resolve(node, policy);
        }
        if (InputViolations.Count != 0) return;
        // Compatibility depends only on this captured graph/result snapshot, not a
        // candidate's time or court. Freeze it before publishing the validator so
        // concurrent readers never populate a mutable, or cross-request, cache.
        Charge(Nodes.Count * 2L);
        var ids = Nodes.Keys.ToArray();
        var conflicts = ids.ToDictionary(id => id, _ => new HashSet<Guid>());
        // Group once per immutable match. Comparing unrelated players across every
        // pair of matches repeats the same identity rejection quadratically.
        foreach (var id in ids)
        {
            Charge(1L + Paths[id].Count);
            foreach (var path in Paths[id]) Charge(1L + path.PlayerKey.Length);
            pathsByPlayer[id] = Paths[id].GroupBy(path => path.PlayerKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        }
        long conflictEntries = 0;
        for (var i = 0; i < ids.Length; i++)
        for (var j = i + 1; j < ids.Length; j++)
        {
            Charge();
            var shared = false;
            foreach (var player in pathsByPlayer[ids[i]])
            {
                Charge(1L + player.Key.Length);
                if (!pathsByPlayer[ids[j]].TryGetValue(player.Key, out var other)) continue;
                if (!ConditionalPlayerPaths.SharesPlayer(player.Value, other, budget)) continue;
                shared = true;
                break;
            }
            if (!shared) continue;
            conflicts[ids[i]].Add(ids[j]);
            conflicts[ids[j]].Add(ids[i]);
            conflictEntries += 2;
        }
        Charge(Nodes.Count + conflictEntries);
        playerConflicts = conflicts.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToFrozenSet());
    }

    internal bool ShareCompatiblePlayer(Guid first, Guid second) => playerConflicts[first].Contains(second);
    // Built once from the captured paths in constructor order and never changed after publication.
    internal IReadOnlyCollection<string> PlayerKeys(Guid matchId) => pathsByPlayer[matchId].Keys;
    internal IReadOnlyList<ConditionalPlayerPath> PlayerPaths(Guid matchId, string player) =>
        pathsByPlayer[matchId].GetValueOrDefault(player) ?? [];
    private bool SharesIndexedPlayer(IReadOnlyList<ConditionalPlayerPath> a, IReadOnlyList<ConditionalPlayerPath> b)
    {
        Charge(1L + a.Count + b.Count);
        foreach (var path in b) Charge(1L + path.PlayerKey.Length);
        var other = b.ToLookup(path => path.PlayerKey, StringComparer.OrdinalIgnoreCase);
        foreach (var path in a)
        {
            Charge(1L + path.PlayerKey.Length);
            foreach (var candidate in other[path.PlayerKey])
            {
                Charge(1L + path.Conditions.Count);
                if (ConditionalPlayerPaths.Compatible(path.Conditions, candidate.Conditions)) return true;
            }
        }
        return false;
    }
    internal int PlayerConflictDegree(Guid matchId) => playerConflicts[matchId].Count;

    private static bool Ratio(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    private static bool SameEntrant(EntrantSource.Participant a, EntrantSource.Participant b) =>
        a.IdentityKey == b.IdentityKey && a.Players.Select(p => p.IdentityKey).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(b.Players.Select(p => p.IdentityKey).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    private static Guid? SourceId(EntrantSource source) => source switch { EntrantSource.WinnerOf w => w.MatchId, EntrantSource.LoserOf l => l.MatchId, _ => null };
    internal double Minute(MatchPlacement p, bool end = false)
    {
        var day = Days[DayIndexes[p.DayLabel]];
        return (long)day.Date.DayNumber * 1440 + (end ? p.EndTime : p.StartTime).ToTimeSpan().TotalMinutes;
    }
}

internal sealed class SchedulingContextInterruptedException : Exception;

internal sealed record AppearanceCacheKey(string PlayerKey, Guid[] MatchIds);
internal sealed class AppearanceCacheKeyComparer : IEqualityComparer<AppearanceCacheKey>
{
    public bool Equals(AppearanceCacheKey? x, AppearanceCacheKey? y) => ReferenceEquals(x, y) ||
        x is not null && y is not null && StringComparer.OrdinalIgnoreCase.Equals(x.PlayerKey, y.PlayerKey) &&
        x.MatchIds.AsSpan().SequenceEqual(y.MatchIds);
    public int GetHashCode(AppearanceCacheKey key)
    {
        var hash = new HashCode();
        hash.Add(key.PlayerKey, StringComparer.OrdinalIgnoreCase);
        foreach (var id in key.MatchIds) hash.Add(id);
        return hash.ToHashCode();
    }
}
