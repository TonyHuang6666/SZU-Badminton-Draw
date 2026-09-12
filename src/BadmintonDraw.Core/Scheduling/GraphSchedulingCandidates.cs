using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Core.Scheduling;

internal sealed class GraphSchedulingCandidates
{
    internal TournamentSchedulingRequest Request { get; }
    internal Dictionary<Guid, MatchNode> Nodes { get; } = [];
    internal Dictionary<Guid, IReadOnlyList<ConditionalPlayerPath>> Paths { get; } = [];
    internal Dictionary<Guid, int> Durations { get; } = [];
    internal Dictionary<Guid, int> Depths { get; } = [];
    internal HashSet<Guid> LockedIds { get; } = [];
    internal List<SchedulingViolation> InputViolations { get; } = [];
    internal ScheduleDaySettings[] Days { get; }

    internal GraphSchedulingCandidates(TournamentSchedulingRequest request)
    {
        Request = request;
        Days = request.Resources.Days.OrderBy(d => d.Date).ToArray();
        void Issue(SchedulingConstraintCode code, string message, MatchNode? n = null, Guid? related = null) =>
            InputViolations.Add(new(code, n?.ProjectId, n?.Id, related, message));
        var projects = request.MatchGraphs.Select(g => g.ProjectId).ToHashSet();
        if (request.MatchGraphs.Count == 0 || projects.Count != request.MatchGraphs.Count || projects.Contains(Guid.Empty))
            Issue(SchedulingConstraintCode.InvalidGraph, "项目图不能为空或重复。");
        foreach (var graph in request.MatchGraphs)
        {
            if (string.IsNullOrWhiteSpace(graph.Revision) || graph.Matches.Count == 0 || graph.Matches.Select(n => n.OriginalMatchId).Distinct().Count() != graph.Matches.Count)
                Issue(SchedulingConstraintCode.InvalidGraph, "项目图必须包含版本和可排场次。");
            foreach (var node in graph.Matches)
            {
                if (!Nodes.TryAdd(node.Id, node) || node.Id == Guid.Empty || node.ProjectId != graph.ProjectId || !node.IsPlayable || node.ExpectedDurationMinutes is <= 0 or >= 1440 ||
                    string.IsNullOrWhiteSpace(node.OriginalMatchId) || string.IsNullOrWhiteSpace(node.DisplayName) || string.IsNullOrWhiteSpace(node.Phase) || node.Order <= 0 || node.GroupNumber < 0)
                    Issue(SchedulingConstraintCode.InvalidGraph, "比赛身份、项目、参赛来源或时长无效。", node);
                foreach (var side in new[] { node.SideA, node.SideB })
                    if (side is EntrantSource.Participant entrant ?
                        string.IsNullOrWhiteSpace(entrant.IdentityKey) || string.IsNullOrWhiteSpace(entrant.DisplayName) || entrant.Players.Count == 0 ||
                        entrant.Players.Any(p => string.IsNullOrWhiteSpace(p.Name)) || entrant.Players.Select(p => p.IdentityKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entrant.Players.Count :
                        side is not EntrantSource.WinnerOf and not EntrantSource.LoserOf)
                        Issue(SchedulingConstraintCode.InvalidGraph, "参赛来源必须保留完整有效的选手身份。", node);
                var sources = new[] { node.SideA, node.SideB }.Select(SourceId).OfType<Guid>().ToHashSet();
                if (!sources.SetEquals(node.Dependencies) || node.Dependencies.Count != node.Dependencies.Distinct().Count())
                    Issue(SchedulingConstraintCode.InvalidGraph, "依赖列表必须与显式参赛来源完全一致。", node);
            }
        }
        foreach (var node in Nodes.Values)
        foreach (var dependency in node.Dependencies)
        {
            if (!Nodes.TryGetValue(dependency, out var source)) Issue(SchedulingConstraintCode.MissingDependency, "比赛来源不存在。", node, dependency);
            else if (source.ProjectId != node.ProjectId) Issue(SchedulingConstraintCode.CrossProjectDependency, "晋级来源不能跨项目。", node, dependency);
        }
        var remaining = Nodes.Keys.ToHashSet();
        while (remaining.Count > 0)
        {
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
            if (day.DayEnd <= day.DayStart || day.Courts.Count == 0 || day.Courts.Any(string.IsNullOrWhiteSpace) || day.Courts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != day.Courts.Count)
                Issue(SchedulingConstraintCode.InvalidResources, "每天必须有有效的时间范围和不重复的场地。");
            if ((day.RefereeCapacityWindows ?? []).Any(w => w.StartTime >= w.EndTime || w.StartTime < day.DayStart || w.EndTime > day.DayEnd || w.RefereeCount < 0) ||
                (day.UnavailableCourtWindows ?? []).Any(w => w.StartTime >= w.EndTime || w.StartTime < day.DayStart || w.EndTime > day.DayEnd || w.Courts.Any(c => !day.Courts.Contains(c, StringComparer.OrdinalIgnoreCase))))
                Issue(SchedulingConstraintCode.InvalidResources, "裁判时段或场地不可用时段无效。");
        }
        var policy = request.Policy;
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

        LockedIds.UnionWith(request.LockedMatchIds);
        LockedIds.UnionWith(request.Results.Keys.Select(k => k.MatchId));
        foreach (var id in LockedIds)
            if (!Nodes.ContainsKey(id) || request.BaselinePlacements is null || !request.BaselinePlacements.ContainsKey(id))
                Issue(SchedulingConstraintCode.LockedPlacement, "已完成或锁定场次必须有真实的原始位置。", Nodes.GetValueOrDefault(id), id);
        if (request.BaselinePlacements is not null)
            foreach (var pair in request.BaselinePlacements)
                if (!Nodes.ContainsKey(pair.Key) || pair.Value.MatchId != pair.Key)
                    Issue(SchedulingConstraintCode.PlacementIdentity, "原始位置引用了未知或不一致的比赛身份。", related: pair.Key);
        foreach (var pair in request.Results)
        {
            if (!Nodes.TryGetValue(pair.Key.MatchId, out var node) || node.ProjectId != pair.Key.ProjectId || pair.Value.Key != pair.Key ||
                pair.Value.Winner.IdentityKey == pair.Value.Loser.IdentityKey || string.IsNullOrWhiteSpace(pair.Value.Score) ||
                pair.Value.DurationMinutes <= 0 || pair.Value.RecordedAt == default)
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
        var resolver = new ConditionalPlayerPaths(Nodes, request.Results);
        foreach (var node in Nodes.Values) { Paths[node.Id] = resolver.Resolve(node); Durations[node.Id] = ScheduleTimingResolver.Resolve(node, policy); }
    }

    private static bool Ratio(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    private static bool SameEntrant(EntrantSource.Participant a, EntrantSource.Participant b) =>
        a.IdentityKey == b.IdentityKey && a.Players.Select(p => p.IdentityKey).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(b.Players.Select(p => p.IdentityKey).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    private static Guid? SourceId(EntrantSource source) => source switch { EntrantSource.WinnerOf w => w.MatchId, EntrantSource.LoserOf l => l.MatchId, _ => null };
    internal double Minute(MatchPlacement p, bool end = false)
    {
        var day = Days.First(d => d.DayLabel == p.DayLabel);
        return (long)day.Date.DayNumber * 1440 + (end ? p.EndTime : p.StartTime).ToTimeSpan().TotalMinutes;
    }
}
