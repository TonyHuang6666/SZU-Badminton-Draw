using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class TournamentWorkspaceWorkflow
{
    /// <summary>Validate and atomically save one completed native row using the record-import rules.
    /// The receipt describes a captured editor submission; no spreadsheet file is created.</summary>
    public WorkspaceResultImportOutcome RecordOnsiteResult(WorkspaceOnsiteResultRequest request,
        long expectedRevision) => WithCapturedSession<WorkspaceResultImportOutcome>(captured =>
    {
        Require(request is not null && request.Winner is not null && request.Score is not null && request.Duration is not null,
            "onsite.input", "请填写完整的现场比赛结果。");
        var row = request!;
        var source = ReadImportSource(captured, expectedRevision);
        Require(source.Purpose == TournamentPurpose.FullTournament && source.Stage >= TournamentStage.ScheduleReady && source.Schedule is not null,
            "onsite.stage", "请先确认抽签并生成比赛赛程。");
        var project = source.Projects.SingleOrDefault(p => p.Id == row.Key.ProjectId);
        var node = project?.MatchGraph?.Matches.SingleOrDefault(n => n.Id == row.Key.MatchId && n.IsPlayable);
        Require(node is not null && project?.Draw?.ConfirmedAt is not null, "onsite.match-missing", "此场比赛已不在当前赛事中，请刷新现场记录表。");
        Require(project!.MatchGraph!.Revision == row.GraphRevision, "onsite.graph-stale", "此场比赛的抽签版本已变化，请刷新后重新填写。");
        Require(Enum.IsDefined(row.Kind), "onsite.kind", "结果类型只能选择正常或弃权。");
        Require(!string.IsNullOrWhiteSpace(row.Winner), "onsite.winner-required", "请选择本场比赛的胜方。");

        var a = ResolveOnsiteParticipant(node!.SideA, project.Id, source.Results);
        var b = ResolveOnsiteParticipant(node.SideB, project.Id, source.Results);
        Require(a is not null && b is not null, "onsite.upstream-missing", "本场参赛双方尚未确定，请先完成前序比赛的记录。");
        Require(a!.IdentityKey == row.SideAIdentityKey && b!.IdentityKey == row.SideBIdentityKey,
            "onsite.participants-changed", "本场参赛双方已变化，请核对刷新后的姓名再填写。");

        var sourceIdentity = RecoverySourceIdentity(source);
        var operationId = Guid.NewGuid();
        var recordedAt = DateTimeOffset.UtcNow;
        var placement = source.Schedule!.Placements[node.Id];
        if (!source.Results.TryGetValue(row.Key, out var savedResult))
        {
            var currentDay = WorkspaceDayConfirmationRules.NextUnconfirmedDay(source);
            Require(currentDay.HasValue && placement.DayLabel == currentDay.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) &&
                row.ActualPlayedDay == currentDay.Value, "onsite.day-locked",
                "请先确认前一个比赛日的结果；当前场次只能在已解锁的比赛日录入。延期场次请先通过当天确认办理编排。");
        }
        else if (WorkspaceDayConfirmationRules.IsConfirmed(source, DateOnly.ParseExact(placement.DayLabel, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
            Require(savedResult.ActualPlayedDay == row.ActualPlayedDay, "onsite.confirmed-day",
                "当天结果已确认，可以补充比分和时长，不能更改实际比赛日期。");
        WorkspaceRecordCell Cell(string value) => new(value);
        var evidence = new WorkspaceRecordRawRow(new("现场记录表", node.Order),
            Cell(source.Id.ToString("D")), Cell(project.Id.ToString("D")), Cell(node.Id.ToString("D")),
            Cell(project.MatchGraph.Revision), Cell(project.Draw!.ConfirmedAt!.Value.ToString("O", CultureInfo.InvariantCulture)),
            Cell(placement.DayLabel), Cell(row.ActualPlayedDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            Cell(row.Kind == TournamentResultKind.Walkover ? "弃权" : "正常"), Cell(row.Winner!), Cell(row.Score!), Cell(row.Duration!),
            Cell(a.DisplayName), Cell(b!.DisplayName), Cell(WorkspaceWinnerOptionText.Format(ScheduleMatchSide.SideA, a.DisplayName)),
            Cell(WorkspaceWinnerOptionText.Format(ScheduleMatchSide.SideB, b.DisplayName)));
        // A receipt identifies this submitted snapshot, not a fictional workbook. Include the operation so
        // an explicit correction back to an earlier value remains a new auditable submission.
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { OperationId = operationId, RecordedAt = recordedAt, Row = evidence })));
        var file = new WorkspaceRecordImportFile("现场记录表",
            $"onsite://{source.Id:D}/{project.Id:D}/{node.Id:D}/{operationId:D}", new(hash, [evidence]));
        var evaluation = new ResultImportWorkflow().Evaluate(source, [file],
            new(row.AllowCorrections, row.CorrectionReason, recordedAt, operationId));
        Require(evaluation.Status == ResultImportEvaluationStatus.Ready,
            evaluation.Status == ResultImportEvaluationStatus.RequiresConfirmation ? "onsite.correction-confirmation" : "onsite.rejected",
            "本场结果尚未保存。" + ImportDiagnosticText(evaluation));

        // Enter and row-focus events may both submit the same values. Equal normalized results never
        // create a receipt, backup, new revision or notification, even with different text formatting.
        if (evaluation.ProposedCounts.AddedResultCount == 0 && evaluation.ProposedCounts.CorrectionCount == 0)
        {
            var unchanged = new ResultImportEvaluation(ResultImportEvaluationStatus.NoChanges,
                evaluation.Diagnostics, [], [], new(0, 0, 0, 0, 0));
            return new(new(captured.Workspace, captured.WorkspacePath, null, []), unchanged);
        }
        var candidate = evaluation.Candidate! with
        {
            AuditEvents = evaluation.Candidate!.AuditEvents.Select(audit => audit.Id == operationId
                ? audit with { Action = evaluation.ProposedCounts.CorrectionCount > 0 ? "OnsiteResultCorrected" : "OnsiteResultRecorded",
                    ProjectId = project.Id, MatchId = node.Id }
                : audit).ToArray()
        };
        evaluation = new(ResultImportEvaluationStatus.Ready, evaluation.Diagnostics, evaluation.Files,
            evaluation.Corrections, evaluation.ProposedCounts, candidate);
        var command = CommitChange(captured, expectedRevision, current =>
        {
            RequireImportSource(current, sourceIdentity);
            return candidate;
        });
        return new(command, evaluation);
    });

    private static EntrantSource.Participant? ResolveOnsiteParticipant(EntrantSource entrant, Guid projectId,
        IReadOnlyDictionary<WorkspaceMatchKey, TournamentMatchResult> results) => entrant switch
    {
        EntrantSource.Participant participant => participant,
        EntrantSource.WinnerOf winner when results.TryGetValue(new(projectId, winner.MatchId), out var result) => result.Winner,
        EntrantSource.LoserOf loser when results.TryGetValue(new(projectId, loser.MatchId), out var result) => result.Loser,
        _ => null
    };
}
