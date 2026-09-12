namespace BadmintonDraw.Core.Scheduling;

internal enum AppearanceProof { Unknown, ProvenBelow, ProvenAtLeast }

// One deterministic budget belongs to one preflight, not to each player. Charges
// precede scans, sorting and copies. Exhaustion is never evidence of infeasibility.
internal sealed class AppearanceProofBudget
{
    private long remaining;
    internal bool Exhausted { get; private set; }
    internal AppearanceProofBudget(int workUnits) => remaining = Math.Max(0, workUnits);
    internal bool TrySpend(long units)
    {
        if (Exhausted || units > remaining) { Exhausted = true; remaining = 0; return false; }
        remaining -= units;
        return true;
    }
}

internal static class TournamentPlayerCapacity
{
    internal const int DefaultWorkUnits = 100_000;
    internal const int MaximumProofGroups = 256;

    internal static SchedulingViolation? FindProvenOverload(GraphSchedulingCandidates context, AppearanceProofBudget budget)
    {
        var capacity = (long)context.Days.Length * context.Request.Resources.MaxPlayerMatchesPerDay;
        if (context.Nodes.Count <= capacity) return null;
        var players = new Dictionary<string, Dictionary<Guid, List<ConditionalPlayerPath>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in context.Paths)
        {
            if (!budget.TrySpend(1)) return null;
            foreach (var path in match.Value)
            {
                if (!budget.TrySpend(1)) return null;
                if (!players.TryGetValue(path.PlayerKey, out var matches)) players[path.PlayerKey] = matches = [];
                if (!matches.TryGetValue(match.Key, out var alternatives)) matches[match.Key] = alternatives = [];
                alternatives.Add(path);
            }
        }
        foreach (var player in players)
        {
            if (!budget.TrySpend(1)) return null;
            var count = player.Value.Count;
            if (count <= capacity || count > MaximumProofGroups) continue;
            // The cast is safe only after the distinct-group count bounds the threshold.
            var target = (int)(capacity + 1);
            if (!budget.TrySpend(count)) return null;
            var matches = player.Value.Values.Cast<IReadOnlyList<ConditionalPlayerPath>>().ToArray();
            var proof = ConditionalPlayerPaths.ProveAtLeast(matches, target, budget);
            if (proof == AppearanceProof.ProvenAtLeast)
                return new(SchedulingConstraintCode.DailyMatchLimit, null, null, null,
                    $"选手 {player.Key} 存在至少 {target} 场可同时成立的参赛路径；{context.Days.Length} 个比赛日 × 每日 {context.Request.Resources.MaxPlayerMatchesPerDay} 场仅允许 {capacity} 场，无法保障全部合法赛果路径。");
            if (budget.Exhausted) return null;
        }
        return null;
    }
}
