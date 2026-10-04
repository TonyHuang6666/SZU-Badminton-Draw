namespace BadmintonDraw.Core.Scheduling;

/// <summary>Optional deterministic local improvement. Every accepted whole schedule passes the hard gate.</summary>
internal static class TournamentScheduleBalancer
{
    internal static IReadOnlyDictionary<Guid, MatchPlacement> Improve(TournamentPlacementValidator validator,
        TournamentSearchState state, SchedulingWorkBudget budget, TournamentSchedulingOptions options)
    {
        var context = validator.Context;
        if (context.Request.Policy.Strategy != ScheduleAutoSchedulingStrategy.BalancedRelaxed) return state.Placements;
        bool Spend(long units) => budget.TrySpend(SchedulingRunPhase.Optimization, units);
        var count = context.Nodes.Count;
        // Ranking scans project depths and policy targets; copies also rebuild player indexes.
        var proposalWork = 1L + count * (4L + count + context.Days.Length *
            (2L + context.Request.Policy.StageWaveTargets.Count) + context.Request.Policy.FinalDayRules.Count) +
            context.Paths.Values.Sum(p => (long)p.Count) + context.Days.Length * (long)(count + context.Days.Length +
                context.RequestedPolicy.DayLoadTargets.Count);
        if (!Spend(proposalWork + count * (long)count)) return state.Placements;
        foreach (var day in context.Days)
            if (!Spend(SchedulingCapacityArithmetic.Add(1L + (day.UnavailableCourtWindows?.Count ?? 0),
                SchedulingCapacityArithmetic.DayIntegrationWork(day)))) return state.Placements;
        var scorer = new TournamentPlacementScorer(context);
        var current = state;
        var rank = scorer.ScheduleRank(current);
        var ids = context.Nodes.Values.OrderBy(n => n.Order).ThenBy(n => n.Id)
            .Where(n => !context.LockedIds.Contains(n.Id)).Select(n => n.Id).ToArray();
        var stopped = false;

        bool Accept(Dictionary<Guid, MatchPlacement> proposal)
        {
            if (!Spend(proposalWork)) { stopped = true; return false; }
            var candidate = new TournamentSearchState(context, proposal);
            var candidateRank = scorer.ScheduleRank(candidate);
            if (candidateRank.CompareTo(rank) >= 0) return false;
            var gate = validator.ValidateScheduleBounded(candidate, budget, SchedulingRunPhase.Optimization);
            if (gate.Status != BoundedValidationStatus.Valid)
            {
                stopped = budget.IsCanceled || budget.HasFailedSpend(SchedulingRunPhase.Optimization);
                return false;
            }
            current = candidate;
            rank = candidateRank;
            return true;
        }

        while (!stopped)
        {
            var improved = false;
            foreach (var id in ids)
            {
                if (!Spend(1)) return current.Placements;
                var old = current.Placements[id];
                foreach (var day in context.Days)
                {
                    if (!Spend(1)) return current.Placements;
                    if (day.DayLabel == old.DayLabel) continue;
                    if (day.DayEnd - day.DayStart < TimeSpan.FromMinutes(context.Durations[id])) continue;
                    // Explicit preferences and aggregate load depend on the destination date,
                    // never its start or court. A worse primary objective cannot be rescued
                    // by a secondary time preference, so skip this entire date's grid.
                    if (!Spend(proposalWork + count)) return current.Placements;
                    var dayProbe = current.Placements.ToDictionary();
                    dayProbe[id] = old with { DayLabel = day.DayLabel, StartTime = day.DayStart,
                        EndTime = day.DayStart.AddMinutes(context.Durations[id]), Court = day.Courts[0] };
                    var probeRank = scorer.ScheduleRank(new(context, dayProbe));
                    var preferenceOrder = probeRank.ExplicitPreference.CompareTo(rank.ExplicitPreference);
                    if (preferenceOrder > 0 || preferenceOrder == 0 && probeRank.LoadDelta > rank.LoadDelta) continue;
                    var boundaries = 2L + Math.Max(0, (int)(day.DayEnd - day.DayStart).TotalMinutes + 1) +
                        count * 4L + (day.RefereeCapacityWindows?.Count ?? 0) * 2L + (day.UnavailableCourtWindows?.Count ?? 0) * 2L;
                    if (!Spend(count + boundaries * (2L + (long)Math.Ceiling(Math.Log2(Math.Max(2, boundaries)))))) return current.Placements;
                    var without = current.Placements.Where(p => p.Key != id).ToDictionary();
                    foreach (var start in TournamentCandidateEnumeration.Starts(day, id, context, without))
                    {
                        foreach (var court in day.Courts)
                        {
                            // Reserve the dictionary copy before allocating it, including rejected attempts.
                            if (!Spend(count + 1L)) return current.Placements;
                            var proposal = current.Placements.ToDictionary();
                            proposal[id] = old with { DayLabel = day.DayLabel, StartTime = start,
                                EndTime = start.AddMinutes(context.Durations[id]), Court = court };
                            if (Accept(proposal)) { improved = true; break; }
                            if (stopped) return current.Placements;
                        }
                        if (improved) break;
                    }
                    if (improved) break;
                }
                if (improved) break;
            }
            if (improved) continue;
            for (var i = 0; i < ids.Length && !improved; i++)
            for (var j = i + 1; j < ids.Length; j++)
            {
                if (!Spend(count + 1L)) return current.Placements;
                var first = current.Placements[ids[i]];
                var second = current.Placements[ids[j]];
                if (first.DayLabel == second.DayLabel) continue;
                var proposal = current.Placements.ToDictionary();
                proposal[first.MatchId] = first with { DayLabel = second.DayLabel, Court = second.Court,
                    StartTime = second.StartTime, EndTime = second.StartTime.AddMinutes(context.Durations[first.MatchId]) };
                proposal[second.MatchId] = second with { DayLabel = first.DayLabel, Court = first.Court,
                    StartTime = first.StartTime, EndTime = first.StartTime.AddMinutes(context.Durations[second.MatchId]) };
                if (Accept(proposal)) { improved = true; break; }
                if (stopped) return current.Placements;
            }
            if (!improved) return current.Placements;
        }
        return current.Placements;
    }
}
