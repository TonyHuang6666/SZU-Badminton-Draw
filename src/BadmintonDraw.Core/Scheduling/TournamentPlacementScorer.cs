using BadmintonDraw.Core.Matches;

namespace BadmintonDraw.Core.Scheduling;

internal sealed class TournamentPlacementScorer(GraphSchedulingCandidates context)
{
    private readonly Dictionary<string, int> capacities = context.Days.ToDictionary(d => d.DayLabel,
        d => ScheduleResourceCalculator.CalculateDayCapacityMinutes(context.Request.Resources, d));
    private bool Compact => context.Request.Policy.Strategy == ScheduleAutoSchedulingStrategy.Compact;
    internal long Score(MatchNode node, MatchPlacement placement, IReadOnlyDictionary<Guid, MatchPlacement> placements)
    {
        var policy = context.Request.Policy;
        // The validated candidate already owns this day's canonical label. Reuse it
        // in the usage scan instead of formatting DateOnly for every existing match.
        var dayLabel = placement.DayLabel;
        var dayIndex = context.DayIndexes[dayLabel];
        var day = context.Days[dayIndex];
        var minute = (int)(placement.StartTime - day.DayStart).TotalMinutes;
        long score = Compact ? dayIndex * 100_000L + minute * 10L : dayIndex * 50L + minute;
        if (context.Request.BaselinePlacements?.TryGetValue(node.Id, out var baseline) == true)
        {
            score = 0; // Absolute early-finish scoring applies only when there is no real baseline.
            // Baseline dates/courts may have disappeared after resource edits. Their actual
            // dates still measure displacement; they never constrain unlocked placements.
            if (DateOnly.TryParseExact(baseline.DayLabel, "yyyy-MM-dd", out var originalDate))
            {
                var originalMinute = (long)originalDate.DayNumber * 1440 + baseline.StartTime.ToTimeSpan().TotalMinutes;
                score += (long)Math.Round(Math.Abs(context.Minute(placement) - originalMinute) * (Compact ? 10 : 4));
                score += Math.Abs(day.Date.DayNumber - originalDate.DayNumber) * (Compact ? 10_000L : 2_500L);
            }
            if (!string.Equals(baseline.Court, placement.Court, StringComparison.OrdinalIgnoreCase)) score += 100;
        }
        var capacity = capacities[dayLabel];
        var totalMinutes = context.Durations.Values.Sum();
        var target = policy.DayLoadTargets.FirstOrDefault(t => t.DayLabel == dayLabel);
        var targetRatio = target?.TargetUtilization ?? (Compact ? .95 : Math.Min(.8, totalMinutes / (double)Math.Max(1, capacities.Values.Sum())));
        var warningRatio = target?.WarningUtilization ?? Math.Min(1, targetRatio + .15);
        var usage = placements.Values.Where(p => p.DayLabel == dayLabel).Sum(p => (p.EndTime - p.StartTime).TotalMinutes) + context.Durations[node.Id];
        score += (long)Math.Round(Math.Pow(Math.Max(0, usage - capacity * targetRatio), 2) * (Compact ? .15 : 1.8) +
            Math.Pow(Math.Max(0, usage - capacity * warningRatio), 2) * (Compact ? 1 : 8));
        if (policy.SynchronizeStageWaves && !Compact)
        {
            var maxDepth = context.Nodes.Values.Where(n => n.ProjectId == node.ProjectId).Max(n => context.Depths[n.Id]);
            var progress = (context.Depths[node.Id] + 1d) / (maxDepth + 1);
            var desired = Array.FindIndex(context.Days, d => progress <= (policy.StageWaveTargets.FirstOrDefault(t => t.DayLabel == d.DayLabel)?.CumulativeProgress ?? (Array.IndexOf(context.Days, d) + 1d) / context.Days.Length));
            if (desired < 0) desired = context.Days.Length - 1;
            score += dayIndex < desired ? (desired - dayIndex) * 45_000L : (dayIndex - desired) * 9_000L;
        }
        var category = Category(node);
        var preference = policy.FinalDayRules.FirstOrDefault(r => r.ProjectId == node.ProjectId && r.Category == category)?.Preference
            ?? (policy.Strategy == ScheduleAutoSchedulingStrategy.FinalsDayFriendly && category is not null ? TournamentFinalDayPreference.PreferFinalDay : TournamentFinalDayPreference.Flexible);
        var finalDay = dayIndex == context.Days.Length - 1;
        score += preference switch
        {
            TournamentFinalDayPreference.StronglyPreferFinalDay => finalDay ? -50_000 : 700_000,
            TournamentFinalDayPreference.PreferFinalDay => finalDay ? -30_000 : 90_000,
            TournamentFinalDayPreference.AvoidFinalDay => finalDay ? 80_000 : -5_000,
            _ => 0
        };
        return score;
    }

    internal TournamentFinalDayMatchCategory? Category(MatchNode node)
    {
        if (node.IsChampionshipFinal) return TournamentFinalDayMatchCategory.Final;
        if (node.IsChampionshipBracket && node.KnockoutEntrantCount == 4) return TournamentFinalDayMatchCategory.Semifinal;
        if (!node.IsPlacementPlayoff) return null;
        return node.SideA is EntrantSource.LoserOf a && node.SideB is EntrantSource.LoserOf b &&
            context.Nodes[a.MatchId].IsChampionshipBracket && context.Nodes[a.MatchId].KnockoutEntrantCount == 4 &&
            context.Nodes[b.MatchId].IsChampionshipBracket && context.Nodes[b.MatchId].KnockoutEntrantCount == 4
            ? TournamentFinalDayMatchCategory.Bronze : TournamentFinalDayMatchCategory.Placement5To8;
    }

    internal static TournamentSchedulingPolicy ResolveEffectivePolicy(GraphSchedulingCandidates context)
    {
        var requested = context.Request.Policy;
        var capacities = context.Days.ToDictionary(d => d.DayLabel, d => ScheduleResourceCalculator.CalculateDayCapacityMinutes(context.Request.Resources, d));
        var ratio = requested.Strategy == ScheduleAutoSchedulingStrategy.Compact ? .95 :
            Math.Min(.8, context.Durations.Values.Sum() / (double)Math.Max(1, capacities.Values.Sum()));
        var loadTargets = context.Days.Select(d => requested.DayLoadTargets.FirstOrDefault(t => t.DayLabel == d.DayLabel)
            ?? new TournamentDayLoadTarget(d.DayLabel, ratio, Math.Min(1, ratio + .15))).ToArray();
        var waves = requested.SynchronizeStageWaves
            ? context.Days.Select((d, i) => requested.StageWaveTargets.FirstOrDefault(t => t.DayLabel == d.DayLabel)
                ?? new TournamentStageWaveTarget(d.DayLabel, Math.Clamp((i + 1d) / context.Days.Length,
                    context.Days.Take(i).Select(day => requested.StageWaveTargets.FirstOrDefault(t => t.DayLabel == day.DayLabel)?.CumulativeProgress).OfType<double>().DefaultIfEmpty(0).Max(),
                    context.Days.Skip(i + 1).Select(day => requested.StageWaveTargets.FirstOrDefault(t => t.DayLabel == day.DayLabel)?.CumulativeProgress).OfType<double>().DefaultIfEmpty(1).Min()))).ToArray()
            : requested.StageWaveTargets;
        var rules = context.Request.MatchGraphs.SelectMany(g => Enum.GetValues<TournamentFinalDayMatchCategory>().Select(category =>
            requested.FinalDayRules.FirstOrDefault(r => r.ProjectId == g.ProjectId && r.Category == category) ??
            new TournamentFinalDayRule(g.ProjectId, category, requested.Strategy == ScheduleAutoSchedulingStrategy.FinalsDayFriendly
                ? TournamentFinalDayPreference.PreferFinalDay : TournamentFinalDayPreference.Flexible))).ToArray();
        return requested with { DayLoadTargets = loadTargets, StageWaveTargets = waves, FinalDayRules = rules };
    }
}
