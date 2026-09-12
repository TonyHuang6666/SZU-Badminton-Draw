namespace BadmintonDraw.Core.Scheduling;

/// <summary>One deterministic global search over time-free graph nodes. Failure never contains a partial schedule.</summary>
public sealed class TournamentScheduler
{
    public TournamentSchedulingResult Generate(TournamentSchedulingRequest request)
    {
        var validator = new TournamentPlacementValidator(request);
        var context = validator.Context;
        var placements = new Dictionary<Guid, MatchPlacement>();
        if (!validator.ValidateInput().IsValid) return Fail(context, placements, context.InputViolations);
        // Persist exactly the defaults actually used, so setup, audit and regeneration can
        // display the policy without reconstructing hidden algorithm decisions.
        request = request with { Policy = TournamentPlacementScorer.ResolveEffectivePolicy(context) };
        validator = new TournamentPlacementValidator(request);
        context = validator.Context;
        if (!validator.ValidateInput().IsValid) return Fail(context, placements, context.InputViolations);
        foreach (var id in context.LockedIds) placements[id] = request.BaselinePlacements![id];
        // Locked descendants can have unlocked predecessors: missing predecessors are checked
        // as they are placed and by the final gate, while all other locked conflicts fail now.
        var lockedIssues = validator.ValidateSchedule(placements, false).Violations.Where(v => v.Code != SchedulingConstraintCode.MissingPlacement).ToArray();
        if (lockedIssues.Length > 0) return Fail(context, placements, lockedIssues);
        var scorer = new TournamentPlacementScorer(context);
        var pending = context.Nodes.Keys.Except(placements.Keys).ToHashSet();
        var degree = context.Nodes.Keys.ToDictionary(id => id, id => context.Nodes.Keys.Count(other => other != id &&
            ConditionalPlayerPaths.SharesPlayer(context.Paths[id], context.Paths[other])));
        var projectOrder = request.MatchGraphs.Select((g, i) => (g.ProjectId, i)).ToDictionary(x => x.ProjectId, x => x.i);
        while (pending.Count > 0)
        {
            var next = pending.Select(id => context.Nodes[id])
                .Where(n => n.Dependencies.All(placements.ContainsKey) && (!n.IsChampionshipFinal ||
                    !pending.Any(id => context.Nodes[id].ProjectId == n.ProjectId && context.Nodes[id].IsPlacementPlayoff)))
                .OrderByDescending(n => n.SameUnit && n.KnockoutEntrantCount is null).ThenByDescending(n => degree[n.Id])
                .ThenBy(n => context.Depths[n.Id]).ThenBy(n => projectOrder[n.ProjectId]).ThenBy(n => n.Order).ThenBy(n => n.Id).FirstOrDefault();
            if (next is null) return Fail(context, placements, pending.Select(id => new SchedulingViolation(
                SchedulingConstraintCode.MissingPlacement, context.Nodes[id].ProjectId, id, null, "未安排的来源或名次赛阻塞了后续场次。")).ToArray());
            MatchPlacement? best = null;
            long bestScore = long.MaxValue;
            var rejections = new Dictionary<SchedulingConstraintCode, SchedulingViolation>();
            foreach (var day in context.Days)
            foreach (var start in CandidateStarts(day, next.Id, context, placements))
            foreach (var court in day.Courts)
            {
                var proposal = new MatchPlacement(next.Id, day.DayLabel, start, start.AddMinutes(context.Durations[next.Id]), court);
                // Scoring is pure for this unchanged partial schedule. A candidate that
                // cannot beat an already validated best cannot change the result, including
                // first-in-traversal ties; avoid repeating the full hard gate for it.
                var score = scorer.Score(next, proposal, placements);
                if (best is not null && score >= bestScore) continue;
                var valid = validator.ValidatePlacement(proposal, placements);
                if (!valid.IsValid) { foreach (var issue in valid.Violations) rejections.TryAdd(issue.Code, issue); continue; }
                if (score >= bestScore) continue;
                best = proposal; bestScore = score;
            }
            if (best is null)
            {
                rejections.TryAdd(SchedulingConstraintCode.SearchExhausted, new(SchedulingConstraintCode.SearchExhausted, next.ProjectId, next.Id, null, "本次搜索未找到合法位置。"));
                return Fail(context, placements, rejections.Values.ToArray());
            }
            placements[next.Id] = best;
            pending.Remove(next.Id);
        }
        var final = validator.ValidateSchedule(placements);
        if (!final.IsValid) return Fail(context, placements, final.Violations);
        placements = TournamentScheduleSpreader.Spread(validator, placements);
        var revisions = request.MatchGraphs.ToDictionary(g => g.ProjectId, g => g.Revision);
        var schedule = new TournamentSchedule(placements, request.Resources, request.Policy, revisions, request.ScheduleRevision);
        return new TournamentSchedulingResult.Success(schedule, schedule.GraphRevisions, new TournamentScheduleQualityAnalyzer().Analyze(request, schedule.Placements));
    }

    private static IEnumerable<TimeOnly> CandidateStarts(ScheduleDaySettings day, Guid id, GraphSchedulingCandidates context,
        IReadOnlyDictionary<Guid, MatchPlacement> placements)
    {
        var duration = context.Durations[id];
        var startMinute = day.DayStart.ToTimeSpan().TotalMinutes;
        var latest = day.DayEnd.ToTimeSpan().TotalMinutes - duration;
        var starts = new HashSet<TimeOnly>();
        for (var minute = startMinute; minute <= latest; minute++) starts.Add(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute)));
        void Add(TimeOnly time, int offset = 0)
        {
            var minute = time.ToTimeSpan().TotalMinutes + offset;
            if (minute >= startMinute && minute <= latest) starts.Add(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute)));
        }
        if (context.Request.BaselinePlacements?.TryGetValue(id, out var baseline) == true && baseline.DayLabel == day.DayLabel) Add(baseline.StartTime);
        foreach (var p in placements.Values.Where(p => p.DayLabel == day.DayLabel))
        {
            Add(p.EndTime); Add(p.EndTime, context.Request.Resources.MinimumRestMinutes);
            Add(p.StartTime, -duration); Add(p.StartTime, -duration - context.Request.Resources.MinimumRestMinutes);
        }
        foreach (var window in day.RefereeCapacityWindows ?? []) { Add(window.EndTime); Add(window.StartTime, -duration); }
        foreach (var block in day.UnavailableCourtWindows ?? []) { Add(block.EndTime); Add(block.StartTime, -duration); }
        Add(day.DayEnd, -duration);
        return starts.Order();
    }

    private static TournamentSchedulingResult.Failure Fail(GraphSchedulingCandidates context,
        IReadOnlyDictionary<Guid, MatchPlacement> placements, IReadOnlyList<SchedulingViolation> issues)
    {
        var affected = issues.Select(v => v.MatchId).OfType<Guid>().ToHashSet();
        var blocked = context.Nodes.Values.Where(n => !placements.ContainsKey(n.Id) || affected.Contains(n.Id))
            .Select(n => new SchedulingBlockedMatch(n.ProjectId, context.Request.ProjectNames.GetValueOrDefault(n.ProjectId, n.ProjectId.ToString()), n.Id, n.DisplayName,
                issues.Where(v => v.MatchId == n.Id || v.MatchId is null).Select(v => v.Code).DefaultIfEmpty(SchedulingConstraintCode.MissingPlacement).Distinct().ToArray())).ToArray();
        var capacity = context.Days.Where(d => d.DayStart < d.DayEnd).Select(d => new SchedulingDayCapacity(d.DayLabel,
            ScheduleResourceCalculator.CalculateDayCapacityMinutes(context.Request.Resources, d),
            placements.Values.Where(p => p.DayLabel == d.DayLabel).Sum(p => (int)(p.EndTime - p.StartTime).TotalMinutes))).ToArray();
        return new(new("本次搜索未找到满足全部约束的完整赛程；这不表示所有编排方案都不可能。", blocked, issues, capacity,
            ["增加比赛日或延长每日可用时段。", "增加可用场地或裁判，并检查不可用时段。", "核对预计时长、锁定位置、最短休息和每日上限。"]));
    }
}
