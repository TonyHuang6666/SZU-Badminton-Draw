using BadmintonDraw.Core.Matches;

namespace BadmintonDraw.Core.Scheduling;

internal readonly record struct SchedulingCandidateScore(long ExplicitPreference, decimal LoadDelta, long Secondary)
    : IComparable<SchedulingCandidateScore>
{
    public int CompareTo(SchedulingCandidateScore other)
    {
        var explicitPreference = ExplicitPreference.CompareTo(other.ExplicitPreference);
        if (explicitPreference != 0) return explicitPreference;
        var load = LoadDelta.CompareTo(other.LoadDelta);
        return load != 0 ? load : Secondary.CompareTo(other.Secondary);
    }
}

internal sealed class TournamentPlacementScorer
{
    private readonly GraphSchedulingCandidates context;
    private readonly Dictionary<string, long> capacityTicks;
    private readonly Dictionary<string, int> capacities;

    internal TournamentPlacementScorer(GraphSchedulingCandidates context)
    {
        this.context = context;
        capacityTicks = context.Days.ToDictionary(d => d.DayLabel,
            d => ScheduleResourceCalculator.CalculateDayCapacityTicks(context.Request.Resources, d));
        capacities = capacityTicks.ToDictionary(p => p.Key,
            p => (int)Math.Min(int.MaxValue, p.Value / TimeSpan.TicksPerMinute));
    }
    private bool Compact => context.Request.Policy.Strategy == ScheduleAutoSchedulingStrategy.Compact;

    internal SchedulingCandidateScore Rank(MatchNode node, MatchPlacement placement, TournamentSearchState state)
    {
        if (context.Request.Policy.Strategy != ScheduleAutoSchedulingStrategy.BalancedRelaxed)
            return new(Score(node, placement, state.Placements), 0, 0);
        var duration = (decimal)context.Durations[node.Id] * TimeSpan.TicksPerMinute;
        var used = (decimal)state.UsedTicksByDay.GetValueOrDefault(placement.DayLabel);
        var explicitPreference = ExplicitNodeScore(node, placement) +
            ExplicitLoadPenalty(placement.DayLabel, used + duration) - ExplicitLoadPenalty(placement.DayLabel, used);
        return new(explicitPreference, ScoreBalancedDelta(node, placement, state), SecondaryScore(node, placement));
    }

    internal SchedulingCandidateScore RankForCourt(SchedulingCandidateScore rank, Guid matchId, string fromCourt, string toCourt)
    {
        var delta = CourtPenalty(matchId, toCourt) - CourtPenalty(matchId, fromCourt);
        return context.Request.Policy.Strategy == ScheduleAutoSchedulingStrategy.BalancedRelaxed
            ? rank with { Secondary = rank.Secondary + delta }
            : rank with { ExplicitPreference = rank.ExplicitPreference + delta };
    }

    private long CourtPenalty(Guid matchId, string court) => context.Request.BaselinePlacements?.TryGetValue(matchId, out var baseline) == true &&
        !string.Equals(baseline.Court, court, StringComparison.OrdinalIgnoreCase) ? 100 : 0;

    internal decimal ScoreBalancedDelta(MatchNode node, MatchPlacement placement, TournamentSearchState state)
    {
        var capacity = capacityTicks[placement.DayLabel];
        if (capacity <= 0) return decimal.MaxValue;
        var duration = (decimal)context.Durations[node.Id] * TimeSpan.TicksPerMinute;
        var gap = state.UsedTicksByDay.GetValueOrDefault(placement.DayLabel) - TargetTicks(placement.DayLabel);
        // Divide before multiplying: retain signed improvements and avoid squaring long ticks.
        return (2 * gap / capacity + duration / capacity) * duration;
    }

    internal SchedulingCandidateScore ScheduleRank(TournamentSearchState state)
    {
        long explicitPreference = 0, secondary = 0;
        decimal load = 0;
        foreach (var day in context.Days)
        {
            var capacity = capacityTicks[day.DayLabel];
            if (capacity <= 0) continue;
            var used = (decimal)state.UsedTicksByDay.GetValueOrDefault(day.DayLabel);
            var gap = used - TargetTicks(day.DayLabel);
            load += gap / capacity * gap;
            explicitPreference += ExplicitLoadPenalty(day.DayLabel, used);
        }
        foreach (var placement in state.Placements.Values)
        {
            var node = context.Nodes[placement.MatchId];
            explicitPreference += ExplicitNodeScore(node, placement);
            secondary += SecondaryScore(node, placement);
        }
        return new(explicitPreference, load, secondary);
    }

    private decimal TargetTicks(string dayLabel)
    {
        var totalCapacity = capacityTicks.Values.Sum(c => (decimal)c);
        return totalCapacity <= 0 ? 0 : context.Durations.Values.Sum(d => (decimal)d * TimeSpan.TicksPerMinute) *
            (capacityTicks[dayLabel] / totalCapacity);
    }

    private long ExplicitLoadPenalty(string dayLabel, decimal usedTicks)
    {
        var target = context.RequestedPolicy.DayLoadTargets.FirstOrDefault(t => t.DayLabel == dayLabel);
        if (target is null) return 0;
        var capacity = capacityTicks[dayLabel] / (decimal)TimeSpan.TicksPerMinute;
        var used = usedTicks / TimeSpan.TicksPerMinute;
        var targetExcess = Math.Max(0, used - capacity * (decimal)target.TargetUtilization);
        var warningExcess = Math.Max(0, used - capacity * (decimal)target.WarningUtilization);
        return (long)decimal.Round(targetExcess * targetExcess * 1.8m + warningExcess * warningExcess * 8m);
    }

    private long ExplicitNodeScore(MatchNode node, MatchPlacement placement)
    {
        var requested = context.RequestedPolicy;
        var stage = requested.SynchronizeStageWaves && requested.StageWaveTargets.Count > 0
            ? StageScore(node, placement, context.Request.Policy) : 0;
        var preference = requested.FinalDayRules.FirstOrDefault(r => r.ProjectId == node.ProjectId && r.Category == Category(node))?.Preference
            ?? TournamentFinalDayPreference.Flexible;
        var finalDay = context.DayIndexes[placement.DayLabel] == context.Days.Length - 1;
        return stage + FinalPreferenceScore(preference, finalDay);
    }

    private static long FinalPreferenceScore(TournamentFinalDayPreference preference, bool finalDay) => preference switch
    {
        TournamentFinalDayPreference.StronglyPreferFinalDay => finalDay ? -50_000 : 700_000,
        TournamentFinalDayPreference.PreferFinalDay => finalDay ? -30_000 : 90_000,
        TournamentFinalDayPreference.AvoidFinalDay => finalDay ? 80_000 : -5_000,
        _ => 0
    };

    private long StageScore(MatchNode node, MatchPlacement placement, TournamentSchedulingPolicy policy)
    {
        var maxDepth = context.Nodes.Values.Where(n => n.ProjectId == node.ProjectId).Max(n => context.Depths[n.Id]);
        var progress = (context.Depths[node.Id] + 1d) / (maxDepth + 1);
        var desired = Array.FindIndex(context.Days, d => progress <=
            (policy.StageWaveTargets.FirstOrDefault(t => t.DayLabel == d.DayLabel)?.CumulativeProgress ??
                (context.DayIndexes[d.DayLabel] + 1d) / context.Days.Length));
        if (desired < 0) desired = context.Days.Length - 1;
        var dayIndex = context.DayIndexes[placement.DayLabel];
        return dayIndex < desired ? (desired - dayIndex) * 45_000L : (dayIndex - desired) * 9_000L;
    }

    private long SecondaryScore(MatchNode node, MatchPlacement placement)
    {
        var dayIndex = context.DayIndexes[placement.DayLabel];
        var day = context.Days[dayIndex];
        long score = dayIndex * 50L + (int)(placement.StartTime - day.DayStart).TotalMinutes;
        if (context.Request.BaselinePlacements?.TryGetValue(node.Id, out var baseline) == true)
        {
            score = 0;
            if (DateOnly.TryParseExact(baseline.DayLabel, "yyyy-MM-dd", out var originalDate))
            {
                var originalMinute = (long)originalDate.DayNumber * 1440 + baseline.StartTime.ToTimeSpan().TotalMinutes;
                score += (long)Math.Round(Math.Abs(context.Minute(placement) - originalMinute) * 4);
                score += Math.Abs(day.Date.DayNumber - originalDate.DayNumber) * 2_500L;
            }
            score += CourtPenalty(node.Id, placement.Court);
        }
        if (context.Request.Policy.SynchronizeStageWaves && context.RequestedPolicy.StageWaveTargets.Count == 0)
            score += StageScore(node, placement, context.Request.Policy);
        return score;
    }
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
            score += CourtPenalty(node.Id, placement.Court);
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
            score += StageScore(node, placement, policy);
        var category = Category(node);
        var preference = policy.FinalDayRules.FirstOrDefault(r => r.ProjectId == node.ProjectId && r.Category == category)?.Preference
            ?? (policy.Strategy == ScheduleAutoSchedulingStrategy.FinalsDayFriendly && category is not null ? TournamentFinalDayPreference.PreferFinalDay : TournamentFinalDayPreference.Flexible);
        var finalDay = dayIndex == context.Days.Length - 1;
        score += FinalPreferenceScore(preference, finalDay);
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
