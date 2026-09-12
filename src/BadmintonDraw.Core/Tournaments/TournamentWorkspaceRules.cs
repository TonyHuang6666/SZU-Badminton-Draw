using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;

namespace BadmintonDraw.Core.Tournaments;

public static class TournamentWorkspaceRules
{
    private static void Require(bool condition, string code, string message)
    {
        if (!condition) throw new WorkspaceValidationException(code, message);
    }

    public static void Validate(TournamentWorkspace workspace)
    {
        Require(workspace.Id != Guid.Empty && !string.IsNullOrWhiteSpace(workspace.Name), "workspace.identity", "赛事名称和标识不能为空。");
        Require(Enum.IsDefined(workspace.Kind) && Enum.IsDefined(workspace.Purpose) && Enum.IsDefined(workspace.Stage), "workspace.enum", "赛事类型、目标或阶段无效。");
        Require(workspace.Revision >= 0 && workspace.CreatedAt != default && workspace.UpdatedAt >= workspace.CreatedAt,
            "workspace.version", "赛事修订号或时间无效。");
        Require(workspace.Projects.Count is >= 1 and <= 5, "projects.count", "赛事必须包含一至五个项目。");
        Require(workspace.Projects.Select(p => p.Id).Distinct().Count() == workspace.Projects.Count &&
            workspace.Projects.Select(p => p.Discipline).Distinct().Count() == workspace.Projects.Count,
            "projects.duplicate", "项目标识和项目种类不能重复。");
        Require(workspace.Kind == TournamentKind.Team
            ? workspace.Projects.Count == 1 && workspace.Projects[0].Discipline == EventDiscipline.Team
            : workspace.Projects.All(p => p.Discipline != EventDiscipline.Team), "projects.kind", "团体赛和单项赛不能混合。");
        foreach (var project in workspace.Projects) ValidateProject(project);
        Require(workspace.Stage >= TournamentStage.RostersReady || workspace.Projects.All(p => p.Draw is null), "stage.draw-data", "名单准备完成后才能抽签。");
        Require(workspace.Schedule is null || workspace.Stage >= TournamentStage.DrawsConfirmed, "stage.schedule-data", "全部抽签确认后才能编排赛程。");
        Require(workspace.Results.Count == 0 || (workspace.Stage >= TournamentStage.ScheduleReady && workspace.Schedule is not null), "stage.result-data", "生成全局赛程后才能录入赛果。");
        var nodes = workspace.Projects.SelectMany(p => p.MatchGraph?.Matches ?? []).ToArray();
        Require(nodes.Select(n => n.Id).Distinct().Count() == nodes.Length, "graph.duplicate", "比赛标识必须在赛事内唯一。");
        if (workspace.Resources is not null) ValidateResources(workspace.Resources);
        if (workspace.Schedule is not null) ValidateSchedule(workspace, nodes);
        foreach (var (key, result) in workspace.Results)
        {
            var node = nodes.SingleOrDefault(n => n.ProjectId == key.ProjectId && n.Id == key.MatchId);
            Require(node is not null && node.IsPlayable && key == result.Key, "result.match", "赛果必须引用本赛事中需要进行的比赛。");
            ValidateParticipant(result.Winner);
            ValidateParticipant(result.Loser);
            Require(result.Winner.IdentityKey != result.Loser.IdentityKey && !string.IsNullOrWhiteSpace(result.Score) &&
                result.DurationMinutes > 0 && result.RecordedAt != default, "result.value", "赛果胜负方、比分或时长无效。");
            var a = Resolve(node!.SideA, node.ProjectId, workspace.Results);
            var b = Resolve(node.SideB, node.ProjectId, workspace.Results);
            Require(a is not null && b is not null &&
                ((SameEntrant(result.Winner, a) && SameEntrant(result.Loser, b)) ||
                 (SameEntrant(result.Winner, b) && SameEntrant(result.Loser, a))), "result.entrant", "赛果参赛方与比赛来源不一致，或上游赛果缺失。");
        }
        Require(workspace.AuditEvents.Select(a => a.Id).Distinct().Count() == workspace.AuditEvents.Count, "audit.duplicate", "审计事件标识重复。");
        foreach (var audit in workspace.AuditEvents)
            Require(audit.Id != Guid.Empty && !string.IsNullOrWhiteSpace(audit.Action) && audit.OccurredAt != default &&
                (audit.ProjectId is null || workspace.Projects.Any(p => p.Id == audit.ProjectId)) &&
                (audit.MatchId is null || (audit.MatchId != Guid.Empty && audit.ProjectId is not null)),
                "audit.identity", "审计事件身份或引用无效。");
        if (workspace.Stage >= TournamentStage.RostersReady)
            Require(workspace.Projects.All(p => p.Roster is not null), "stage.rosters", "全部项目名单通过校验后才能开始抽签。");
        if (workspace.Stage >= TournamentStage.DrawsConfirmed)
            Require(workspace.Projects.All(p => p.Draw?.ConfirmedAt is not null && p.MatchGraph is not null), "stage.draws", "请先确认全部项目抽签。");
        Require(workspace.Purpose != TournamentPurpose.PublicDrawOnly ||
            (workspace.Stage <= TournamentStage.DrawsConfirmed && workspace.Schedule is null && workspace.Results.Count == 0),
            "stage.draw-only", "仅公开抽签赛事需要先升级为完整赛事才能编排赛程。");
        if (workspace.Stage >= TournamentStage.ScheduleReady)
            Require(workspace.Resources is not null && workspace.Schedule is not null, "stage.schedule", "需要完整的全局赛程。");
        if (workspace.Stage >= TournamentStage.InProgress)
            Require(workspace.Results.Count > 0, "stage.results", "至少录入一场有效赛果才能进入现场执行。");
        if (workspace.Stage == TournamentStage.Completed)
            Require(nodes.Where(n => n.IsPlayable).All(n => workspace.Results.ContainsKey(new(n.ProjectId, n.Id))),
                "stage.completed", "全部需要进行的比赛均有有效赛果后才能完成赛事。");
    }

    public static TournamentWorkspace Transition(TournamentWorkspace workspace, TournamentStage target)
    {
        Validate(workspace);
        Require(Enum.IsDefined(target) && (int)target == (int)workspace.Stage + 1, "stage.transition", "只能前进到相邻阶段。");
        var candidate = workspace with { Stage = target };
        Validate(candidate);
        return candidate;
    }

    public static TournamentWorkspace UpgradeToFullTournament(TournamentWorkspace workspace)
    {
        Validate(workspace);
        Require(workspace.Purpose == TournamentPurpose.PublicDrawOnly, "purpose.upgrade", "只有仅公开抽签赛事可以升级。");
        var candidate = workspace with { Purpose = TournamentPurpose.FullTournament,
            AuditEvents = [..workspace.AuditEvents, new(Guid.NewGuid(), "PurposeUpgraded", DateTimeOffset.UtcNow)] };
        Validate(candidate);
        return candidate;
    }

    public static TournamentWorkspace ReopenDraw(TournamentWorkspace workspace, Guid projectId, string reason)
    {
        Validate(workspace);
        Require(workspace.Results.Count == 0, "draw.frozen", "已有赛果，不能解除抽签确认。");
        Require(!string.IsNullOrWhiteSpace(reason), "draw.reason", "请填写解除确认原因。");
        var project = workspace.Projects.SingleOrDefault(p => p.Id == projectId);
        Require(project?.Draw?.ConfirmedAt is not null, "draw.confirmed", "项目不存在或抽签尚未确认。");
        var candidate = workspace with { Stage = TournamentStage.RostersReady, Schedule = null, Resources = null,
            Projects = workspace.Projects.Select(p => p.Id == projectId ? p with { MatchGraph = null, Draw = null } : p).ToArray(),
            AuditEvents = [..workspace.AuditEvents, new(Guid.NewGuid(), "DrawReopened", DateTimeOffset.UtcNow, projectId, Detail: reason)] };
        Validate(candidate);
        return candidate;
    }

    public static void ValidateProject(TournamentProject project)
    {
        Require(project.Id != Guid.Empty && !string.IsNullOrWhiteSpace(project.DisplayName) && project.SortOrder >= 0,
            "project.identity", "项目标识、名称或顺序无效。");
        Require(Enum.IsDefined(project.Discipline) && Enum.IsDefined(project.CompetitionMode), "project.enum", "项目种类或赛制无效。");
        Require((project.Discipline == EventDiscipline.Team) ==
            (project.CompetitionMode is CompetitionMode.TeamKnockout or CompetitionMode.TeamRoundRobin), "project.mode", "团体项目必须使用团体赛制，单项项目必须使用单项赛制。");
        if (project.Roster is { } roster)
        {
            Require(roster.Participants.Count >= 2 && !string.IsNullOrWhiteSpace(roster.SourceFileName) && !string.IsNullOrWhiteSpace(roster.ContentHash), "roster.required", "名单至少需要两个参赛方，并保留来源文件和哈希。");
            var entrants = roster.Participants.Select(p => ProjectEntrantIdentity.Create(project.Discipline, p)).ToArray();
            Require(entrants.Select(p => p.IdentityKey).Distinct(StringComparer.Ordinal).Count() == entrants.Length,
                "roster.identity", "名单参赛方身份不能重复。");
            Require(roster.Warnings.All(w => !string.IsNullOrWhiteSpace(w.Code) && !string.IsNullOrWhiteSpace(w.Message) && (w.RowNumber is null or > 0)), "roster.warning", "名单警告无效。");
        }
        if (project.Draw is { } draw)
        {
            Require(project.Roster is not null, "draw.roster", "抽签必须有名单。");
            var expectedKind = project.Discipline switch { EventDiscipline.Team => EventKind.Team,
                EventDiscipline.MenDoubles or EventDiscipline.WomenDoubles or EventDiscipline.MixedDoubles => EventKind.Doubles, _ => EventKind.Singles };
            Require(draw.Result.Settings.CompetitionMode == project.CompetitionMode && draw.Result.Settings.EventKind == expectedKind &&
                draw.Result.Settings.GroupCount > 0 && !string.IsNullOrWhiteSpace(draw.Result.Settings.RandomSeed), "draw.settings", "抽签设置与项目不一致。");
            Require(draw.ConfirmedAt is null || draw.ConfirmedAt >= draw.Result.Audit.GeneratedAt, "draw.time", "抽签确认时间早于生成时间。");
        }
        Require((project.MatchGraph is not null) == (project.Draw?.ConfirmedAt is not null), "draw.graph", "确认抽签必须同时存在比赛关系图，未确认抽签不能有比赛关系图。");
        if (project.MatchGraph is { } graph) ValidateGraph(project, graph);
    }

    private static void ValidateParticipant(EntrantSource.Participant participant)
    {
        Require(!string.IsNullOrWhiteSpace(participant.IdentityKey) && !string.IsNullOrWhiteSpace(participant.DisplayName) &&
            participant.Players.Count > 0 && participant.Players.All(p => !string.IsNullOrWhiteSpace(p.Name)) &&
            participant.Players.Select(p => p.IdentityKey).Distinct().Count() == participant.Players.Count,
            "entrant.identity", "参赛方必须保留有效且唯一的选手身份。");
    }

    private static bool SameEntrant(EntrantSource.Participant a, EntrantSource.Participant b) =>
        a.IdentityKey == b.IdentityKey && a.Players.Select(p => p.IdentityKey).Order().SequenceEqual(b.Players.Select(p => p.IdentityKey).Order());

    private static EntrantSource.Participant? Resolve(EntrantSource source, Guid projectId,
        IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> results)
    {
        if (source is EntrantSource.Participant participant) return participant;
        var id = source switch { EntrantSource.WinnerOf winner => winner.MatchId, EntrantSource.LoserOf loser => loser.MatchId, _ => Guid.Empty };
        if (results.TryGetValue(new(projectId, id), out var result)) return source is EntrantSource.WinnerOf ? result.Winner : result.Loser;
        return null;
    }

    private static void ValidateGraph(TournamentProject project, MatchGraph graph)
    {
        graph.RequirePlayableNodes();
        var projectId = project.Id;
        var rosterIdentities = project.Roster!.Participants.Select(p => ProjectEntrantIdentity.Create(project.Discipline, p).IdentityKey).ToHashSet(StringComparer.Ordinal);
        Require(graph.ProjectId == projectId && !string.IsNullOrWhiteSpace(graph.Revision) && graph.Matches.Count > 0, "graph.identity", "比赛关系图项目、版本或场次无效。");
        Require(graph.Matches.Select(n => n.Id).Distinct().Count() == graph.Matches.Count &&
            graph.Matches.Select(n => n.OriginalMatchId).Distinct().Count() == graph.Matches.Count, "graph.duplicate", "比赛标识重复。");
        var byId = graph.Matches.ToDictionary(n => n.Id);
        foreach (var node in graph.Matches)
        {
            Require(node.Id != Guid.Empty && node.ProjectId == projectId && !string.IsNullOrWhiteSpace(node.OriginalMatchId) &&
                !string.IsNullOrWhiteSpace(node.DisplayName) && !string.IsNullOrWhiteSpace(node.Phase) && node.Order > 0 &&
                node.GroupNumber >= 0 && node.ExpectedDurationMinutes > 0, "graph.node", "比赛身份、阶段或预计时长无效。");
            var references = new List<Guid>();
            foreach (var side in new[] { node.SideA, node.SideB })
                switch (side)
                {
                    case EntrantSource.Participant p:
                        ValidateParticipant(p);
                        Require(rosterIdentities.Contains(ProjectEntrantIdentity.IdentityKey(p.Players)), "graph.roster", "比赛参赛方必须完整匹配项目名单中的选手身份和搭档。");
                        break;
                    case EntrantSource.WinnerOf w: references.Add(w.MatchId); break;
                    case EntrantSource.LoserOf l: references.Add(l.MatchId); break;
                    default: throw new WorkspaceValidationException("graph.source", "比赛来源无效。");
                }
            Require(node.Dependencies.Distinct().Count() == node.Dependencies.Count &&
                references.ToHashSet().SetEquals(node.Dependencies) &&
                references.All(id => byId.TryGetValue(id, out var predecessor) && predecessor.Order < node.Order),
                "graph.dependency", "比赛依赖必须引用同项目中顺序在前的比赛，且与双方来源一致。");
        }
    }

    private static void ValidateResources(TournamentResourcePlan resources)
    {
        Require(resources.Days.Count > 0 && resources.Days.Select(d => d.Date).Distinct().Count() == resources.Days.Count &&
            resources.RefereeCount is null or > 0 && resources.MinimumRestMinutes >= 0 && resources.MaxPlayerMatchesPerDay > 0,
            "resources.value", "比赛日、裁判容量或负荷限制无效。");
        foreach (var day in resources.Days)
        {
            Require(day.DayStart < day.DayEnd && day.Courts.Count > 0 && day.Courts.All(c => !string.IsNullOrWhiteSpace(c)) &&
                day.Courts.Distinct(StringComparer.OrdinalIgnoreCase).Count() == day.Courts.Count, "resources.day", "比赛日时间或场地无效。");
            Require((day.RefereeCapacityWindows ?? []).All(w => w.StartTime < w.EndTime && w.StartTime >= day.DayStart && w.EndTime <= day.DayEnd && w.RefereeCount >= 0), "resources.referee", "裁判时段无效。");
            Require((day.UnavailableCourtWindows ?? []).All(w => w.StartTime < w.EndTime && w.StartTime >= day.DayStart && w.EndTime <= day.DayEnd && w.Courts.All(c => day.Courts.Contains(c))), "resources.window", "场地不可用时段无效。");
        }
    }

    private static void ValidateSchedule(TournamentWorkspace workspace, IReadOnlyList<MatchNode> nodes)
    {
        var schedule = workspace.Schedule!;
        Require(workspace.Resources is not null, "schedule.resources", "全局赛程必须关联赛事资源。");
        ValidateResources(schedule.Resources);
        // Value comparison includes nested courts/windows; record list equality would compare references.
        Require(System.Text.Json.JsonSerializer.Serialize(workspace.Resources) == System.Text.Json.JsonSerializer.Serialize(schedule.Resources),
            "schedule.resources", "赛程资源与赛事资源不一致。");
        Require(schedule.Revision >= 0 && Enum.IsDefined(schedule.Policy.Strategy), "schedule.version", "赛程版本或策略无效。");
        Require(schedule.GraphRevisions.Count == workspace.Projects.Count && workspace.Projects.All(p =>
            p.MatchGraph is not null && schedule.GraphRevisions.TryGetValue(p.Id, out var revision) && revision == p.MatchGraph.Revision),
            "schedule.graph-revision", "赛程绑定的比赛关系图版本已失效。");
        Require(schedule.Placements.Count == nodes.Count && nodes.All(n => schedule.Placements.ContainsKey(n.Id)), "schedule.coverage", "每场比赛必须恰好安排一次。");
        foreach (var (id, placement) in schedule.Placements)
        {
            var day = schedule.Resources.Days.SingleOrDefault(d => d.DayLabel == placement.DayLabel);
            Require(id == placement.MatchId && day is not null && day.Courts.Contains(placement.Court) &&
                placement.StartTime < placement.EndTime && placement.StartTime >= day.DayStart && placement.EndTime <= day.DayEnd,
                "schedule.placement", "比赛位置的身份、比赛日、时间或场地无效。");
            Require(ScheduleResourceCalculator.IsCourtAvailable(day!, placement.Court, placement.StartTime, placement.EndTime),
                "schedule.unavailable", "比赛落在场地不可用时段。");
        }
    }
}
