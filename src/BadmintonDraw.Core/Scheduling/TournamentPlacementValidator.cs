namespace BadmintonDraw.Core.Scheduling;

/// <summary>
/// Shared hard gate for generation, board moves, cascades and quality analysis. Build from the
/// captured graph/resource/policy/result snapshot. ValidatePlacement requires all predecessors,
/// checks existing successors, and expects the candidate removed from otherPlacements.
/// ValidateSchedule is the final acceptance gate for an entire proposed edit; it never mutates input.
/// </summary>
public sealed class TournamentPlacementValidator
{
    internal GraphSchedulingCandidates Context { get; }
    public TournamentPlacementValidator(TournamentSchedulingRequest request) => Context = new(request);
    public TournamentPlacementValidation ValidateInput() => new(Context.InputViolations);

    public TournamentPlacementValidation ValidatePlacement(MatchPlacement placement,
        IReadOnlyDictionary<Guid, MatchPlacement> otherPlacements)
    {
        if (Context.InputViolations.Count > 0) return ValidateInput();
        var issues = new List<SchedulingViolation>();
        if (!Context.Nodes.TryGetValue(placement.MatchId, out var node))
            return new([new(SchedulingConstraintCode.UnknownMatch, null, placement.MatchId, null, "位置引用了未知比赛。")]);
        void Issue(SchedulingConstraintCode code, string message, Guid? related = null) =>
            issues.Add(new(code, node.ProjectId, node.Id, related, message));
        var resources = Context.Request.Resources;
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
        if (!day.Courts.Contains(placement.Court, StringComparer.OrdinalIgnoreCase) ||
            !ScheduleResourceCalculator.IsCourtAvailable(day, placement.Court, placement.StartTime, placement.EndTime))
            Issue(SchedulingConstraintCode.CourtUnavailable, "场地不存在或覆盖了不可用时段。");
        var validOthers = new List<MatchPlacement>();
        foreach (var pair in otherPlacements)
        {
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
            var dependency = validOthers.FirstOrDefault(p => p.MatchId == id);
            if (dependency is null) Issue(SchedulingConstraintCode.MissingPlacement, "必须先安排全部来源场次。", id);
            else if (Context.Minute(dependency, true) > start) Issue(SchedulingConstraintCode.DependencyOrder, "比赛不能早于来源场次结束。", id);
        }
        foreach (var other in validOthers)
        {
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
            if (overlap && string.Equals(placement.Court, other.Court, StringComparison.OrdinalIgnoreCase))
                Issue(SchedulingConstraintCode.CourtOverlap, "同一场地有重叠场次。", other.MatchId);
            if (!Context.ShareCompatiblePlayer(node.Id, other.MatchId)) continue;
            if (overlap) Issue(SchedulingConstraintCode.PlayerOverlap, "确定或兼容晋级路径的同一选手撞场。", other.MatchId);
            else if (Math.Max(start - otherEnd, otherStart - end) < resources.MinimumRestMinutes)
                Issue(SchedulingConstraintCode.MinimumRest, "确定或兼容晋级路径的休息时间不足。", other.MatchId);
        }
        var sameDay = validOthers.Where(p => p.DayLabel == placement.DayLabel).ToArray();
        // Evaluate actual concurrent occupancy on every boundary, not the number of matches
        // touching the candidate's entire interval (which overcounts sequential matches).
        var boundaries = sameDay.SelectMany(p => new[] { p.StartTime, p.EndTime })
            .Concat((day.RefereeCapacityWindows ?? []).SelectMany(w => new[] { w.StartTime, w.EndTime }))
            .Concat((day.UnavailableCourtWindows ?? []).SelectMany(w => new[] { w.StartTime, w.EndTime }))
            .Append(placement.StartTime).Append(placement.EndTime)
            .Where(t => t >= placement.StartTime && t <= placement.EndTime).Distinct().Order().ToArray();
        for (var i = 0; i + 1 < boundaries.Length; i++)
        {
            var a = boundaries[i]; var b = boundaries[i + 1];
            if (sameDay.Count(p => p.StartTime < b && a < p.EndTime) + 1 > ScheduleResourceCalculator.GetConcurrentMatchLimit(day, resources.RefereeCount, a, b))
            { Issue(SchedulingConstraintCode.RefereeCapacity, "同时比赛场数超过该时段的裁判或场地容量。"); break; }
        }
        foreach (var key in Context.Paths[node.Id].Select(p => p.PlayerKey).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var appearances = sameDay.Select(p => p.MatchId).Append(node.Id)
                .Select(id => (IReadOnlyList<ConditionalPlayerPath>)Context.Paths[id].Where(p => string.Equals(p.PlayerKey, key, StringComparison.OrdinalIgnoreCase)).ToArray())
                .Where(paths => paths.Count > 0).ToArray();
            if (appearances.Length > resources.MaxPlayerMatchesPerDay &&
                ConditionalPlayerPaths.MaximumAppearances(appearances, resources.MaxPlayerMatchesPerDay + 1) > resources.MaxPlayerMatchesPerDay)
            { Issue(SchedulingConstraintCode.DailyMatchLimit, $"选手 {key} 的兼容每日场次超过全赛事上限。"); break; }
        }
        return new(issues);
    }

    public TournamentPlacementValidation ValidateSchedule(IReadOnlyDictionary<Guid, MatchPlacement> placements, bool requireComplete = true)
    {
        if (Context.InputViolations.Count > 0) return ValidateInput();
        var issues = new List<SchedulingViolation>();
        foreach (var pair in placements)
        {
            if (pair.Key != pair.Value.MatchId)
                issues.Add(new(SchedulingConstraintCode.PlacementIdentity, null, pair.Key, pair.Value.MatchId, "位置字典键与比赛身份不符。"));
            issues.AddRange(ValidatePlacement(pair.Value, placements.Where(p => p.Key != pair.Key).ToDictionary()).Violations);
        }
        foreach (var node in Context.Nodes.Values.Where(n => !placements.ContainsKey(n.Id) && (requireComplete || Context.LockedIds.Contains(n.Id))))
            issues.Add(new(SchedulingConstraintCode.MissingPlacement, node.ProjectId, node.Id, null, "赛程缺少必须安排的场次。"));
        return new(issues.Distinct().ToArray());
    }
}
