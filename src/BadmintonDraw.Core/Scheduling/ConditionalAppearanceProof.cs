using System.Collections.ObjectModel;

namespace BadmintonDraw.Core.Scheduling;

internal sealed record AppearanceGroup(Guid MatchId, IReadOnlyList<ConditionalPlayerPath> Alternatives);
internal sealed record AppearanceWitness(IReadOnlyList<Guid> MatchIds, IReadOnlyDictionary<Guid, bool> Conditions);
internal sealed record AppearanceBounds(int LowerBound, int UpperBound, AppearanceWitness Witness, bool IsExact, bool BudgetExhausted);

internal static class ConditionalAppearanceProof
{
    internal static AppearanceBounds Prove(IReadOnlyList<AppearanceGroup> groups, int stopAt,
        SchedulingWorkBudget budget, SchedulingRunPhase phase) => new ProofRun(budget, phase).Prove(groups, stopAt);

    private sealed class ProofRun(SchedulingWorkBudget budget, SchedulingRunPhase phase)
    {
        private bool exhausted;
        private static readonly AppearanceWitness Empty = new(Array.Empty<Guid>(), new ReadOnlyDictionary<Guid, bool>(new Dictionary<Guid, bool>()));

        private bool Spend(long units = 1)
        {
            if (exhausted) return false;
            if (budget.TrySpend(phase, units)) return true;
            exhausted = true;
            return false;
        }

        internal AppearanceBounds Prove(IReadOnlyList<AppearanceGroup> groups, int stopAt)
        {
            var upper = groups.Count; // Safe even before duplicate match IDs have been normalized.
            AppearanceBounds Unknown() => new(0, upper, Empty, false, exhausted);
            if (!Spend()) return Unknown();

            // A match is a single vertex, including ALL of its alternative paths. Normalizing
            // duplicate IDs here also prevents two components from claiming the same match.
            var normalized = new Dictionary<Guid, List<ConditionalPlayerPath>>();
            foreach (var group in groups)
            {
                if (!Spend(1L + group.Alternatives.Count)) return Unknown();
                if (!normalized.TryGetValue(group.MatchId, out var alternatives))
                    normalized.Add(group.MatchId, alternatives = []);
                foreach (var path in group.Alternatives) alternatives.Add(path);
            }
            upper = normalized.Count;
            if (!Spend(3L * upper)) return Unknown();
            var matches = normalized.Select(pair => new AppearanceGroup(pair.Key, pair.Value)).ToArray();
            var parents = Enumerable.Range(0, matches.Length).ToArray();
            var sizes = Enumerable.Repeat(1, matches.Length).ToArray();
            var conditionCounts = new long[matches.Length];
            var variableOwner = new Dictionary<Guid, int>();

            int Root(int index)
            {
                while (parents[index] != index)
                {
                    if (!Spend()) return -1;
                    index = parents[index];
                }
                return index;
            }

            for (var i = 0; i < matches.Length; i++)
            {
                if (!Spend()) return Unknown();
                foreach (var path in matches[i].Alternatives)
                {
                    if (!Spend()) return Unknown();
                    foreach (var variable in path.Conditions.Keys)
                    {
                        if (!Spend(2)) return Unknown();
                        conditionCounts[i]++;
                        if (!variableOwner.TryGetValue(variable, out var owner)) variableOwner.Add(variable, i);
                        else
                        {
                            var a = Root(i);
                            var b = Root(owner);
                            if (a < 0 || b < 0) return Unknown();
                            if (a == b) continue;
                            if (sizes[a] < sizes[b]) (a, b) = (b, a);
                            parents[b] = a;
                            sizes[a] += sizes[b];
                        }
                    }
                }
            }

            var components = new Dictionary<int, Component>();
            for (var i = 0; i < matches.Length; i++)
            {
                if (!Spend(2)) return Unknown();
                var root = Root(i);
                if (root < 0) return Unknown();
                if (!components.TryGetValue(root, out var component)) components.Add(root, component = new());
                component.Matches.Add(matches[i]);
                component.ConditionCount += conditionCounts[i];
            }

            var witness = Empty;
            foreach (var component in components.Values)
            {
                if (witness.MatchIds.Count >= stopAt) break;
                if (!Spend()) break;
                // The recursion limit is per connected component, not per tournament.
                if (component.Matches.Count > TournamentPlayerCapacity.MaximumProofGroups) continue;

                // Reserve the final merge before DFS: a useful partial witness remains returnable
                // even when DFS exhausts the allowance. These are upper bounds on copy sizes.
                if (!Spend((long)witness.MatchIds.Count + component.Matches.Count +
                    witness.Conditions.Count + component.ConditionCount)) break;
                var proof = Search(component.Matches, stopAt - witness.MatchIds.Count);
                var ids = new List<Guid>(witness.MatchIds);
                ids.AddRange(proof.Witness.MatchIds);
                var conditions = new Dictionary<Guid, bool>(witness.Conditions);
                foreach (var condition in proof.Witness.Conditions) conditions.Add(condition.Key, condition.Value);
                witness = new(ids.AsReadOnly(), new ReadOnlyDictionary<Guid, bool>(conditions));
                upper -= component.Matches.Count - proof.UpperBound;
                if (exhausted) break;
            }
            return new(witness.MatchIds.Count, upper, witness, witness.MatchIds.Count == upper, exhausted);
        }

        private AppearanceBounds Search(List<AppearanceGroup> groups, int stopAt)
        {
            var best = Empty;
            var thresholdReached = false;
            // Conservative sorting/copy charge; the recursion guard bounds this to 256 groups.
            if (!Spend((long)groups.Count * groups.Count + groups.Count))
                return new(0, groups.Count, best, false, exhausted);
            var ordered = groups.OrderBy(group => group.Alternatives.Count).ToArray();
            var memo = new Dictionary<string, int>(StringComparer.Ordinal);
            var selected = new List<Guid>();

            void Visit(int index, Dictionary<Guid, bool> assignment)
            {
                if (!Spend()) return;
                if (selected.Count > best.MatchIds.Count)
                {
                    if (!Spend((long)selected.Count + assignment.Count)) return;
                    best = new(selected.ToArray(), new ReadOnlyDictionary<Guid, bool>(new Dictionary<Guid, bool>(assignment)));
                    if (best.MatchIds.Count >= stopAt) { thresholdReached = true; return; }
                }
                if (selected.Count + ordered.Length - index <= best.MatchIds.Count) return;

                // The canonical assignment makes cached states independent of path insertion order.
                // Cache dominance is sound because match IDs are unique: only prefix count matters.
                if (!Spend((long)assignment.Count * assignment.Count + 50L * assignment.Count + 1)) return;
                var key = index + ":" + string.Join(";", assignment.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));
                if (memo.TryGetValue(key, out var priorCount) && priorCount >= selected.Count) return;
                if (!Spend(key.Length + 1L)) return;
                memo[key] = selected.Count;
                budget.ObserveCache("AppearanceProofMemo", memo.Count);

                foreach (var path in ordered[index].Alternatives)
                {
                    if (!Spend(1L + path.Conditions.Count)) return;
                    var compatible = true;
                    foreach (var pair in path.Conditions)
                        if (assignment.TryGetValue(pair.Key, out var value) && value != pair.Value) { compatible = false; break; }
                    if (!compatible) continue;
                    if (!Spend((long)assignment.Count + path.Conditions.Count + 1)) return;
                    var next = new Dictionary<Guid, bool>(assignment);
                    foreach (var pair in path.Conditions) next[pair.Key] = pair.Value;
                    selected.Add(ordered[index].MatchId);
                    Visit(index + 1, next);
                    selected.RemoveAt(selected.Count - 1);
                    if (exhausted || thresholdReached) return;
                }
                Visit(index + 1, assignment);
            }

            Visit(0, []);
            var upper = exhausted || thresholdReached ? groups.Count : best.MatchIds.Count;
            return new(best.MatchIds.Count, upper, best, best.MatchIds.Count == upper, exhausted);
        }

        private sealed class Component
        {
            internal List<AppearanceGroup> Matches { get; } = [];
            internal long ConditionCount { get; set; }
        }
    }
}
