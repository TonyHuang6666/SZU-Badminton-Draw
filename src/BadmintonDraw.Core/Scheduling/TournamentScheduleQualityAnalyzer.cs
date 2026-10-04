namespace BadmintonDraw.Core.Scheduling;

public sealed class TournamentScheduleQualityAnalyzer
{
    public TournamentScheduleQuality Analyze(TournamentSchedulingRequest request, IReadOnlyDictionary<Guid, MatchPlacement> placements)
    {
        var budget = new SchedulingWorkBudget(TournamentSchedulingOptions.Default, default);
        if (!GraphSchedulingCandidates.TryCreate(request, budget, out var context)) return Incomplete();
        // Balanced archives retain requested preferences. Resolve runtime defaults once
        // before scoring so reopened schedules use the same policy as generation.
        if (context!.InputViolations.Count == 0)
        {
            try { context.ApplyEffectivePolicy(); }
            catch (SchedulingContextInterruptedException) { return Incomplete(); }
        }
        return AnalyzeBounded(context.Request, new(context, placements), budget, false);
    }

    private static TournamentScheduleQuality Incomplete() => new(0, 0, [], [], [], 0, 0)
    { HardValidationComplete = false, PlayerAnalysisComplete = false, SoftAnalysisComplete = false };

    internal TournamentScheduleQuality AnalyzeBounded(TournamentSchedulingRequest request, TournamentSearchState state,
        SchedulingWorkBudget budget, bool hardValidationAlreadyPassed)
    {
        var context = state.Context;
        var placements = state.Placements;
        var validation = hardValidationAlreadyPassed ? new BoundedPlacementValidation(BoundedValidationStatus.Valid, []) :
            TournamentPlacementValidator.FromContext(context).ValidateScheduleBounded(state, budget, SchedulingRunPhase.Validation);
        var inputValid = context.InputViolations.Count == 0;
        var playerComplete = false;
        var playerLoads = inputValid ? TournamentPlayerLoadAnalysis.AnalyzeBounded(context, placements, budget, out playerComplete) : [];
        var loads = new List<SchedulingDayCapacity>();
        foreach (var day in context.Days.Where(d => d.DayStart < d.DayEnd))
        {
            if (!budget.TrySpend(SchedulingRunPhase.Quality, 1L + (day.UnavailableCourtWindows?.Count ?? 0))) break;
            if (!budget.TrySpend(SchedulingRunPhase.Quality, SchedulingCapacityArithmetic.Add(
                1L + placements.Count + (day.UnavailableCourtWindows?.Count ?? 0), SchedulingCapacityArithmetic.DayIntegrationWork(day)))) break;
            loads.Add(new(day.DayLabel, ScheduleResourceCalculator.CalculateDayCapacityMinutes(context.Request.Resources, day),
                placements.Values.Where(p => p.DayLabel == day.DayLabel).Sum(p => (int)(p.EndTime - p.StartTime).TotalMinutes)));
        }
        long score = 0;
        // Reserve usage scans and graph preference scans before creating the scorer,
        // whose constructor integrates each day's resources.
        var scoringWork = SchedulingCapacityArithmetic.Multiply(placements.Count,
            1L + placements.Count * 2L + context.Nodes.Count * 3L + context.Days.Length * 3L +
            context.Request.Policy.DayLoadTargets.Count + context.Request.Policy.FinalDayRules.Count);
        var softComplete = inputValid && budget.TrySpend(SchedulingRunPhase.Quality, 1L + context.Days.Length);
        if (softComplete)
        {
            foreach (var day in context.Days)
            {
                if (!budget.TrySpend(SchedulingRunPhase.Quality, 1L + (day.UnavailableCourtWindows?.Count ?? 0)))
                { softComplete = false; break; }
                scoringWork = SchedulingCapacityArithmetic.Add(scoringWork, SchedulingCapacityArithmetic.DayIntegrationWork(day));
            }
            softComplete = softComplete && budget.TrySpend(SchedulingRunPhase.Quality, scoringWork);
        }
        if (softComplete)
        {
            var scorer = new TournamentPlacementScorer(context);
            foreach (var placement in placements.Values.Where(p => context.Nodes.ContainsKey(p.MatchId) && p.DayLabel is not null && context.DayIndexes.ContainsKey(p.DayLabel)))
                score += scorer.Score(context.Nodes[placement.MatchId], placement,
                    placements.Where(pair => pair.Key != placement.MatchId).ToDictionary());
        }
        var moved = !softComplete || request.BaselinePlacements is null ? [] : placements.Values.Where(p =>
            request.BaselinePlacements.TryGetValue(p.MatchId, out var old) && p != old).ToArray();
        return new(validation.Violations.Count, score, validation.Violations, loads, playerLoads, moved.Length,
            moved.Count(p => request.BaselinePlacements![p.MatchId].DayLabel != p.DayLabel))
        { HardValidationComplete = validation.Status != BoundedValidationStatus.Unknown, PlayerAnalysisComplete = inputValid && playerComplete,
            SoftAnalysisComplete = softComplete };
    }
}

internal static class TournamentPlayerLoadAnalysis
{
    internal static IReadOnlyList<TournamentPlayerDailyLoadForecast> AnalyzeBounded(GraphSchedulingCandidates context,
        IReadOnlyDictionary<Guid, MatchPlacement> placements, SchedulingWorkBudget budget, out bool complete, double winProbability = .5)
    {
        complete = false;
        if (!budget.TrySpend(SchedulingRunPhase.Quality, 1L + placements.Count)) return [];
        long pathCount = 0;
        var maximumIdentityLength = 1;
        foreach (var placement in placements.Values)
            if (context.Paths.TryGetValue(placement.MatchId, out var paths))
            {
                if (!budget.TrySpend(SchedulingRunPhase.Quality, 1L + paths.Count)) return [];
                pathCount += paths.Count;
                foreach (var path in paths)
                {
                    if (!budget.TrySpend(SchedulingRunPhase.Quality, 4L + path.PlayerKey.Length + path.Conditions.Count)) return [];
                    maximumIdentityLength = Math.Max(maximumIdentityLength, path.PlayerKey.Length + (placement.DayLabel?.Length ?? 0));
                }
            }
        // Reserve both stable identity ordering and final result ordering. The number
        // of player/day groups cannot exceed the already charged path count.
        var sortWork = SchedulingCapacityArithmetic.Multiply(pathCount,
            SchedulingCapacityArithmetic.Multiply(maximumIdentityLength, 2L * (1 + (long)Math.Ceiling(Math.Log2(Math.Max(1, pathCount))))));
        if (!budget.TrySpend(SchedulingRunPhase.Quality, sortWork)) return [];
        var groups = placements.Values.Where(p => context.Paths.ContainsKey(p.MatchId))
            .SelectMany(p => context.Paths[p.MatchId].Select(path => (Placement: p, Path: path)))
            .GroupBy(x => (Player: x.Path.PlayerKey.ToUpperInvariant(), x.Placement.DayLabel))
            .OrderBy(g => g.Key.DayLabel, StringComparer.Ordinal).ThenBy(g => g.Key.Player, StringComparer.Ordinal).ToArray();
        var probability = double.IsFinite(winProbability) ? Math.Clamp(winProbability, .01, .99) : .5;
        var result = new List<TournamentPlayerDailyLoadForecast>();
        complete = true;
        foreach (var group in groups)
        {
            var first = group.First();
            var matches = group.GroupBy(x => x.Placement.MatchId).Select(g => new AppearanceGroup(g.Key,
                g.Select(x => x.Path).ToArray())).ToArray();
            var confirmed = matches.Count(m => m.Alternatives.Any(p => p.Conditions.Count == 0));
            var lookupWork = 1L + first.Path.PlayerKey.Length + matches.Length * (long)matches.Length;
            AppearanceBounds? proof = null;
            if (budget.TrySpend(SchedulingRunPhase.Quality, lookupWork))
            {
                var cacheKey = new AppearanceCacheKey(first.Path.PlayerKey, matches.Select(m => m.MatchId).Order().ToArray());
                context.TryGetAppearanceBounds(cacheKey, out var cached);
                if (cached?.IsExact == true) proof = cached;
                else
                {
                    proof = ConditionalAppearanceProof.Prove(matches, int.MaxValue, budget, SchedulingRunPhase.Quality);
                    context.CacheAppearanceBounds(cacheKey, proof);
                    if (cached is not null && cached.LowerBound > proof.LowerBound) proof = proof with { LowerBound = cached.LowerBound };
                }
            }
            var lower = Math.Max(confirmed, proof?.LowerBound ?? confirmed);
            var upper = proof?.UpperBound ?? matches.Length;
            int? maximum = proof?.IsExact == true ? proof.LowerBound : null;
            var distribution = new Dictionary<int, double>();
            var exact = false;
            if (maximum is not null && budget.TrySpend(SchedulingRunPhase.Quality, group.Sum(x => 1L + x.Path.Conditions.Count)))
            {
                var variables = matches.SelectMany(m => m.Alternatives.SelectMany(p => p.Conditions.Keys)).Distinct().ToArray();
                // A size guard is unknown, not a refused budget spend. Every player
                // otherwise draws from the one request-level Quality allowance.
                if (variables.Length <= 18)
                {
                    var index = variables.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
                    var assignmentWork = 1L + variables.Length + matches.Sum(m => 1L + m.Alternatives.Sum(p => 1L + p.Conditions.Count));
                    exact = true;
                    for (var mask = 0; mask < (1 << variables.Length); mask++)
                    {
                        if (!budget.TrySpend(SchedulingRunPhase.Quality, assignmentWork)) { exact = false; break; }
                        var mass = 1d;
                        for (var i = 0; i < variables.Length; i++) mass *= (mask & (1 << i)) != 0 ? probability : 1 - probability;
                        var count = matches.Count(m => m.Alternatives.Any(p => p.Conditions.All(pair =>
                            ((mask & (1 << index[pair.Key])) != 0) == pair.Value)));
                        distribution[count] = distribution.GetValueOrDefault(count) + mass;
                    }
                }
            }
            if (!exact) distribution.Clear();
            if (!exact || maximum is null) complete = false;
            result.Add(new(first.Path.PlayerKey, first.Path.PlayerName, first.Placement.DayLabel, confirmed, maximum,
                exact ? distribution.Sum(p => p.Key * p.Value) : null,
                exact ? distribution.Where(p => p.Key >= context.Request.Resources.MaxPlayerMatchesPerDay).Sum(p => p.Value) : null,
                distribution, exact, matches.Select(m => m.MatchId).ToArray()) { MaximumLowerBound = lower, MaximumUpperBound = upper });
        }
        return result.OrderBy(p => p.DayLabel, StringComparer.Ordinal).ThenByDescending(p => p.MaximumCount ?? p.MaximumLowerBound)
            .ThenBy(p => p.PlayerKey, StringComparer.Ordinal).ToArray();
    }
}
