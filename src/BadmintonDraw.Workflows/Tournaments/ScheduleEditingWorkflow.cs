using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class TournamentWorkspaceWorkflow
{
    private Guid scheduleEditingSession = Guid.NewGuid();
    private volatile IReadOnlyList<ScheduleUndoEntry> undoScheduleEdits = [];

    /// <summary>Session-local availability only. Undo rechecks the durable archive and current results.</summary>
    public bool CanUndoScheduleEdit => undoScheduleEdits.Count > 0 && CurrentSession?.RequiresReload == false;

    public ScheduleEditBaseline CaptureScheduleEditBaseline() => WithCapturedSession(captured =>
    {
        var workspace = ReadEditingSource(captured, captured.Workspace.Revision);
        ThrowIfEditInvalid(new TournamentPlacementValidator(EditingRequest(workspace)).ValidateSchedule(workspace.Schedule!.Placements),
            "schedule.invalid", "当前完整赛程未通过约束校验，不能开始手工调整。");
        return NewEditingBaseline(workspace);
    });

    public ScheduleEditPreview PreviewMove(MoveMatchRequest request, long expectedRevision) => WithCapturedSession(captured =>
        BuildEditPreview(ReadEditingSource(captured, expectedRevision, RequiredBaseline(request)), request, false));

    public ScheduleEditPreview PreviewCascade(MoveMatchRequest request, long expectedRevision) => WithCapturedSession(captured =>
        BuildEditPreview(ReadEditingSource(captured, expectedRevision, RequiredBaseline(request)), request, true));

    public WorkspaceCommandResult MoveMatch(MoveMatchRequest request, long expectedRevision) => WithCapturedSession(captured =>
    {
        var source = ReadEditingSource(captured, expectedRevision, RequiredBaseline(request));
        return ApplyEdit(captured, source, BuildEditPreview(source, request, false), expectedRevision);
    });

    public WorkspaceCommandResult CascadeMove(ScheduleEditPreview preview, long expectedRevision) => WithCapturedSession(captured =>
    {
        var source = ReadEditingSource(captured, expectedRevision, RequiredBaseline(preview.Root));
        Require(preview.IsCascade, "schedule.preview-kind", "连锁移动需要连锁预览。");
        Require(preview.SourceRevision == expectedRevision, "schedule.preview-stale", "预览后工作区已变化，请重新预览并确认。");
        return ApplyEdit(captured, source, preview, expectedRevision);
    });

    public WorkspaceCommandResult UndoLastScheduleEdit(long expectedRevision) => WithCapturedSession(captured =>
    {
        var source = ReadEditingSource(captured, expectedRevision);
        Require(undoScheduleEdits.Count > 0, "schedule.undo-empty", "当前会话没有可撤销的赛程调整。");
        var entry = undoScheduleEdits[^1];
        Require(entry.AfterIdentity == EditingScheduleIdentity(source) && entry.ContextIdentity == EditingContextIdentity(source),
            "schedule.undo-stale", "赛程或资源已变化，不能套用旧撤销记录。");
        ThrowIfEditInvalid(new TournamentPlacementValidator(EditingRequest(source)).ValidateSchedule(entry.Before),
            "schedule.undo-blocked", "原位置已不再满足当前赛果锁定或其他约束，无法撤销。");
        var sourceIdentity = EditingSourceIdentity(source);
        var nextSchedule = EditedSchedule(source, entry.Before);
        var nextUndo = undoScheduleEdits.Take(undoScheduleEdits.Count - 1).ToArray();
        if (nextUndo.Length > 0)
            nextUndo[^1] = nextUndo[^1] with { AfterIdentity = EditingScheduleIdentity(source with { Schedule = nextSchedule }) };
        return CommitChange(captured, expectedRevision, workspace =>
        {
            RequireEditingSource(workspace, sourceIdentity);
            ThrowIfEditInvalid(new TournamentPlacementValidator(EditingRequest(workspace)).ValidateSchedule(entry.Before),
                "schedule.undo-blocked", "原位置已不再满足当前约束，无法撤销。");
            // Preserve the current aggregate, including any results, import history and audit since the edit.
            return Audit(workspace with { Schedule = nextSchedule }, "ScheduleEditUndone", detail: "赛程修订号：" + nextSchedule.Revision);
        }, () => undoScheduleEdits = nextUndo);
    });

    private WorkspaceCommandResult ApplyEdit(WorkspaceSession captured, TournamentWorkspace source, ScheduleEditPreview preview, long expectedRevision)
    {
        var code = preview.IsCascade ? "schedule.cascade-blocked" : "schedule.move-blocked";
        ThrowIfEditInvalid(new(preview.Violations), code, "移动未通过全赛事约束校验。");
        Require(preview.Placements is not null, code, "移动没有完整有效的候选赛程。");
        ThrowIfEditInvalid(new TournamentPlacementValidator(EditingRequest(source)).ValidateSchedule(preview.Placements!), code,
            "移动未通过全赛事约束校验。");
        if (!preview.HasChanges) return new(source, captured.WorkspacePath, null, []);
        var nextSchedule = EditedSchedule(source, preview.Placements!);
        var nextUndo = undoScheduleEdits.Append(new ScheduleUndoEntry(source.Schedule!.Placements,
            EditingContextIdentity(source), EditingScheduleIdentity(source with { Schedule = nextSchedule }))).ToArray();
        var sourceIdentity = EditingSourceIdentity(source);
        return CommitChange(captured, expectedRevision, workspace =>
        {
            RequireEditingSource(workspace, sourceIdentity);
            ThrowIfEditInvalid(new TournamentPlacementValidator(EditingRequest(workspace)).ValidateSchedule(preview.Placements!), code,
                "移动未通过当前全赛事约束校验。");
            var detail = JsonSerializer.Serialize(preview.Changes);
            return Audit(workspace with { Schedule = nextSchedule }, preview.IsCascade ? "ScheduleCascadeMoved" : "ScheduleMatchMoved",
                preview.Root.Key.ProjectId, detail);
        }, () => undoScheduleEdits = nextUndo);
    }

    private ScheduleEditPreview BuildEditPreview(TournamentWorkspace workspace, MoveMatchRequest request, bool cascade)
    {
        var graph = workspace.Projects.SingleOrDefault(p => p.Id == request.Key.ProjectId)?.MatchGraph;
        var root = graph?.Matches.SingleOrDefault(n => n.Id == request.Key.MatchId);
        Require(root is not null, "schedule.match-not-found", "当前项目中找不到要移动的比赛。");
        var schedule = workspace.Schedule!;
        var validator = new TournamentPlacementValidator(EditingRequest(workspace));
        var input = validator.ValidateSchedule(schedule.Placements);
        if (!input.IsValid) return new(request, workspace.Revision, cascade, [], input.Violations, null);
        var minutes = ScheduleTimingResolver.Resolve(root!, schedule.Policy);
        var target = new MatchPlacement(root!.Id, request.DayLabel, request.StartTime, request.StartTime.AddMinutes(minutes), request.Court);
        IReadOnlyDictionary<Guid, MatchPlacement> proposed;
        IReadOnlyDictionary<Guid, int> depths;
        if (cascade)
        {
            var repair = ScheduleCascadeRepair.Build(workspace, root, target, validator);
            if (!repair.Validation.IsValid) return new(request, workspace.Revision, true, [], repair.Validation.Violations, null);
            proposed = repair.Placements!; depths = repair.Depths;
        }
        else
        {
            var map = schedule.Placements.ToDictionary(); map[root.Id] = target;
            proposed = map; depths = new Dictionary<Guid, int> { [root.Id] = 0 };
        }
        var validation = validator.ValidateSchedule(proposed);
        if (!validation.IsValid) return new(request, workspace.Revision, cascade, [], validation.Violations, null);
        var projectNames = workspace.Projects.ToDictionary(p => p.Id, p => p.DisplayName);
        var nodes = workspace.Projects.SelectMany(p => p.MatchGraph!.Matches).ToDictionary(n => n.Id);
        var changes = proposed.Values.Where(p => p != schedule.Placements[p.MatchId]).Select(p =>
        {
            var n = nodes[p.MatchId];
            return new ScheduleEditChange(new(n.ProjectId, n.Id), projectNames[n.ProjectId], n.DisplayName,
                depths.GetValueOrDefault(n.Id), schedule.Placements[n.Id], p);
        }).OrderBy(c => c.Depth).ThenBy(c => nodes[c.Key.MatchId].Order).ThenBy(c => c.Key.MatchId).ToArray();
        return new(request, workspace.Revision, cascade, changes, [], proposed);
    }

    private TournamentWorkspace ReadEditingSource(WorkspaceSession captured, long expectedRevision, ScheduleEditBaseline? baseline = null)
    {
        RequireSchedulingRevision(captured.Workspace, expectedRevision);
        RequireEditingEligible(captured.Workspace);
        if (baseline is not null) RequireEditingBaseline(captured.Workspace, baseline);
        var durable = store.Read(captured.WorkspacePath);
        // Once an external change is actually observed, old undo no longer belongs to the
        // durable schedule, even though the stale command below must not refresh the session.
        ReconcileScheduleUndo(new(durable, captured.WorkspacePath));
        RequireWorkspaceIdentity(durable, captured);
        RequireSchedulingRevision(durable, expectedRevision);
        RequireEditingSource(durable, EditingSourceIdentity(captured.Workspace));
        return captured.Workspace;
    }

    private static void RequireEditingEligible(TournamentWorkspace workspace) => Require(
        workspace.Purpose == TournamentPurpose.FullTournament && workspace.Stage is TournamentStage.ScheduleReady or TournamentStage.InProgress or TournamentStage.Completed &&
        workspace.Schedule is not null && workspace.Resources is not null && workspace.Projects.All(p => p.Draw?.ConfirmedAt is not null && p.MatchGraph is not null),
        "schedule.not-ready", "请先确认全部抽签并生成有效的全赛事赛程。");

    private ScheduleEditBaseline NewEditingBaseline(TournamentWorkspace workspace) =>
        new(this, scheduleEditingSession, workspace.Id, workspace.Schedule!.Revision, EditingSourceIdentity(workspace));

    private static ScheduleEditBaseline RequiredBaseline(MoveMatchRequest request)
    {
        Require(request.Baseline is not null, "schedule.edit-baseline-required", "请先基于当前赛程开始编辑，再选择目标位置。");
        return request.Baseline!;
    }

    private void RequireEditingBaseline(TournamentWorkspace workspace, ScheduleEditBaseline baseline)
    {
        Require(ReferenceEquals(baseline.Owner, this) && baseline.Session == scheduleEditingSession && baseline.WorkspaceId == workspace.Id,
            "schedule.edit-session-changed", "编辑所属工作区会话已关闭或切换，请重新选择目标。");
        Require(baseline.SourceIdentity == EditingSourceIdentity(workspace), "schedule.edit-stale",
            "编辑期间赛程、比赛关系、资源或赛果已变化，请保留输入并重新检查目标。");
    }

    private static void RequireEditingSource(TournamentWorkspace workspace, string sourceIdentity)
    {
        RequireEditingEligible(workspace);
        Require(EditingSourceIdentity(workspace) == sourceIdentity, "schedule.source-changed",
            "正式存档的赛程、比赛关系、资源或赛果已变化，请重新打开后再调整。");
    }

    private static TournamentSchedulingRequest EditingRequest(TournamentWorkspace workspace) => new(
        workspace.Projects.OrderBy(p => p.SortOrder).Select(p => p.MatchGraph!).ToArray(), workspace.Resources!, workspace.Schedule!.Policy)
    { Results = workspace.Results, BaselinePlacements = workspace.Schedule.Placements,
        ProjectNames = workspace.Projects.ToDictionary(p => p.Id, p => p.DisplayName), ScheduleRevision = workspace.Schedule.Revision };

    private static TournamentSchedule EditedSchedule(TournamentWorkspace source, IReadOnlyDictionary<Guid, MatchPlacement> placements) =>
        source.Schedule! with { Placements = placements, Revision = checked(Math.Max(source.Revision, source.Schedule!.Revision) + 1) };

    private static void ThrowIfEditInvalid(TournamentPlacementValidation validation, string code, string message)
    {
        if (validation.IsValid) return;
        throw new WorkspaceCommandException(new(code, message, SchedulingFailure: new(message, [], validation.Violations, [],
            ["检查目标时间、场地、后续依赖和当前赛果锁定。", "需要时预览连锁移动，或选择其他空位；不会自动移动其他项目比赛。"] )));
    }

    private static string Fingerprint<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static string EditingContextIdentity(TournamentWorkspace w) => Fingerprint(new
    { w.Id, Projects = w.Projects.OrderBy(p => p.Id), w.Resources, ScheduleResources = w.Schedule!.Resources, w.Schedule.Policy,
        Graphs = w.Schedule.GraphRevisions.OrderBy(p => p.Key) });
    private static string EditingScheduleIdentity(TournamentWorkspace w) => Fingerprint(new
    { Context = EditingContextIdentity(w), w.Schedule!.Revision, Placements = w.Schedule.Placements.OrderBy(p => p.Key) });
    private static string EditingSourceIdentity(TournamentWorkspace w) => Fingerprint(new
    { Schedule = EditingScheduleIdentity(w), Results = w.Results.OrderBy(p => p.Key.ProjectId).ThenBy(p => p.Key.MatchId) });

    // Future Restore/Recover commands call this after successful publication and before SessionChanged,
    // including a same-ID, same-placement restoration. Ordinary audit/result publications do not rotate it.
    private void InvalidateScheduleEditingSession() { scheduleEditingSession = Guid.NewGuid(); undoScheduleEdits = []; }
    private void ReconcileScheduleUndo(WorkspaceSession next)
    {
        if (undoScheduleEdits.Count == 0) return;
        try
        {
            if (next.RequiresReload || next.Workspace.Schedule is null || CurrentSession?.WorkspacePath != next.WorkspacePath ||
                undoScheduleEdits[^1].AfterIdentity != EditingScheduleIdentity(next.Workspace)) undoScheduleEdits = [];
        }
        catch { undoScheduleEdits = []; } // Ephemeral state can never make a saved publication fail.
    }
    private sealed record ScheduleUndoEntry(IReadOnlyDictionary<Guid, MatchPlacement> Before, string ContextIdentity, string AfterIdentity);
}
