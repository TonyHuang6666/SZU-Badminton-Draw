using System.Collections.ObjectModel;

namespace BadmintonDraw.Core.Scheduling;

/// <summary>One reversible placement set. Indexes only contain structurally usable entries;
/// malformed initial entries remain visible to the shared validation gate.</summary>
internal sealed class TournamentSearchState
{
    private readonly Dictionary<Guid, MatchPlacement> placements;
    private readonly Dictionary<string, long> usedTicks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Guid>> byDay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Guid>> byCourt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> nullCourtIds = [];
    private readonly Dictionary<string, HashSet<Guid>> byPlayer = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TimeValidationKey, IReadOnlyList<SchedulingViolation>> timeChecks = [];
    private readonly Queue<TimeValidationKey> timeOrder = [];

    internal GraphSchedulingCandidates Context { get; }
    internal IReadOnlyDictionary<Guid, MatchPlacement> Placements { get; }
    internal IReadOnlyDictionary<string, long> UsedTicksByDay { get; }
    internal int TimeCacheEntryCount => timeChecks.Count;

    internal TournamentSearchState(GraphSchedulingCandidates context, IReadOnlyDictionary<Guid, MatchPlacement> initialPlacements)
    {
        Context = context;
        placements = new(initialPlacements);
        Placements = new ReadOnlyDictionary<Guid, MatchPlacement>(placements);
        UsedTicksByDay = new ReadOnlyDictionary<string, long>(usedTicks);
        foreach (var pair in placements)
            if (CanIndex(pair.Key, pair.Value)) Index(pair.Value, true);
    }

    internal void Add(MatchPlacement placement)
    {
        placements.Add(placement.MatchId, placement);
        if (CanIndex(placement.MatchId, placement)) Index(placement, true);
        InvalidateTimeChecks();
    }

    internal MatchPlacement Remove(Guid matchId)
    {
        if (Context.LockedIds.Contains(matchId)) throw new InvalidOperationException("Locked placements cannot be removed.");
        var placement = placements[matchId];
        if (CanIndex(matchId, placement)) Index(placement, false);
        placements.Remove(matchId);
        InvalidateTimeChecks();
        return placement;
    }

    internal IReadOnlyDictionary<Guid, MatchPlacement> Snapshot() =>
        new ReadOnlyDictionary<Guid, MatchPlacement>(new Dictionary<Guid, MatchPlacement>(placements));

    internal IEnumerable<MatchPlacement> OnDay(string day) => Lookup(byDay, day);
    internal IEnumerable<MatchPlacement> OnCourt(string? court) => court is null
        ? nullCourtIds.Select(id => placements[id]) : Lookup(byCourt, court);
    internal IEnumerable<MatchPlacement> ForPlayer(string player) => Lookup(byPlayer, player);

    private IEnumerable<MatchPlacement> Lookup(Dictionary<string, HashSet<Guid>> index, string key) =>
        index.TryGetValue(key, out var ids) ? ids.Select(id => placements[id]) : [];

    private bool CanIndex(Guid id, MatchPlacement placement) => id == placement.MatchId &&
        Context.Nodes.ContainsKey(id) && placement.DayLabel is not null && Context.DayIndexes.ContainsKey(placement.DayLabel);

    private void Index(MatchPlacement placement, bool add)
    {
        void Change(Dictionary<string, HashSet<Guid>> index, string key)
        {
            if (add)
            {
                if (!index.TryGetValue(key, out var ids)) index.Add(key, ids = []);
                ids.Add(placement.MatchId);
            }
            else if (index.TryGetValue(key, out var ids))
            {
                ids.Remove(placement.MatchId);
                if (ids.Count == 0) index.Remove(key);
            }
        }
        Change(byDay, placement.DayLabel);
        if (placement.Court is not null) Change(byCourt, placement.Court);
        else if (add) nullCourtIds.Add(placement.MatchId);
        else nullCourtIds.Remove(placement.MatchId);
        if (Context.Paths.TryGetValue(placement.MatchId, out var paths))
            foreach (var key in paths.Select(p => p.PlayerKey).Distinct(StringComparer.OrdinalIgnoreCase)) Change(byPlayer, key);
        var ticks = (placement.EndTime - placement.StartTime).Ticks;
        usedTicks[placement.DayLabel] = usedTicks.GetValueOrDefault(placement.DayLabel) + (add ? ticks : -ticks);
        if (!byDay.ContainsKey(placement.DayLabel)) usedTicks.Remove(placement.DayLabel);
    }

    private void InvalidateTimeChecks() { timeChecks.Clear(); timeOrder.Clear(); }

    internal bool TryGetTimeCheck(TimeValidationKey key, out IReadOnlyList<SchedulingViolation> issues) => timeChecks.TryGetValue(key, out issues!);

    internal void CacheTimeCheck(TimeValidationKey key, IReadOnlyList<SchedulingViolation> issues)
    {
        if (Context.MaxCacheEntries == 0 || timeChecks.ContainsKey(key)) return;
        if (timeChecks.Count >= Context.MaxCacheEntries) timeChecks.Remove(timeOrder.Dequeue());
        timeChecks.Add(key, issues);
        timeOrder.Enqueue(key);
        Context.ObserveTimeCache(timeChecks.Count);
    }
}

internal readonly record struct TimeValidationKey(Guid MatchId, string DayLabel, TimeOnly Start, TimeOnly End, Guid? OmittedId);
