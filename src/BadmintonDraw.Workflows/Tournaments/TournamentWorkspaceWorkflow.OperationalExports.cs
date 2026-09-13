using System.Text.Json;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class TournamentWorkspaceWorkflow
{
    public OperationalPackageOutcome ExportOperationalPackage(OperationalExportRequest request, long expectedRevision)
    {
        var progress = new OperationalPackageWorkflow.ExportProgress();
        var auditId = Guid.NewGuid();
        try
        {
            Require(request is not null, "export.request", "请选择导出范围。");
            // Also copy at the command boundary, before waiting for the captured-session gate.
            var selected = request! with { Days = request.Days?.ToArray() };
            var command = WithCapturedSession(captured =>
            {
                Require(captured.Workspace.Revision == expectedRevision, "RevisionConflict", "界面修订已变化，请重新检查导出范围。");
                var sourceIdentity = RecoverySourceIdentity(captured.Workspace);
                return CommitChange(captured, expectedRevision, source =>
                {
                    Require(RecoverySourceIdentity(source) == sourceIdentity, "export.source-changed",
                        "正式工作区内容已变化，请重新打开并检查后导出。");
                    var exportedAt = DateTimeOffset.UtcNow;
                    progress.SourceRevision = source.Revision; progress.ExportedAt = exportedAt;
                    operationalPackages.Export(source, captured.WorkspacePath, selected, auditId, exportedAt, progress);
                    var audit = new WorkspaceAuditEvent(auditId, "OperationalPackageExported", exportedAt, selected.ProjectId,
                        Detail: JsonSerializer.Serialize(new { SourceRevision = source.Revision, progress.Scope,
                            progress.Counts, progress.Skips, progress.Outputs }));
                    return source with { AuditEvents = [.. source.AuditEvents, audit] };
                });
            });
            return new(command, progress.SourceRevision!.Value, auditId, progress.ExportedAt!.Value,
                progress.Scope!, progress.Counts!, progress.Outputs, progress.Skips);
        }
        catch (Exception exception)
        {
            var error = WorkspaceCommandException.From(exception);
            throw new OperationalPackageExportException(error.Error, progress.SourceRevision, auditId, progress.ExportedAt,
                progress.Scope, progress.Counts, progress.Outputs, progress.Skips, progress.StagingDirectory,
                progress.AttemptedOutputPath, error);
        }
    }
}
