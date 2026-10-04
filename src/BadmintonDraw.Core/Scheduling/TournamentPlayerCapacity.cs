namespace BadmintonDraw.Core.Scheduling;

internal enum AppearanceProof { Unknown, ProvenBelow, ProvenAtLeast }

// One deterministic budget belongs to one preflight, not to each player. Charges
// precede scans, sorting and copies. Exhaustion is never evidence of infeasibility.
internal sealed class AppearanceProofBudget
{
    internal SchedulingWorkBudget WorkBudget { get; }
    internal SchedulingRunPhase Phase { get; }
    internal bool Exhausted { get; private set; }
    internal AppearanceProofBudget(int workUnits)
        : this(new SchedulingWorkBudget(new() { PreflightWorkUnits = Math.Max(0, workUnits) }, default), SchedulingRunPhase.Preflight) { }

    internal AppearanceProofBudget(SchedulingWorkBudget workBudget, SchedulingRunPhase phase)
    {
        WorkBudget = workBudget;
        Phase = phase;
    }

    internal void MarkExhausted() => Exhausted = true;

    internal bool TrySpend(long units)
    {
        if (Exhausted || !WorkBudget.TrySpend(Phase, units)) { Exhausted = true; return false; }
        return true;
    }
}

internal static class TournamentPlayerCapacity
{
    internal const int DefaultWorkUnits = 100_000;
    internal const int MaximumProofGroups = 256;

    internal static long CalculatePlayerMatchCapacity(IReadOnlyList<ScheduleDaySettings> days, int durationMinutes, int restMinutes, int dailyCap)
    {
        var slot = checked(((long)durationMinutes + restMinutes) * TimeSpan.TicksPerMinute);
        var rest = checked((long)restMinutes * TimeSpan.TicksPerMinute);
        long capacity = 0;
        foreach (var day in days)
            capacity = checked(capacity + Math.Min((long)dailyCap, checked((day.DayEnd - day.DayStart).Ticks + rest) / slot));
        return capacity;
    }

    internal static SchedulingPreflightResult Check(GraphSchedulingCandidates context, SchedulingWorkBudget budget)
    {
        SchedulingPreflightResult Unknown() => new(SchedulingPreflightStatus.Unknown, null, []);
        SchedulingPreflightResult Proven(SchedulingCapacityEvidence evidence, SchedulingConstraintCode code, string message) =>
            new(SchedulingPreflightStatus.ProvenInfeasible, evidence, [new(code, null, null, null, message)]);
        // Reserve and execute the cheap resource proof before conditional player work.
        // Even copying a contradiction's witness is charged before publishing it.
        long requiredTicks = 0, availableTicks = 0;
        foreach (var duration in context.Durations.Values)
        {
            if (!budget.TrySpend(SchedulingRunPhase.Preflight)) return Unknown();
            requiredTicks = SchedulingCapacityArithmetic.Add(requiredTicks, (long)duration * TimeSpan.TicksPerMinute);
        }
        foreach (var day in context.Days)
        {
            if (!budget.TrySpend(SchedulingRunPhase.Preflight, 1L + (day.UnavailableCourtWindows?.Count ?? 0))) return Unknown();
            if (!budget.TrySpend(SchedulingRunPhase.Preflight, SchedulingCapacityArithmetic.DayIntegrationWork(day))) return Unknown();
            availableTicks = SchedulingCapacityArithmetic.Add(availableTicks, ScheduleResourceCalculator.CalculateDayCapacityTicks(context.Request.Resources, day));
        }
        if (requiredTicks > availableTicks)
        {
            if (!budget.TrySpend(SchedulingRunPhase.Preflight, context.Nodes.Count)) return Unknown();
            return Proven(new("ResourceTime", null, null, requiredTicks, availableTicks, context.Nodes.Keys.ToArray(), new Dictionary<Guid, bool>()),
                SchedulingConstraintCode.RefereeCapacity, "全部比赛的预计时长超过所有比赛日的场地和裁判积分容量。");
        }
        var players = new Dictionary<string, Dictionary<Guid, List<ConditionalPlayerPath>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in context.Paths)
        {
            if (!budget.TrySpend(SchedulingRunPhase.Preflight)) return Unknown();
            foreach (var path in match.Value)
            {
                if (!budget.TrySpend(SchedulingRunPhase.Preflight)) return Unknown();
                if (!players.TryGetValue(path.PlayerKey, out var matches)) players[path.PlayerKey] = matches = [];
                if (!matches.TryGetValue(match.Key, out var alternatives)) matches[match.Key] = alternatives = [];
                alternatives.Add(path);
            }
        }
        var dailyCapacity = checked((long)context.Days.Length * context.Request.Resources.MaxPlayerMatchesPerDay);
        var unknown = false;
        foreach (var player in players)
        {
            if (!budget.TrySpend(SchedulingRunPhase.Preflight, player.Value.Count + context.Days.Length + 1L)) return Unknown();
            var duration = player.Value.Keys.Min(id => context.Durations[id]);
            var capacity = CalculatePlayerMatchCapacity(context.Days, duration, context.Request.Resources.MinimumRestMinutes,
                context.Request.Resources.MaxPlayerMatchesPerDay);
            if (player.Value.Count <= capacity) continue;
            // The group count bounds capacity before adding/casting; even int.MaxValue caps are safe.
            var proof = ConditionalAppearanceProof.Prove(player.Value.Select(pair => new AppearanceGroup(pair.Key, pair.Value)).ToArray(),
                (int)(capacity + 1), budget, SchedulingRunPhase.Preflight);
            if (proof.LowerBound > capacity)
            {
                var daily = proof.LowerBound > dailyCapacity;
                // Rest constrains the whole-match count; evidence scales that count by the
                // minimum occupied match duration, rather than multiplying huge rest values.
                var matchTicks = checked((long)duration * TimeSpan.TicksPerMinute);
                var required = daily ? proof.LowerBound : SchedulingCapacityArithmetic.Multiply(proof.LowerBound, matchTicks);
                var upper = daily ? dailyCapacity : SchedulingCapacityArithmetic.Multiply(capacity, matchTicks);
                if (required <= upper) { unknown = true; continue; } // Saturation cannot fabricate a contradiction.
                var evidence = new SchedulingCapacityEvidence(daily ? "PlayerDailyCap" : "PlayerTime", player.Key,
                    player.Value.Values.First()[0].PlayerName, required, upper, proof.Witness.MatchIds, proof.Witness.Conditions);
                var message = daily
                    ? $"选手 {evidence.PlayerName} 存在至少 {proof.LowerBound} 场可同时成立的参赛路径；{context.Days.Length} 个比赛日 × 每日 {context.Request.Resources.MaxPlayerMatchesPerDay} 场仅允许 {dailyCapacity} 场，无法保障全部合法赛果路径。"
                    : $"选手 {evidence.PlayerName} 存在至少 {proof.LowerBound} 场兼容参赛路径；按最短比赛时长 {duration} 分钟、休息 {context.Request.Resources.MinimumRestMinutes} 分钟和每日上限，全部比赛日最多容纳 {capacity} 场。";
                return Proven(evidence, daily ? SchedulingConstraintCode.DailyMatchLimit : SchedulingConstraintCode.MinimumRest, message);
            }
            unknown |= proof.BudgetExhausted || proof.UpperBound > capacity;
        }
        return new(unknown ? SchedulingPreflightStatus.Unknown : SchedulingPreflightStatus.NoContradictionFound, null, []);
    }

    internal static SchedulingViolation? FindProvenOverload(GraphSchedulingCandidates context, AppearanceProofBudget budget)
    {
        var capacity = (long)context.Days.Length * context.Request.Resources.MaxPlayerMatchesPerDay;
        if (context.Nodes.Count <= capacity) return null;
        var players = new Dictionary<string, Dictionary<Guid, List<ConditionalPlayerPath>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in context.Paths)
        {
            if (!budget.TrySpend(1)) return null;
            foreach (var path in match.Value)
            {
                if (!budget.TrySpend(1)) return null;
                if (!players.TryGetValue(path.PlayerKey, out var matches)) players[path.PlayerKey] = matches = [];
                if (!matches.TryGetValue(match.Key, out var alternatives)) matches[match.Key] = alternatives = [];
                alternatives.Add(path);
            }
        }
        foreach (var player in players)
        {
            if (!budget.TrySpend(1)) return null;
            var count = player.Value.Count;
            if (count <= capacity || count > MaximumProofGroups) continue;
            // The cast is safe only after the distinct-group count bounds the threshold.
            var target = (int)(capacity + 1);
            if (!budget.TrySpend(count)) return null;
            var matches = player.Value.Values.Cast<IReadOnlyList<ConditionalPlayerPath>>().ToArray();
            var proof = ConditionalPlayerPaths.ProveAtLeast(matches, target, budget);
            if (proof == AppearanceProof.ProvenAtLeast)
                return new(SchedulingConstraintCode.DailyMatchLimit, null, null, null,
                    $"选手 {player.Key} 存在至少 {target} 场可同时成立的参赛路径；{context.Days.Length} 个比赛日 × 每日 {context.Request.Resources.MaxPlayerMatchesPerDay} 场仅允许 {capacity} 场，无法保障全部合法赛果路径。");
            if (budget.Exhausted) return null;
        }
        return null;
    }
}
