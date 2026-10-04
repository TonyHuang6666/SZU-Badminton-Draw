namespace BadmintonDraw.Core.Scheduling;

/// <summary>
/// Shared hard gate for generation, board moves, cascades and quality analysis. Build from the
/// captured graph/resource/policy/result snapshot. ValidatePlacement requires all predecessors,
/// checks existing successors, and expects the candidate removed from otherPlacements.
/// ValidateSchedule is the final acceptance gate for an entire proposed edit; it never mutates input.
/// </summary>
public sealed partial class TournamentPlacementValidator
{
    internal GraphSchedulingCandidates Context { get; }
    public TournamentPlacementValidator(TournamentSchedulingRequest request) => Context = new(request);
    private TournamentPlacementValidator(GraphSchedulingCandidates context, bool reuseContext) => Context = context;
    internal static TournamentPlacementValidator FromContext(GraphSchedulingCandidates context) => new(context, true);
    public TournamentPlacementValidation ValidateInput() => new(Context.InputViolations);

    public TournamentPlacementValidation ValidatePlacement(MatchPlacement placement,
        IReadOnlyDictionary<Guid, MatchPlacement> otherPlacements)
        => ValidatePlacementCore(placement, otherPlacements, null, null);

    private TournamentPlacementValidation ValidatePlacementCore(MatchPlacement placement,
        IReadOnlyDictionary<Guid, MatchPlacement> otherPlacements, ValidationRun? run, Guid? omittedId)
    {
        run?.Charge();
        if (Context.InputViolations.Count > 0) return ValidateInput();
        var issues = new List<SchedulingViolation>();
        if (!Context.Nodes.TryGetValue(placement.MatchId, out var node))
            return new([new(SchedulingConstraintCode.UnknownMatch, null, placement.MatchId, null, "位置引用了未知比赛。")]);
        void Issue(SchedulingConstraintCode code, string message, Guid? related = null) =>
            issues.Add(new(code, node.ProjectId, node.Id, related, message));
        if (Context.LockedIds.Contains(node.Id) && Context.Request.BaselinePlacements![node.Id] != placement)
            Issue(SchedulingConstraintCode.LockedPlacement, "已完成或锁定场次不能移动。");
        var day = placement.DayLabel is not null && Context.DayIndexes.TryGetValue(placement.DayLabel, out var dayIndex)
            ? Context.Days[dayIndex] : null;
        if (day is null || placement.StartTime < day.DayStart || placement.EndTime > day.DayEnd || placement.EndTime <= placement.StartTime)
        {
            Issue(SchedulingConstraintCode.DayBounds, "位置必须在某个比赛日的时间范围内。");
            return new(issues);
        }
        if ((placement.EndTime - placement.StartTime).TotalMinutes != Context.Durations[node.Id])
            Issue(SchedulingConstraintCode.Duration, "位置时长与项目时长规则不符。");
        run?.Charge(1L + day.Courts.Count);
        foreach (var block in day.UnavailableCourtWindows ?? []) run?.Charge(1L + block.Courts.Count);
        if (!day.Courts.Contains(placement.Court, StringComparer.OrdinalIgnoreCase) ||
            !ScheduleResourceCalculator.IsCourtAvailable(day, placement.Court, placement.StartTime, placement.EndTime))
            Issue(SchedulingConstraintCode.CourtUnavailable, "场地不存在或覆盖了不可用时段。");
        var cacheKey = new TimeValidationKey(placement.MatchId, placement.DayLabel!, placement.StartTime, placement.EndTime, omittedId);
        if (run is not null && run.State.TryGetTimeCheck(cacheKey, out var cached))
        {
            run.Charge(1L + cached.Count);
            issues.AddRange(cached);
        }
        else
        {
            var timeIssues = ValidateTimeAndDay(placement, otherPlacements, run, omittedId);
            run?.Charge(1L + timeIssues.Count);
            run?.State.CacheTimeCheck(cacheKey, timeIssues);
            issues.AddRange(timeIssues);
        }
        var courtOthers = run is null ? otherPlacements.Where(p => p.Key != omittedId && p.Key != placement.MatchId &&
                p.Key == p.Value.MatchId && Context.Nodes.ContainsKey(p.Key) && p.Value.DayLabel is not null && Context.DayIndexes.ContainsKey(p.Value.DayLabel))
                .Select(p => p.Value) : run.State.OnCourt(placement.Court);
        var start = Context.Minute(placement);
        var end = Context.Minute(placement, true);
        foreach (var other in courtOthers)
        {
            run?.Charge();
            if (other.MatchId == omittedId || other.MatchId == placement.MatchId) continue;
            if (start < Context.Minute(other, true) && Context.Minute(other) < end &&
                string.Equals(placement.Court, other.Court, StringComparison.OrdinalIgnoreCase))
                Issue(SchedulingConstraintCode.CourtOverlap, "同一场地有重叠场次。", other.MatchId);
        }
        return new(issues);
    }

    // Both public exact validation and bounded generation execute these same hard rules.
    // This part is independent of the candidate court and can be reused across court choices.
    private IReadOnlyList<SchedulingViolation> ValidateTimeAndDay(MatchPlacement placement,
        IReadOnlyDictionary<Guid, MatchPlacement> otherPlacements, ValidationRun? run, Guid? omittedId)
    {
        var node = Context.Nodes[placement.MatchId];
        var day = Context.Days[Context.DayIndexes[placement.DayLabel]];
        var resources = Context.Request.Resources;
        var issues = new List<SchedulingViolation>();
        void Issue(SchedulingConstraintCode code, string message, Guid? related = null) =>
            issues.Add(new(code, node.ProjectId, node.Id, related, message));
        var validOthers = new List<MatchPlacement>();
        foreach (var pair in otherPlacements)
        {
            run?.Charge();
            if (pair.Key == omittedId) continue;
            if (pair.Key == placement.MatchId) { Issue(SchedulingConstraintCode.PlacementIdentity, "候选比赛必须从已有位置中移除。", pair.Key); continue; }
            if (pair.Key != pair.Value.MatchId || !Context.Nodes.ContainsKey(pair.Key))
            { Issue(SchedulingConstraintCode.PlacementIdentity, "已有位置的比赛身份不一致。", pair.Key); continue; }
            if (pair.Value.DayLabel is null || !Context.DayIndexes.ContainsKey(pair.Value.DayLabel))
            { Issue(SchedulingConstraintCode.DayBounds, "已有位置引用了未知比赛日。", pair.Key); continue; }
            validOthers.Add(pair.Value);
        }
        var start = Context.Minute(placement);
        var end = Context.Minute(placement, true);
        foreach (var id in node.Dependencies)
        {
            run?.Charge(1L + validOthers.Count);
            var dependency = validOthers.FirstOrDefault(p => p.MatchId == id);
            if (dependency is null) Issue(SchedulingConstraintCode.MissingPlacement, "必须先安排全部来源场次。", id);
            else if (Context.Minute(dependency, true) > start) Issue(SchedulingConstraintCode.DependencyOrder, "比赛不能早于来源场次结束。", id);
        }
        foreach (var other in validOthers)
        {
            run?.Charge(1L + Context.Nodes[other.MatchId].Dependencies.Count);
            var otherNode = Context.Nodes[other.MatchId];
            var otherStart = Context.Minute(other);
            var otherEnd = Context.Minute(other, true);
            if (otherNode.Dependencies.Contains(node.Id) && end > otherStart)
                Issue(SchedulingConstraintCode.DependencyOrder, "移动会晚于已排后继场次开始。", other.MatchId);
            if (node.ProjectId == otherNode.ProjectId &&
                ((node.IsChampionshipFinal && otherNode.IsPlacementPlayoff && start < otherStart) ||
                 (node.IsPlacementPlayoff && otherNode.IsChampionshipFinal && start > otherStart)))
                Issue(SchedulingConstraintCode.ChampionshipOrder, "项目名次赛不能在冠军决赛开始后才开始。", other.MatchId);
            var overlap = start < otherEnd && otherStart < end;
            if (!Context.ShareCompatiblePlayer(node.Id, other.MatchId)) continue;
            if (overlap) Issue(SchedulingConstraintCode.PlayerOverlap, "确定或兼容晋级路径的同一选手撞场。", other.MatchId);
            else if (Math.Max(start - otherEnd, otherStart - end) < resources.MinimumRestMinutes)
                Issue(SchedulingConstraintCode.MinimumRest, "确定或兼容晋级路径的休息时间不足。", other.MatchId);
        }
        run?.Charge(1L + validOthers.Count);
        var sameDay = (run is null ? validOthers.Where(p => p.DayLabel == placement.DayLabel) :
            run.State.OnDay(placement.DayLabel).Where(p => p.MatchId != omittedId && p.MatchId != placement.MatchId)).ToArray();
        // Evaluate actual concurrent occupancy on every boundary, not the number of matches
        // touching the candidate's entire interval (which overcounts sequential matches).
        var boundaryCount = 2L * (sameDay.LongLength + (day.RefereeCapacityWindows?.Count ?? 0) + (day.UnavailableCourtWindows?.Count ?? 0) + 1L);
        // Filter and deduplicate every source boundary before reserving ordering work.
        // Disjoint matches/windows still cost a scan, but cannot create candidate segments.
        run?.Charge(1L + boundaryCount);
        var boundaries = sameDay.SelectMany(p => new[] { p.StartTime, p.EndTime })
            .Concat((day.RefereeCapacityWindows ?? []).SelectMany(w => new[] { w.StartTime, w.EndTime }))
            .Concat((day.UnavailableCourtWindows ?? []).SelectMany(w => new[] { w.StartTime, w.EndTime }))
            .Append(placement.StartTime).Append(placement.EndTime)
            .Where(t => t >= placement.StartTime && t <= placement.EndTime).Distinct().ToArray();
        run?.Charge(SchedulingCapacityArithmetic.Multiply(boundaries.LongLength, boundaries.LongLength));
        Array.Sort(boundaries);
        long segmentWork = 1L + sameDay.Length + day.Courts.Count + (day.RefereeCapacityWindows?.Count ?? 0);
        foreach (var block in day.UnavailableCourtWindows ?? [])
        {
            run?.Charge();
            segmentWork = SchedulingCapacityArithmetic.Add(segmentWork, (1L + block.Courts.Count) * day.Courts.Count);
        }
        for (var i = 0; i + 1 < boundaries.Length; i++)
        {
            run?.Charge(segmentWork);
            var a = boundaries[i]; var b = boundaries[i + 1];
            if (sameDay.Count(p => p.StartTime < b && a < p.EndTime) + 1 > ScheduleResourceCalculator.GetConcurrentMatchLimit(day, resources.RefereeCount, a, b))
            { Issue(SchedulingConstraintCode.RefereeCapacity, "同时比赛场数超过该时段的裁判或场地容量。"); break; }
        }
        // No player can appear in more distinct matches than the entire day contains.
        if (sameDay.Length < resources.MaxPlayerMatchesPerDay) return issues.ToArray();
        foreach (var key in Context.PlayerKeys(node.Id))
        {
            run?.Charge();
            IEnumerable<Guid> playerMatchIds;
            if (run is null) playerMatchIds = sameDay.Select(p => p.MatchId);
            else
            {
                run.Charge(1L + key.Length);
                var indexedIds = new List<Guid>();
                foreach (var other in run.State.ForPlayer(key))
                {
                    // The index includes other days; charge before filtering every entry.
                    run.Charge();
                    if (other.DayLabel == placement.DayLabel && other.MatchId != omittedId && other.MatchId != placement.MatchId)
                        indexedIds.Add(other.MatchId);
                }
                // Each indexed match contributes at most one appearance, irrespective of
                // how many conditional paths it has. Expand paths only if the cap is at risk.
                if (indexedIds.Count < resources.MaxPlayerMatchesPerDay) continue;
                playerMatchIds = indexedIds;
            }
            var groups = new List<AppearanceGroup>();
            foreach (var id in playerMatchIds.Append(node.Id))
            {
                run?.Charge(1L + key.Length);
                var paths = Context.PlayerPaths(id, key);
                run?.Charge(paths.Count);
                if (paths.Count > 0) groups.Add(new(id, paths));
            }
            // This comparison also prevents overflowing cap + 1 for int.MaxValue caps.
            if (groups.Count > resources.MaxPlayerMatchesPerDay && ExceedsDailyCap(key, groups, run))
            { Issue(SchedulingConstraintCode.DailyMatchLimit, $"选手 {key} 的兼容每日场次超过全赛事上限。"); break; }
        }
        return issues.ToArray();
    }

    private bool ExceedsDailyCap(string player, IReadOnlyList<AppearanceGroup> groups, ValidationRun? run)
    {
        var cap = Context.Request.Resources.MaxPlayerMatchesPerDay;
        if (run is null) return ConditionalPlayerPaths.MaximumAppearances(groups.Select(g => g.Alternatives).ToArray(), cap + 1) > cap;
        run.Charge(SchedulingCapacityArithmetic.Add(player.Length + 1L, groups.Count * (long)groups.Count));
        var key = new AppearanceCacheKey(player, groups.Select(g => g.MatchId).Order().ToArray());
        if (Context.TryGetAppearanceCheck(key, out var cached)) return cached;
        var proof = ConditionalAppearanceProof.Prove(groups, cap + 1, run.Budget, run.Phase);
        Context.CacheAppearanceBounds(key, proof);
        bool exceeds;
        if (proof.LowerBound > cap) exceeds = true;
        else if (proof.UpperBound <= cap) exceeds = false;
        else throw new ValidationInterruptedException();
        run.Charge(1L + groups.Count + player.Length);
        Context.CacheAppearanceCheck(key, exceeds);
        return exceeds;
    }

    public TournamentPlacementValidation ValidateSchedule(IReadOnlyDictionary<Guid, MatchPlacement> placements, bool requireComplete = true)
        => ValidateScheduleCore(placements, requireComplete, null);

    private TournamentPlacementValidation ValidateScheduleCore(IReadOnlyDictionary<Guid, MatchPlacement> placements,
        bool requireComplete, ValidationRun? run)
    {
        run?.Charge();
        if (Context.InputViolations.Count > 0) return ValidateInput();
        var issues = new List<SchedulingViolation>();
        foreach (var pair in placements)
        {
            run?.Charge();
            if (pair.Key != pair.Value.MatchId)
                issues.Add(new(SchedulingConstraintCode.PlacementIdentity, null, pair.Key, pair.Value.MatchId, "位置字典键与比赛身份不符。"));
            issues.AddRange(ValidatePlacementCore(pair.Value, placements, run, pair.Key).Violations);
        }
        run?.Charge(1L + Context.Nodes.Count);
        foreach (var node in Context.Nodes.Values.Where(n => !placements.ContainsKey(n.Id) && (requireComplete || Context.LockedIds.Contains(n.Id))))
            issues.Add(new(SchedulingConstraintCode.MissingPlacement, node.ProjectId, node.Id, null, "赛程缺少必须安排的场次。"));
        run?.Charge(1L + issues.Count);
        return new(issues.Distinct().ToArray());
    }
}
