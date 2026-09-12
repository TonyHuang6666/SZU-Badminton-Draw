namespace BadmintonDraw.Core.Scheduling;

public sealed class TournamentScheduleQualityAnalyzer
{
    public TournamentScheduleQuality Analyze(TournamentSchedulingRequest request, IReadOnlyDictionary<Guid, MatchPlacement> placements)
    {
        var validator = new TournamentPlacementValidator(request);
        var validation = validator.ValidateSchedule(placements);
        var context = validator.Context;
        var loads = context.Days.Where(d => d.DayStart < d.DayEnd).Select(d => new SchedulingDayCapacity(d.DayLabel,
            ScheduleResourceCalculator.CalculateDayCapacityMinutes(request.Resources, d),
            placements.Values.Where(p => p.DayLabel == d.DayLabel).Sum(p => (int)(p.EndTime - p.StartTime).TotalMinutes))).ToArray();
        long score = 0;
        if (validator.ValidateInput().IsValid)
        {
            var scorer = new TournamentPlacementScorer(context);
            foreach (var p in placements.Values.Where(p => context.Nodes.ContainsKey(p.MatchId) && context.Days.Any(d => d.DayLabel == p.DayLabel)))
                score += scorer.Score(context.Nodes[p.MatchId], p, placements.Where(pair => pair.Key != p.MatchId).ToDictionary());
        }
        var moved = request.BaselinePlacements is null ? [] : placements.Values.Where(p =>
            request.BaselinePlacements.TryGetValue(p.MatchId, out var old) && p != old).ToArray();
        return new(validation.Violations.Count, score, validation.Violations, loads,
            validator.ValidateInput().IsValid ? TournamentPlayerLoadAnalysis.Analyze(context, placements) : [], moved.Length,
            moved.Count(p => request.BaselinePlacements![p.MatchId].DayLabel != p.DayLabel));
    }
}

internal static class TournamentPlayerLoadAnalysis
{
    internal static IReadOnlyList<TournamentPlayerDailyLoadForecast> Analyze(GraphSchedulingCandidates context,
        IReadOnlyDictionary<Guid, MatchPlacement> placements, double winProbability = .5)
    {
        var probability = double.IsFinite(winProbability) ? Math.Clamp(winProbability, .01, .99) : .5;
        var appearances = placements.Values.Where(p => context.Paths.ContainsKey(p.MatchId))
            .SelectMany(p => context.Paths[p.MatchId].Select(path => (Placement: p, Path: path)));
        return appearances.GroupBy(x => x.Path.PlayerKey.ToUpperInvariant() + "|" + x.Placement.DayLabel)
            .Select(group =>
            {
                var first = group.First();
                var matches = group.GroupBy(x => x.Placement.MatchId).Select(g =>
                    (Id: g.Key, Paths: (IReadOnlyList<ConditionalPlayerPath>)g.Select(x => x.Path).ToArray())).ToArray();
                var variables = matches.SelectMany(m => m.Paths.SelectMany(p => p.Conditions.Keys)).Distinct().ToArray();
                var maximum = ConditionalPlayerPaths.MaximumAppearances(matches.Select(m => m.Paths).ToArray());
                var confirmed = matches.Count(m => m.Paths.Any(p => p.Conditions.Count == 0));
                var distribution = new Dictionary<int, double>();
                var exact = variables.Length <= 18;
                if (exact)
                {
                    var variableIndex = variables.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
                    for (var mask = 0; mask < (1 << variables.Length); mask++)
                    {
                        var mass = 1d;
                        for (var i = 0; i < variables.Length; i++) mass *= (mask & (1 << i)) != 0 ? probability : 1 - probability;
                        var count = matches.Count(m => m.Paths.Any(p => p.Conditions.All(pair => ((mask & (1 << variableIndex[pair.Key])) != 0) == pair.Value)));
                        distribution[count] = distribution.GetValueOrDefault(count) + mass;
                    }
                }
                return new TournamentPlayerDailyLoadForecast(first.Path.PlayerKey, first.Path.PlayerName, first.Placement.DayLabel,
                    confirmed, maximum, exact ? distribution.Sum(p => p.Key * p.Value) : null,
                    exact ? distribution.Where(p => p.Key >= context.Request.Resources.MaxPlayerMatchesPerDay).Sum(p => p.Value) : null,
                    distribution, exact, matches.Select(m => m.Id).ToArray());
            }).OrderBy(p => p.DayLabel).ThenByDescending(p => p.MaximumCount).ThenBy(p => p.PlayerKey, StringComparer.Ordinal).ToArray();
    }
}
