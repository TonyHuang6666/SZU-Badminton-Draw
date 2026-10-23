using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class TournamentWorkspaceWorkflow
{
    public WorkspaceDayConfirmationPreview PreviewDayConfirmation(WorkspaceDayConfirmationRequest request, long expectedRevision) =>
        WithCapturedSession(captured => BuildDayConfirmation(ReadEditingSource(captured, expectedRevision), request));

    public WorkspaceCommandResult ConfirmDayResults(WorkspaceDayConfirmationPreview preview, long expectedRevision) =>
        WithCapturedSession(captured =>
        {
            var source = ReadEditingSource(captured, expectedRevision);
            Require(ReferenceEquals(preview.Owner, this) && preview.Session == scheduleEditingSession &&
                preview.SourceIdentity == DayConfirmationIdentity(source), "day-confirmation.stale", "确认方案已过期，请重新预览。");
            Require(preview.CanConfirm, "day-confirmation.blocked", string.Join("；", preview.Errors));
            return CommitChange(captured, expectedRevision, workspace =>
            {
                Require(DayConfirmationIdentity(workspace) == preview.SourceIdentity, "day-confirmation.stale", "工作区已变化，请重新预览。");
                return Audit(workspace with { Schedule = preview.Candidate.Schedule, DayConfirmations = preview.Candidate.DayConfirmations },
                    "DayResultsConfirmed", detail: JsonSerializer.Serialize(new { preview.Day, preview.CompletedCount, preview.PostponedCount, preview.Changes,
                        Postponements = preview.Candidate.DayConfirmations.Last().Postponements }));
            }, () => undoScheduleEdits = []);
        });

    private static string DayConfirmationIdentity(TournamentWorkspace source) => Fingerprint(new
        { Source = RecoverySourceIdentity(source), source.DayConfirmations });

    private WorkspaceDayConfirmationPreview BuildDayConfirmation(TournamentWorkspace source, WorkspaceDayConfirmationRequest request)
    {
        var errors = new List<string>();
        var sourceValidation = new TournamentPlacementValidator(EditingRequest(source)).ValidateSchedule(source.Schedule!.Placements);
        if (!sourceValidation.IsValid) errors.AddRange(sourceValidation.Violations.Select(v => v.Message));
        var days = source.Resources!.Days.OrderBy(d => d.Date).ToArray();
        var day = days.SingleOrDefault(d => d.Date == request.Day);
        if (day is null || WorkspaceDayConfirmationRules.NextUnconfirmedDay(source) != request.Day)
            errors.Add("必须按日期顺序确认下一个比赛日。");
        var nodes = source.Projects.SelectMany(p => p.MatchGraph!.Matches).Where(n => n.IsPlayable).ToDictionary(n => n.Id);
        var current = nodes.Values.Where(n => source.Schedule!.Placements[n.Id].DayLabel == day?.DayLabel).ToArray();
        var pending = current.Where(n => !source.Results.ContainsKey(new(n.ProjectId, n.Id))).ToArray();
        var completed = current.Length - pending.Length;
        var postponements = request.Postponements.ToArray();
        if (postponements.Select(p => p.MatchKey).Distinct().Count() != postponements.Length ||
            postponements.Any(p => !p.RefereeApproved || string.IsNullOrWhiteSpace(p.Reason)))
            errors.Add("每场延期必须有唯一比赛、延期原因和裁判长同意。");
        if (!pending.Select(n => new WorkspaceMatchKey(n.ProjectId, n.Id)).ToHashSet().SetEquals(postponements.Select(p => p.MatchKey)))
            errors.Add("全部未完成比赛必须明确延期，不能遗漏或包含其他场次。");
        var nextDay = days.FirstOrDefault(d => d.Date > request.Day);
        if (postponements.Length > 0 && nextDay is null) errors.Add(source.Results.Count == 0
            ? "没有下一个已配置比赛日，无法办理延期。可以先在安排赛程中增加日期和资源，再生成赛程。"
            : "没有下一个已配置比赛日，无法办理延期。当天保持待确认，现有赛果与赛程保持不变；已有赛果时不能重新生成赛程。请人工核对后续办赛安排。");
        var candidate = source;
        IReadOnlyList<ScheduleEditChange> changes = [];
        if (errors.Count == 0 && pending.Length > 0)
        {
            var map = BuildBatchPostponement(source, pending, nextDay!, out var failure);
            if (map is null) errors.Add(failure);
            else
            {
                candidate = source with { Schedule = EditedSchedule(source, map) };
                var names = source.Projects.ToDictionary(p => p.Id, p => p.DisplayName);
                changes = map.Values.Where(p => p != source.Schedule!.Placements[p.MatchId]).Select(p =>
                {
                    var n = nodes[p.MatchId];
                    return new ScheduleEditChange(new(n.ProjectId, n.Id), names[n.ProjectId], n.DisplayName, 0, source.Schedule!.Placements[n.Id], p);
                }).ToArray();
            }
        }
        if (errors.Count == 0)
        {
            var finalValidation = new TournamentPlacementValidator(EditingRequest(candidate)).ValidateSchedule(candidate.Schedule!.Placements);
            if (!finalValidation.IsValid) errors.AddRange(finalValidation.Violations.Select(v => v.Message));
        }
        if (errors.Count == 0)
        {
            candidate = candidate with { DayConfirmations = [.. source.DayConfirmations,
                new(request.Day, DateTimeOffset.UtcNow, candidate.Schedule!.Revision, completed, postponements)] };
            try { TournamentWorkspaceRules.Validate(candidate); }
            catch (WorkspaceValidationException e) { errors.Add(e.Message); candidate = source; changes = []; }
        }
        return new(this, scheduleEditingSession, DayConfirmationIdentity(source), candidate, request.Day, changes, errors, completed, postponements.Length);
    }

    private static IReadOnlyDictionary<Guid, MatchPlacement>? BuildBatchPostponement(TournamentWorkspace source,
        IReadOnlyList<MatchNode> roots, ScheduleDaySettings nextDay, out string failure)
    {
        failure = "本次延期搜索未找到完整合法赛程；原赛程未改变。";
        var schedule = source.Schedule!;
        var nodes = source.Projects.SelectMany(p => p.MatchGraph!.Matches).Where(n => n.IsPlayable).ToDictionary(n => n.Id);
        var rootIds = roots.Select(n => n.Id).ToHashSet();
        var affected = rootIds.ToHashSet();
        var queue = new Queue<Guid>(rootIds);
        while (queue.TryDequeue(out var id))
            foreach (var n in nodes.Values.Where(n => n.Dependencies.Contains(id)))
                if (affected.Add(n.Id)) queue.Enqueue(n.Id);
        affected.ExceptWith(source.Results.Keys.Select(k => k.MatchId));
        var map = schedule.Placements.Where(p => !affected.Contains(p.Key)).ToDictionary();
        var validator = new TournamentPlacementValidator(EditingRequest(source));
        var days = source.Resources!.Days.OrderBy(d => d.Date).ToArray();
        var dates = days.ToDictionary(d => d.DayLabel, d => d.Date);
        var pending = affected.ToHashSet();
        var visits = 0;
        bool Search()
        {
            if (++visits > 100000) return false;
            if (pending.Count == 0) return validator.ValidateSchedule(map).IsValid;
            var node = pending.Select(id => nodes[id]).Where(n => n.Dependencies.All(map.ContainsKey) &&
                (!n.IsChampionshipFinal || !pending.Any(id => nodes[id].IsPlacementPlayoff && nodes[id].ProjectId == n.ProjectId)))
                .OrderBy(n => n.Order).ThenBy(n => n.Id).FirstOrDefault();
            if (node is null) return false;
            pending.Remove(node.Id);
            var old = schedule.Placements[node.Id];
            var duration = ScheduleTimingResolver.Resolve(node, schedule.Policy);
            IEnumerable<MatchPlacement> Candidates()
            {
                if (!rootIds.Contains(node.Id) && dates[old.DayLabel] >= nextDay.Date) yield return old;
                foreach (var d in days.Where(d => rootIds.Contains(node.Id) ? d.Date == nextDay.Date : d.Date >= nextDay.Date && d.Date >= dates[old.DayLabel]))
                    for (var minute = (int)d.DayStart.ToTimeSpan().TotalMinutes; minute + duration <= d.DayEnd.ToTimeSpan().TotalMinutes; minute++)
                    {
                        var time = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute));
                        if (d.Date == dates[old.DayLabel] && time < old.StartTime) continue;
                        foreach (var court in d.Courts.OrderBy(c => c == old.Court ? 0 : 1))
                            yield return new(node.Id, d.DayLabel, time, time.AddMinutes(duration), court);
                    }
            }
            foreach (var placement in Candidates().Distinct())
            {
                if (++visits > 100000) break;
                if (!validator.ValidatePlacement(placement, map).IsValid) continue;
                map[node.Id] = placement;
                if (Search()) return true;
                map.Remove(node.Id);
            }
            pending.Add(node.Id);
            return false;
        }
        return Search() ? map : null;
    }
}
