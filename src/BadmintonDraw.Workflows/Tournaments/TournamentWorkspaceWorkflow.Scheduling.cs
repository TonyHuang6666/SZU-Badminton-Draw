using System.Text.Json;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class TournamentWorkspaceWorkflow
{
    /// <summary>
    /// Generates from all confirmed graphs before starting a durable mutation. A genuine existing schedule is
    /// an unlocked scoring reference; resources and policy may change. Any results forbid regeneration.
    /// Failure throws WorkspaceCommandException with optional typed SchedulingFailure and leaves the archive intact.
    /// Desktop callers should run this synchronous search off the UI thread.
    /// </summary>
    public WorkspaceCommandResult GenerateSchedule(TournamentResourcePlan resources, TournamentSchedulingPolicy policy,
        long expectedRevision) => WithCapturedSession(captured =>
    {
        var source = captured.Workspace;
        RequireSchedulingRevision(source, expectedRevision);
        RequireSchedulingEligible(source);
        var sourceIdentity = SchedulingSourceIdentity(source);

        // Avoid searching an already stale snapshot. The store's own revision check and callback checks below
        // are still necessary: a different process can update or replace the archive while the search runs.
        var durable = store.Read(captured.WorkspacePath);
        RequireWorkspaceIdentity(durable, captured);
        RequireSchedulingRevision(durable, expectedRevision);
        RequireSchedulingSource(durable, sourceIdentity);

        var request = new TournamentSchedulingRequest(source.Projects.OrderBy(p => p.SortOrder).Select(p => p.MatchGraph!).ToArray(),
            resources, policy)
        {
            ProjectNames = source.Projects.ToDictionary(p => p.Id, p => p.DisplayName),
            BaselinePlacements = source.Schedule?.Placements,
            // Workspace revision is a durable floor even when reopening draws cleared the previous schedule.
            // Revisions are monotonic identity tokens, not counts of Generate invocations.
            ScheduleRevision = checked(Math.Max(source.Revision, source.Schedule?.Revision ?? 0) + 1)
        };
        var result = new TournamentScheduler().Generate(request);
        if (result is TournamentSchedulingResult.Failure failure)
            throw new WorkspaceCommandException(new("schedule.generation-failed", failure.Detail.Message,
                SchedulingFailure: failure.Detail));
        var success = (TournamentSchedulingResult.Success)result;

        // No candidate, backup, audit or Mutate call exists before a complete successful search.
        return CommitChange(captured, expectedRevision, workspace =>
        {
            RequireSchedulingSource(workspace, sourceIdentity);
            return Audit(workspace with
            {
                Schedule = success.Schedule,
                Resources = success.Schedule.Resources,
                Stage = TournamentStage.ScheduleReady
            }, "ScheduleGenerated", detail: "赛程修订号：" + success.Schedule.Revision);
        });
    });

    private static void RequireSchedulingRevision(TournamentWorkspace workspace, long expectedRevision) =>
        Require(workspace.Revision == expectedRevision, "RevisionConflict", "工作区修订号已变化，请重新打开后再操作。");

    private static void RequireSchedulingEligible(TournamentWorkspace workspace)
    {
        Require(workspace.Purpose == TournamentPurpose.FullTournament, "stage.draw-only",
            "仅公开抽签赛事需要先升级为完整赛事才能编排赛程。");
        Require(workspace.Results.Count == 0, "schedule.results", "已有赛果，不能重新生成赛程。");
        Require(workspace.Stage is TournamentStage.DrawsConfirmed or TournamentStage.ScheduleReady &&
            workspace.Projects.All(p => p.Draw?.ConfirmedAt is not null && p.MatchGraph is not null),
            "stage.draws", "请先确认全部项目抽签，再生成全局赛程。");
    }

    private static void RequireSchedulingSource(TournamentWorkspace workspace, string identity)
    {
        RequireSchedulingEligible(workspace);
        Require(SchedulingSourceIdentity(workspace) == identity, "schedule.source-changed",
            "已确认抽签、比赛关系图或原赛程已变化，请重新打开后再生成赛程。");
    }

    // Compare complete immutable source values, not record/list reference equality or declared revision alone.
    // Includes graph node/source identities, confirmed draw/roster provenance, and the genuine baseline.
    private static string SchedulingSourceIdentity(TournamentWorkspace workspace) =>
        JsonSerializer.Serialize(new { workspace.Projects, workspace.Schedule });
}
