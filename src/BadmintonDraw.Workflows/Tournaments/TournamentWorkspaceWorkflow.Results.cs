using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class TournamentWorkspaceWorkflow
{
    public WorkspaceResultImportPreview PreviewResultImport(IReadOnlyList<string> paths)
    {
        // Snapshot the caller's selection before waiting for another command's session gate.
        var selected = paths?.ToArray() ?? throw new WorkspaceCommandException(new("results.read", "请选择记录表文件。"));
        return WithCapturedSession(captured =>
        {
            var source = ReadImportSource(captured, captured.Workspace.Revision);
            var files = CaptureResultFiles(selected);
            var evaluation = new ResultImportWorkflow().Evaluate(source, files, new(false, null, DateTimeOffset.UtcNow, Guid.NewGuid()));
            return new WorkspaceResultImportPreview(this, captured, RecoverySourceIdentity(source),
                files.Select(f => new WorkspaceResultImportFileInfo(f.SourcePath, f.SourceFileName, f.Document.ContentHash)), evaluation);
        });
    }

    public WorkspaceResultImportOutcome ImportResults(WorkspaceResultImportPreview preview,
        ResultCorrectionConfirmation confirmation, long expectedRevision) => WithCapturedSession(captured =>
    {
        Require(preview is not null && ReferenceEquals(preview.Owner, this) && ReferenceEquals(preview.Source, captured),
            "results.preview-session-changed", "导入预览所属会话已变化，请保留原文件选择并重新预览。");
        Require(preview!.SourceRevision == expectedRevision, "results.preview-stale", "导入修订与预览不一致，请重新预览。");
        Require(confirmation is not null, "results.confirmation", "请明确确认本次导入。");
        Require(preview.Evaluation.Status != ResultImportEvaluationStatus.Rejected, "results.preview-rejected",
            "预览包含不能接受的记录，请修正文件后重新预览。" + ImportDiagnosticText(preview.Evaluation));
        var source = ReadImportSource(captured, expectedRevision);
        RequireImportSource(source, preview.SourceIdentity);

        var files = CaptureResultFiles(preview.Files.Select(f => f.FullPath).ToArray());
        for (var i = 0; i < files.Count; i++)
            Require(string.Equals(files[i].Document.ContentHash, preview.Files[i].ContentHash, StringComparison.OrdinalIgnoreCase),
                "results.file-changed", "记录表在预览后已变化，请重新预览：“" + preview.Files[i].FullPath + "”。");

        var evaluation = new ResultImportWorkflow().Evaluate(source, files,
            new(confirmation!.AllowCorrections, confirmation.Reason, DateTimeOffset.UtcNow, Guid.NewGuid()));
        Require(CorrectionIdentity(evaluation) == CorrectionIdentity(preview.Evaluation), "results.correction-changed",
            "更正前后内容与已检查的预览不同，请重新预览并核对。");
        Require(evaluation.Status is ResultImportEvaluationStatus.Ready or ResultImportEvaluationStatus.NoChanges,
            evaluation.Status == ResultImportEvaluationStatus.RequiresConfirmation ? "results.correction-confirmation" : "results.rejected",
            "本次导入未保存。" + ImportDiagnosticText(evaluation));
        if (evaluation.Status == ResultImportEvaluationStatus.NoChanges)
        {
            // Do not Publish: even a same-workspace notification would imply a newly saved session.
            return new WorkspaceResultImportOutcome(new(captured.Workspace, captured.WorkspacePath, null, []), evaluation);
        }
        var command = CommitChange(captured, expectedRevision, current =>
        {
            RequireImportSource(current, preview.SourceIdentity);
            return evaluation.Candidate!; // Complete validated results + receipts + history + coverage + one audit.
        });
        return new WorkspaceResultImportOutcome(command, evaluation);
    });

    private TournamentWorkspace ReadImportSource(WorkspaceSession captured, long expectedRevision)
    {
        Require(captured.Workspace.Revision == expectedRevision, "RevisionConflict", "界面修订已变化，请重新预览导入。");
        var source = store.Read(captured.WorkspacePath);
        RequireWorkspaceIdentity(source, captured);
        Require(source.Revision == expectedRevision, "RevisionConflict", "正式工作区已由其他操作修改，请重新打开并预览。");
        RequireImportSource(source, RecoverySourceIdentity(captured.Workspace));
        return source;
    }

    private static void RequireImportSource(TournamentWorkspace workspace, string identity) =>
        Require(RecoverySourceIdentity(workspace) == identity, "results.source-changed",
            "正式工作区的内容已变化，请重新打开并检查赛果导入预览。");

    private static IReadOnlyList<WorkspaceRecordImportFile> CaptureResultFiles(IReadOnlyList<string> paths)
    {
        var files = new List<WorkspaceRecordImportFile>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                var fullPath = Path.GetFullPath(path);
                if (!string.Equals(Path.GetExtension(fullPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
                    throw new ExcelImportException("仅支持 .xlsx 记录表。");
                // The reader hashes and parses its own private copy of this one byte capture; never reopens the path.
                var document = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(File.ReadAllBytes(fullPath));
                files.Add(new(Path.GetFileName(fullPath), fullPath, document));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ExcelImportException
                or ArgumentException or NotSupportedException)
            {
                throw new WorkspaceCommandException(new("results.read", "无法读取记录表“" + (path ?? "<空路径>") + "”：" + exception.Message), exception);
            }
        }
        return files.AsReadOnly();
    }

    // Acceptance timestamps and generated receipt/audit IDs intentionally differ from preview. The complete
    // historical Before (including RecordedAt), semantic After and precise attribution must remain identical.
    private static string CorrectionIdentity(ResultImportEvaluation evaluation) => Fingerprint(evaluation.Corrections
        .OrderBy(c => c.Key.ProjectId).ThenBy(c => c.Key.MatchId).Select(c => new
        {
            c.Key, c.Before, After = new { c.After.Winner, c.After.Loser, c.After.Kind, c.After.Score,
                c.After.DurationMinutes, c.After.ActualPlayedDay }, c.Source
        }));

    private static string ImportDiagnosticText(ResultImportEvaluation evaluation) => string.Join("\n", evaluation.Diagnostics
        .Where(d => d.Severity == ResultImportDiagnosticSeverity.Blocking).Take(5).Select(d =>
            (d.Source is { } s ? s.SourceFileName + (s.Location is { } l ? $" / {l.SheetName} 第{l.RowNumber}行：" : "：") : "") + d.Message));
}
