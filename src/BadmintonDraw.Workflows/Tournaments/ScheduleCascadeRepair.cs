using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

/// <summary>Local, forward-only dependency repair. Other projects never become movable input.</summary>
internal static class ScheduleCascadeRepair
{
    internal sealed record Result(IReadOnlyDictionary<Guid, MatchPlacement>? Placements,
        IReadOnlyDictionary<Guid, int> Depths, TournamentPlacementValidation Validation);

    internal static Result Build(TournamentWorkspace workspace, MatchNode root, MatchPlacement target, TournamentPlacementValidator validator)
    {
        var schedule = workspace.Schedule!;
        var nodes = workspace.Projects.Single(p => p.Id == root.ProjectId).MatchGraph!.Matches.ToDictionary(n => n.Id);
        var depths = new Dictionary<Guid, int> { [root.Id] = 0 };
        var queue = new Queue<Guid>(); queue.Enqueue(root.Id);
        while (queue.TryDequeue(out var source))
            foreach (var node in nodes.Values.Where(n => n.Dependencies.Contains(source)))
                if (depths.TryAdd(node.Id, depths[source] + 1)) queue.Enqueue(node.Id);

        var locked = workspace.Results.Keys.Select(k => k.MatchId).ToHashSet();
        // Completed descendants stay at their genuine location. They constrain every candidate.
        var pending = depths.Keys.Where(id => id != root.Id && !locked.Contains(id)).ToHashSet();
        var map = schedule.Placements.Where(p => p.Key != root.Id && !pending.Contains(p.Key)).ToDictionary();
        var valid = validator.ValidatePlacement(target, map);
        if (!valid.IsValid) return new(null, depths, valid);
        map[root.Id] = target;
        var days = workspace.Resources!.Days.OrderBy(d => d.Date).ToArray();
        var dates = days.ToDictionary(d => d.DayLabel, d => d.Date);
        double Minute(MatchPlacement p) => (long)dates[p.DayLabel].DayNumber * 1440 + p.StartTime.ToTimeSpan().TotalMinutes;
        while (pending.Count > 0)
        {
            var next = pending.Select(id => nodes[id]).Where(n => n.Dependencies.All(map.ContainsKey) &&
                    (!n.IsChampionshipFinal || !pending.Any(id => nodes[id].IsPlacementPlayoff)))
                .OrderBy(n => depths[n.Id]).ThenBy(n => n.Order).ThenBy(n => n.Id).FirstOrDefault();
            if (next is null) return new(null, depths, new([new(SchedulingConstraintCode.MissingPlacement,
                root.ProjectId, root.Id, null, "后续依赖或名次赛阻塞了连锁调整。") ]));
            var old = schedule.Placements[next.Id];
            var placement = validator.ValidatePlacement(old, map).IsValid ? old : null;
            var rejections = new Dictionary<SchedulingConstraintCode, SchedulingViolation>();
            var duration = ScheduleTimingResolver.Resolve(next, schedule.Policy);
            if (placement is null)
                foreach (var day in days)
                {
                    foreach (var time in CandidateStarts(day, old, duration, workspace.Resources.MinimumRestMinutes, map.Values))
                    {
                        if ((long)day.Date.DayNumber * 1440 + time.ToTimeSpan().TotalMinutes < Minute(old)) continue;
                        foreach (var court in day.Courts.OrderBy(c => c == old.Court ? 0 : 1))
                        {
                            var candidate = new MatchPlacement(next.Id, day.DayLabel, time, time.AddMinutes(duration), court);
                            var check = validator.ValidatePlacement(candidate, map);
                            if (check.IsValid) { placement = candidate; break; }
                            foreach (var issue in check.Violations) rejections.TryAdd(issue.Code, issue);
                        }
                        if (placement is not null) break;
                    }
                    if (placement is not null) break;
                }
            if (placement is null)
            {
                rejections.TryAdd(SchedulingConstraintCode.SearchExhausted, new(SchedulingConstraintCode.SearchExhausted,
                    next.ProjectId, next.Id, null, "本次连锁搜索未找到合法的后移位置；不会移动其他项目，也不表示所有方案都无解。"));
                return new(null, depths, new(rejections.Values.ToArray()));
            }
            map[next.Id] = placement; pending.Remove(next.Id);
        }
        valid = validator.ValidateSchedule(map);
        return new(valid.IsValid ? map : null, depths, valid);
    }

    private static IEnumerable<TimeOnly> CandidateStarts(ScheduleDaySettings day, MatchPlacement original, int duration,
        int rest, IEnumerable<MatchPlacement> placed)
    {
        var earliest = day.DayStart.ToTimeSpan().TotalMinutes;
        var latest = day.DayEnd.ToTimeSpan().TotalMinutes - duration;
        var times = new HashSet<TimeOnly>();
        void Add(TimeOnly time, int offset = 0)
        {
            var minute = time.ToTimeSpan().TotalMinutes + offset;
            if (minute >= earliest && minute <= latest) times.Add(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute)));
        }
        for (var minute = earliest; minute <= latest; minute++) times.Add(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute)));
        if (original.DayLabel == day.DayLabel) Add(original.StartTime);
        foreach (var p in placed.Where(p => p.DayLabel == day.DayLabel))
        { Add(p.EndTime); Add(p.EndTime, rest); Add(p.StartTime, -duration); Add(p.StartTime, -duration - rest); }
        foreach (var w in day.UnavailableCourtWindows ?? []) { Add(w.EndTime); Add(w.StartTime, -duration); }
        foreach (var w in day.RefereeCapacityWindows ?? []) { Add(w.EndTime); Add(w.StartTime, -duration); }
        Add(day.DayEnd, -duration);
        return times.Order();
    }
}
