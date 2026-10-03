using System.Text.Json;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class TournamentWorkspaceWorkflow
{
    public IReadOnlyList<string> PreviewDrawExportConflicts(Guid? projectId, DrawExportRequest request, long expectedRevision) =>
        WithCapturedSession(captured =>
        {
            Require(request is not null, "export.request", "请选择导出范围。");
            return drawPackages.PreviewConflicts(ReadExportSource(captured, expectedRevision), captured.WorkspacePath,
                projectId, request!);
        });

    private TournamentWorkspace ReadExportSource(WorkspaceSession captured, long expectedRevision)
    {
        Require(captured.Workspace.Revision == expectedRevision, "RevisionConflict", "界面修订已变化，请重新检查导出范围。");
        var source = store.Read(captured.WorkspacePath);
        RequireWorkspaceIdentity(source, captured);
        Require(source.Revision == expectedRevision, "RevisionConflict", "工作区已被其他操作修改，请重新打开并检查。");
        Require(RecoverySourceIdentity(source) == RecoverySourceIdentity(captured.Workspace), "export.source-changed",
            "正式工作区内容已变化，请重新打开并检查后导出。");
        return source;
    }

    public DrawPackageExportResult ExportDrawPackage(Guid? projectId, DrawExportRequest request, long expectedRevision)
    {
        var progress = new DrawPackageWorkflow.ExportProgress();
        var auditId = Guid.NewGuid();
        try
        {
            var command = WithCapturedSession(captured =>
            {
                Require(captured.Workspace.Revision == expectedRevision, "RevisionConflict", "界面修订已变化，请重新检查导出范围。");
                var sourceIdentity = RecoverySourceIdentity(captured.Workspace);
                return CommitChange(captured, expectedRevision, workspace =>
                {
                    Require(RecoverySourceIdentity(workspace) == sourceIdentity, "export.source-changed",
                        "正式工作区内容已变化，请重新打开并检查后导出。");
                    var exportedAt = DateTimeOffset.UtcNow;
                    drawPackages.Export(workspace, captured.WorkspacePath, projectId, request, auditId, exportedAt, progress);
                    var audit = new WorkspaceAuditEvent(auditId, "DrawPackageExported", exportedAt, projectId,
                        Detail: JsonSerializer.Serialize(new { SourceRevision = workspace.Revision, request.State, Outputs = progress.Outputs }));
                    return workspace with { AuditEvents = [.. workspace.AuditEvents, audit] };
                });
            });
            return new(command, Array.AsReadOnly(progress.Outputs.ToArray()), expectedRevision, auditId);
        }
        catch (WorkspaceCommandException exception)
        {
            throw new DrawPackageExportException(exception.Error, progress.Outputs, progress.StagingDirectory, exception);
        }
    }

    public WorkspaceCommandResult ExportRosterTemplate(Guid projectId, string outputPath, long expectedRevision,
        bool overwriteExisting = false)
    {
        var progress = new DrawPackageWorkflow.ExportProgress();
        try
        {
            return Change(expectedRevision, workspace =>
            {
                drawPackages.ExportTemplate(workspace, CurrentSession!.WorkspacePath, projectId, outputPath, overwriteExisting, progress);
                return Audit(workspace, "RosterTemplateExported", projectId, progress.Outputs[0].Path);
            });
        }
        catch (WorkspaceCommandException exception)
        {
            throw new DrawPackageExportException(exception.Error, progress.Outputs, progress.StagingDirectory, exception);
        }
    }
}
