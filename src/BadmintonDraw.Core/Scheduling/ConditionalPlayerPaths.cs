using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Core.Scheduling;

// Conditions describe the winning SIDE of a source match, so paths from different participants
// share the same outcome variable. Winner/loser labels alone are not an outcome assignment.
internal sealed record ConditionalPlayerPath(string PlayerKey, string PlayerName, IReadOnlyDictionary<Guid, bool> Conditions);
internal sealed record ResolvedMatchPlayerPaths(IReadOnlyList<ConditionalPlayerPath> SideA,
    IReadOnlyList<ConditionalPlayerPath> SideB, IReadOnlyList<ConditionalPlayerPath> All);

internal sealed class ConditionalPlayerPaths(IReadOnlyDictionary<Guid, MatchNode> nodes,
    IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> results, SchedulingWorkBudget? budget = null)
{
    private readonly Dictionary<Guid, ResolvedMatchPlayerPaths> cache = [];
    private void Charge(long units = 1)
    {
        if (budget is not null && !budget.TrySpend(SchedulingRunPhase.Context, units)) throw new SchedulingContextInterruptedException();
    }

    internal ResolvedMatchPlayerPaths Resolve(MatchNode node)
    {
        Charge();
        if (cache.TryGetValue(node.Id, out var paths)) return paths;
        var sideA = Side(node.SideA).DistinctBy(Key).ToArray();
        var sideB = Side(node.SideB).DistinctBy(Key).ToArray();
        return cache[node.Id] = new(sideA, sideB, sideA.Concat(sideB).DistinctBy(Key).ToArray());
    }

    private IReadOnlyList<ConditionalPlayerPath> Side(EntrantSource side)
    {
        if (side is EntrantSource.Participant p)
        {
            Charge(p.Players.Count + 1L);
            return p.Players.Count == 0
                ? [new(p.IdentityKey, p.DisplayName, new Dictionary<Guid, bool>())]
                : p.Players.Select(player => new ConditionalPlayerPath(player.IdentityKey, player.Name.Trim(), new Dictionary<Guid, bool>())).ToArray();
        }
        Charge();
        var sourceId = side switch { EntrantSource.WinnerOf w => w.MatchId, EntrantSource.LoserOf l => l.MatchId, _ => Guid.Empty };
        if (sourceId == Guid.Empty) return [];
        var source = nodes[sourceId];
        var winner = side is EntrantSource.WinnerOf;
        if (results.TryGetValue(new(source.ProjectId, source.Id), out var result))
            return Side(winner ? result.Winner : result.Loser);
        var sourcePaths = Resolve(source);
        return sourcePaths.SideA.Select(path => Add(path, sourceId, winner))
            .Concat(sourcePaths.SideB.Select(path => Add(path, sourceId, !winner)))
            .OfType<ConditionalPlayerPath>().DistinctBy(Key).ToArray();
    }

    private ConditionalPlayerPath? Add(ConditionalPlayerPath path, Guid id, bool winnerSideA)
    {
        Charge(path.Conditions.Count + 1L);
        if (path.Conditions.TryGetValue(id, out var existing) && existing != winnerSideA) return null;
        var conditions = new Dictionary<Guid, bool>(path.Conditions) { [id] = winnerSideA };
        return path with { Conditions = conditions };
    }

    private string Key(ConditionalPlayerPath p)
    {
        Charge(p.PlayerKey.Length + p.Conditions.Count * (long)p.Conditions.Count + 50L * p.Conditions.Count + 1);
        return p.PlayerKey.ToUpperInvariant() + "|" + string.Join(";", p.Conditions.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"));
    }

    internal static bool Compatible(IReadOnlyDictionary<Guid, bool> a, IReadOnlyDictionary<Guid, bool> b) =>
        a.All(pair => !b.TryGetValue(pair.Key, out var value) || pair.Value == value);

    internal static bool SharesPlayer(IReadOnlyList<ConditionalPlayerPath> a, IReadOnlyList<ConditionalPlayerPath> b) =>
        a.Any(x => b.Any(y => string.Equals(x.PlayerKey, y.PlayerKey, StringComparison.OrdinalIgnoreCase) && Compatible(x.Conditions, y.Conditions)));

    internal static bool SharesPlayer(IReadOnlyList<ConditionalPlayerPath> a, IReadOnlyList<ConditionalPlayerPath> b, SchedulingWorkBudget? budget)
    {
        foreach (var x in a)
        foreach (var y in b)
        {
            if (budget is not null && !budget.TrySpend(SchedulingRunPhase.Context, 1L + x.PlayerKey.Length + y.PlayerKey.Length + x.Conditions.Count))
                throw new SchedulingContextInterruptedException();
            if (string.Equals(x.PlayerKey, y.PlayerKey, StringComparison.OrdinalIgnoreCase) && Compatible(x.Conditions, y.Conditions)) return true;
        }
        return false;
    }

    // Each group is ONE match with alternative paths. Branch on a path (or omit the match),
    // carrying a shared assignment; independent mutually exclusive branches are not double-counted.
    internal static int MaximumAppearances(IReadOnlyList<IReadOnlyList<ConditionalPlayerPath>> matches, int stopAt = int.MaxValue) =>
        SearchAppearances(matches, stopAt);

    internal static AppearanceProof ProveAtLeast(IReadOnlyList<IReadOnlyList<ConditionalPlayerPath>> matches, int target, AppearanceProofBudget budget)
    {
        // Unlike the unbounded exact validator, this optional proof must also bound
        // recursion depth. Skipping an oversized proof says nothing about feasibility.
        if (matches.Count > TournamentPlayerCapacity.MaximumProofGroups) return AppearanceProof.Unknown;
        if (!budget.TrySpend(matches.Count)) return AppearanceProof.Unknown;
        // Legacy callers identify matches by group position; the bounded API carries real IDs.
        var groups = matches.Select((paths, index) => new AppearanceGroup(new Guid(index, 0, 0, new byte[8]), paths)).ToArray();
        var proof = ConditionalAppearanceProof.Prove(groups, target, budget.WorkBudget, budget.Phase);
        if (proof.BudgetExhausted) budget.MarkExhausted();
        return proof.LowerBound >= target ? AppearanceProof.ProvenAtLeast :
            proof.UpperBound < target ? AppearanceProof.ProvenBelow : AppearanceProof.Unknown;
    }

    private static int SearchAppearances(IReadOnlyList<IReadOnlyList<ConditionalPlayerPath>> matches, int stopAt)
    {
        var ordered = matches.OrderBy(x => x.Min(p => p.Conditions.Count)).ToArray();
        var best = 0;
        void Visit(int index, int count, Dictionary<Guid, bool> assignment)
        {
            if (best >= stopAt || count + ordered.Length - index <= best) return;
            if (index == ordered.Length) { best = Math.Max(best, count); return; }
            foreach (var path in ordered[index])
            {
                if (!Compatible(assignment, path.Conditions)) continue;
                var merged = new Dictionary<Guid, bool>(assignment);
                foreach (var pair in path.Conditions) merged[pair.Key] = pair.Value;
                Visit(index + 1, count + 1, merged);
                if (best >= stopAt) return;
            }
            Visit(index + 1, count, assignment);
        }
        Visit(0, 0, []);
        return best;
    }
}
