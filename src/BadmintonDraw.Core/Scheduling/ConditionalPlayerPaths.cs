using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Core.Scheduling;

// Conditions describe the winning SIDE of a source match, so paths from different participants
// share the same outcome variable. Winner/loser labels alone are not an outcome assignment.
internal sealed record ConditionalPlayerPath(string PlayerKey, string PlayerName, IReadOnlyDictionary<Guid, bool> Conditions);
internal sealed record ResolvedMatchPlayerPaths(IReadOnlyList<ConditionalPlayerPath> SideA,
    IReadOnlyList<ConditionalPlayerPath> SideB, IReadOnlyList<ConditionalPlayerPath> All);

internal sealed class ConditionalPlayerPaths(IReadOnlyDictionary<Guid, MatchNode> nodes,
    IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> results)
{
    private readonly Dictionary<Guid, ResolvedMatchPlayerPaths> cache = [];

    internal ResolvedMatchPlayerPaths Resolve(MatchNode node)
    {
        if (cache.TryGetValue(node.Id, out var paths)) return paths;
        var sideA = Side(node.SideA).DistinctBy(Key).ToArray();
        var sideB = Side(node.SideB).DistinctBy(Key).ToArray();
        return cache[node.Id] = new(sideA, sideB, sideA.Concat(sideB).DistinctBy(Key).ToArray());
    }

    private IReadOnlyList<ConditionalPlayerPath> Side(EntrantSource side)
    {
        if (side is EntrantSource.Participant p)
            return p.Players.Count == 0
                ? [new(p.IdentityKey, p.DisplayName, new Dictionary<Guid, bool>())]
                : p.Players.Select(player => new ConditionalPlayerPath(player.IdentityKey, player.DisplayName, new Dictionary<Guid, bool>())).ToArray();
        var sourceId = side switch { EntrantSource.WinnerOf w => w.MatchId, EntrantSource.LoserOf l => l.MatchId, _ => Guid.Empty };
        if (sourceId == Guid.Empty) return [];
        var source = nodes[sourceId];
        var winner = side is EntrantSource.WinnerOf;
        if (results.TryGetValue(new(source.ProjectId, source.Id), out var result))
            return Side(winner ? result.Winner : result.Loser);
        return Side(source.SideA).Select(path => Add(path, sourceId, winner))
            .Concat(Side(source.SideB).Select(path => Add(path, sourceId, !winner)))
            .OfType<ConditionalPlayerPath>().DistinctBy(Key).ToArray();
    }

    private static ConditionalPlayerPath? Add(ConditionalPlayerPath path, Guid id, bool winnerSideA)
    {
        if (path.Conditions.TryGetValue(id, out var existing) && existing != winnerSideA) return null;
        var conditions = new Dictionary<Guid, bool>(path.Conditions) { [id] = winnerSideA };
        return path with { Conditions = conditions };
    }

    private static string Key(ConditionalPlayerPath p) => p.PlayerKey.ToUpperInvariant() + "|" +
        string.Join(";", p.Conditions.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"));

    internal static bool Compatible(IReadOnlyDictionary<Guid, bool> a, IReadOnlyDictionary<Guid, bool> b) =>
        a.All(pair => !b.TryGetValue(pair.Key, out var value) || pair.Value == value);

    internal static bool SharesPlayer(IReadOnlyList<ConditionalPlayerPath> a, IReadOnlyList<ConditionalPlayerPath> b) =>
        a.Any(x => b.Any(y => string.Equals(x.PlayerKey, y.PlayerKey, StringComparison.OrdinalIgnoreCase) && Compatible(x.Conditions, y.Conditions)));

    // Each group is ONE match with alternative paths. Branch on a path (or omit the match),
    // carrying a shared assignment; independent mutually exclusive branches are not double-counted.
    internal static int MaximumAppearances(IReadOnlyList<IReadOnlyList<ConditionalPlayerPath>> matches, int stopAt = int.MaxValue)
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
