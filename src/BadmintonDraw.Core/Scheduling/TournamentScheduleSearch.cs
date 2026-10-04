using BadmintonDraw.Core.Matches;

namespace BadmintonDraw.Core.Scheduling;

internal sealed record TournamentSearchResult(IReadOnlyDictionary<Guid, MatchPlacement>? CompletePlacements,
    IReadOnlyDictionary<Guid, MatchPlacement> DiagnosticPlacements, IReadOnlyList<SchedulingViolation> Violations)
{
    internal SchedulingRunPhase Phase { get; init; } = SchedulingRunPhase.Search;
}

/// <summary>Deterministic, bounded depth-first decisions over one reversible request state.</summary>
internal static class TournamentScheduleSearch
{
    internal static TournamentSearchResult Run(TournamentPlacementValidator validator, TournamentSearchState state,
        TournamentPlacementScorer scorer, SchedulingWorkBudget budget, TournamentSchedulingOptions options)
        => new SearchRun(validator, state, scorer, budget, options).Run();

    private sealed record Candidate(MatchPlacement Placement, SchedulingCandidateScore Score, long Order);
    private sealed class Decision(Guid matchId, Candidate[] candidates, int marker)
    {
        internal Guid MatchId { get; } = matchId;
        internal Candidate[] Candidates { get; set; } = candidates;
        internal int Marker { get; } = marker;
        internal int Cursor { get; set; }
        internal bool DenseExpanded { get; set; }
    }

    private sealed class SearchRun(TournamentPlacementValidator validator, TournamentSearchState state,
        TournamentPlacementScorer scorer, SchedulingWorkBudget budget, TournamentSchedulingOptions options)
    {
        private readonly GraphSchedulingCandidates context = validator.Context;
        private readonly List<Decision> decisions = [];
        private IReadOnlyDictionary<Guid, MatchPlacement> diagnostic = state.Placements;
        private IReadOnlyList<SchedulingViolation> violations = [];
        private int backtracks;
        private SchedulingRunPhase phase = SchedulingRunPhase.Search;
        private bool interrupted;

        private bool Spend(long units = 1)
        {
            if (budget.TrySpend(SchedulingRunPhase.Search, units)) return true;
            interrupted = true;
            phase = SchedulingRunPhase.Search;
            return false;
        }

        private TournamentSearchResult Incomplete() => new(null, diagnostic, violations) { Phase = phase };

        internal TournamentSearchResult Run()
        {
            // Reserve initialization, ranking and the initial diagnostic copy before doing work.
            if (!Spend(1L + context.Nodes.Count * 3L + state.Placements.Count)) return Incomplete();
            diagnostic = state.Snapshot();
            var degree = context.Nodes.Keys.ToDictionary(id => id, context.PlayerConflictDegree);
            var projectOrder = context.Request.MatchGraphs.Select((g, i) => (g.ProjectId, i))
                .ToDictionary(x => x.ProjectId, x => x.i);
            var pending = context.Nodes.Keys.Except(state.Placements.Keys).ToHashSet();
            while (!budget.IsCanceled)
            {
                if (pending.Count == 0)
                {
                    phase = SchedulingRunPhase.Validation;
                    var final = validator.ValidateScheduleBounded(state, budget, phase);
                    if (final.Status == BoundedValidationStatus.Valid)
                    {
                        if (!budget.TrySpend(phase, 1L + state.Placements.Count)) return Incomplete();
                        var complete = state.Snapshot();
                        return new(complete, complete, []);
                    }
                    if (final.Status == BoundedValidationStatus.Unknown &&
                        (budget.IsCanceled || budget.HasFailedSpend(phase))) return Incomplete();
                    violations = final.Violations;
                    // An incomplete proof is not a violation; another branch may be provable.
                    if (!Recover(pending)) return Incomplete();
                    phase = SchedulingRunPhase.Search;
                    continue;
                }

                // Covers readiness, playoff scans and deterministic ordering.
                if (!Spend(1L + pending.Count * (long)pending.Count + context.Nodes.Count * 4L)) return Incomplete();
                var next = pending.Select(id => context.Nodes[id])
                    .Where(n => n.Dependencies.All(state.Placements.ContainsKey) && (!n.IsChampionshipFinal ||
                        !pending.Any(id => context.Nodes[id].ProjectId == n.ProjectId && context.Nodes[id].IsPlacementPlayoff)))
                    .OrderByDescending(n => n.SameUnit && n.KnockoutEntrantCount is null).ThenByDescending(n => degree[n.Id])
                    .ThenBy(n => context.Depths[n.Id]).ThenBy(n => projectOrder[n.ProjectId]).ThenBy(n => n.Order).ThenBy(n => n.Id).FirstOrDefault();
                if (next is null)
                {
                    violations = pending.Select(id => new SchedulingViolation(SchedulingConstraintCode.MissingPlacement,
                        context.Nodes[id].ProjectId, id, null, "未安排的来源或名次赛阻塞了后续场次。")).ToArray();
                    if (!Recover(pending)) return Incomplete();
                    continue;
                }
                var limit = Math.Min(64, options.MaxDecisionAlternatives);
                // Reserve alternatives for a dense fallback, instead of spending the
                // entire allowance on minute/court variants before trying one position.
                var candidates = Candidates(next, (limit + 1) / 2, sparse: true);
                var denseExpanded = false;
                if (candidates.Length == 0 && !interrupted)
                {
                    candidates = Candidates(next, limit, sparse: false);
                    denseExpanded = true;
                }
                if (interrupted) return Incomplete();
                if (candidates.Length == 0)
                {
                    if (!Recover(pending)) return Incomplete();
                    continue;
                }
                var decision = new Decision(next.Id, candidates, state.Placements.Count) { DenseExpanded = denseExpanded };
                decisions.Add(decision);
                if (!Apply(decision, pending)) return Incomplete();
            }
            return Incomplete();
        }

        private bool Apply(Decision decision, HashSet<Guid> pending)
        {
            // Index maintenance, cache invalidation and an optional diagnostic snapshot.
            if (!Spend(1L + context.Paths[decision.MatchId].Count + state.TimeCacheEntryCount + state.Placements.Count)) return false;
            state.Add(decision.Candidates[decision.Cursor++].Placement);
            pending.Remove(decision.MatchId);
            if (state.Placements.Count > diagnostic.Count)
            {
                diagnostic = state.Snapshot();
                violations = [];
            }
            return true;
        }

        private bool Recover(HashSet<Guid> pending)
        {
            var depth = 0;
            while (decisions.Count > 0)
            {
                if (depth >= options.MaxRollbackDepth || backtracks >= options.MaxBacktracks) return false;
                var decision = decisions[^1];
                if (!Spend(1L + context.Paths[decision.MatchId].Count + state.TimeCacheEntryCount)) return false;
                state.Remove(decision.MatchId); // Only actual decisions enter this stack; never locks.
                pending.Add(decision.MatchId);
                backtracks++;
                depth++;
                if (state.Placements.Count != decision.Marker) throw new InvalidOperationException("Decision suffix was not fully rolled back.");
                if (decision.Cursor < decision.Candidates.Length) return Apply(decision, pending);
                if (!decision.DenseExpanded)
                {
                    decision.DenseExpanded = true;
                    var remaining = Math.Min(64, options.MaxDecisionAlternatives) - decision.Candidates.Length;
                    if (!Spend(1L + decision.Candidates.Length)) return false;
                    var tried = decision.Candidates.Select(c => c.Placement).ToHashSet();
                    var expanded = Candidates(context.Nodes[decision.MatchId], remaining, sparse: false, tried);
                    if (interrupted) return false;
                    if (expanded.Length > 0)
                    {
                        decision.Candidates = expanded;
                        decision.Cursor = 0;
                        return Apply(decision, pending);
                    }
                }
                decisions.RemoveAt(decisions.Count - 1);
            }
            return false;
        }

        private Candidate[] Candidates(MatchNode node, int limit, bool sparse, HashSet<MatchPlacement>? excluded = null)
        {
            if (limit == 0) return [];
            var best = new List<Candidate>();
            var reserved = new List<Candidate>();
            var rejections = new Dictionary<SchedulingConstraintCode, SchedulingViolation>();
            long order = 0;
            foreach (var day in context.Days)
            {
                var dayBest = new List<Candidate>();
                // Reserve enumeration/sorting before materializing either candidate set.
                var boundaries = 2L + (sparse ? 0 : Math.Max(0, (int)(day.DayEnd - day.DayStart).TotalMinutes + 1))
                    + state.Placements.Count * 4L + (day.RefereeCapacityWindows?.Count ?? 0) * 2L
                    + (day.UnavailableCourtWindows?.Count ?? 0) * 2L;
                if (!Spend(boundaries * (2L + (long)Math.Ceiling(Math.Log2(Math.Max(2, boundaries)))))) return [];
                foreach (var start in TournamentCandidateEnumeration.Starts(day, node.Id, context, state.Placements, !sparse))
                {
                    // Scoring scans these request-local collections; count rejected proposals too.
                    var policy = context.Request.Policy;
                    if (!Spend(2L + state.Placements.Count + context.Nodes.Count + context.Days.Length +
                        policy.DayLoadTargets.Count + policy.FinalDayRules.Count +
                        (policy.SynchronizeStageWaves ? context.Nodes.Count + context.Days.Length * (1L + policy.StageWaveTargets.Count) : 0))) return [];
                    var first = new MatchPlacement(node.Id, day.DayLabel, start, start.AddMinutes(context.Durations[node.Id]), day.Courts[0]);
                    var rank = scorer.Rank(node, first, state);
                    Candidate? bestAtStart = null;
                    foreach (var court in day.Courts)
                    {
                        if (!Spend(3L + court.Length + first.Court.Length)) return [];
                        var placement = first with { Court = court };
                        var candidate = new Candidate(placement, scorer.RankForCourt(rank, node.Id, first.Court, court), order++);
                        if (excluded?.Contains(placement) == true) continue;
                        if (sparse && bestAtStart is not null && Compare(candidate, bestAtStart) >= 0) continue;
                        if (best.Count == limit && Compare(candidate, best[^1]) >= 0 &&
                            dayBest.Count == 2 && Compare(candidate, dayBest[^1]) >= 0) continue;
                        var valid = validator.ValidatePlacementBounded(placement, state, budget, SchedulingRunPhase.Search);
                        if (valid.Status == BoundedValidationStatus.Unknown)
                        {
                            if (budget.IsCanceled || budget.HasFailedSpend(SchedulingRunPhase.Search)) { interrupted = true; return []; }
                            continue;
                        }
                        if (valid.Status == BoundedValidationStatus.Invalid)
                        {
                            foreach (var issue in valid.Violations) rejections.TryAdd(issue.Code, issue);
                            continue;
                        }
                        if (!Spend(4L + best.Count + dayBest.Count)) return [];
                        if (sparse) { bestAtStart = candidate; continue; }
                        Keep(best, candidate, limit);
                        Keep(dayBest, candidate, 2);
                    }
                    // One best legal court per time keeps early alternatives diverse.
                    // Different courts at that time remain available in dense recovery.
                    if (bestAtStart is not null)
                    {
                        Keep(best, bestAtStart, limit);
                        Keep(dayBest, bestAtStart, 2);
                    }
                }
                reserved.AddRange(dayBest);
            }
            if (!Spend(1L + reserved.Count * (long)reserved.Count + best.Count * (long)best.Count)) return [];
            // Reserve two best per valid date, then fill by global score. A finite cap is
            // intentionally incomplete; even exhausting every retained branch proves no impossibility.
            var selected = reserved.OrderBy(c => c.Score).ThenBy(c => c.Order).Take(limit).ToList();
            foreach (var candidate in best)
                if (selected.Count < limit && !selected.Contains(candidate)) selected.Add(candidate);
            if (selected.Count == 0 && state.Placements.Count >= diagnostic.Count)
            {
                rejections.TryAdd(SchedulingConstraintCode.SearchExhausted, new(SchedulingConstraintCode.SearchExhausted,
                    node.ProjectId, node.Id, null, "本次受限搜索未找到合法位置；这不表示所有编排方案都不可能。"));
                violations = rejections.Values.ToArray();
            }
            return selected.OrderBy(c => c.Score).ThenBy(c => c.Order).ToArray();
        }

        private static int Compare(Candidate a, Candidate b)
        {
            var score = a.Score.CompareTo(b.Score);
            return score != 0 ? score : a.Order.CompareTo(b.Order);
        }

        private static void Keep(List<Candidate> candidates, Candidate candidate, int limit)
        {
            var index = candidates.FindIndex(other => Compare(candidate, other) < 0);
            if (index < 0) index = candidates.Count;
            candidates.Insert(index, candidate);
            if (candidates.Count > limit) candidates.RemoveAt(candidates.Count - 1);
        }
    }
}
