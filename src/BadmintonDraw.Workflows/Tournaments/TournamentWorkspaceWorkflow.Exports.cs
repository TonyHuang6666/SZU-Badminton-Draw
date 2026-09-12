using System.Text.Json;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class TournamentWorkspaceWorkflow
{
    public DrawPackageExportResult ExportDrawPackage(Guid? projectId, DrawExportRequest request, long expectedRevision)
    {
        var progress = new DrawPackageWorkflow.ExportProgress();
        var auditId = Guid.NewGuid();
        try
        {
            var command = Change(expectedRevision, workspace =>
            {
                var exportedAt = DateTimeOffset.UtcNow;
                drawPackages.Export(workspace, CurrentSession!.WorkspacePath, projectId, request, auditId, exportedAt, progress);
                var audit = new WorkspaceAuditEvent(auditId, "DrawPackageExported", exportedAt, projectId,
                    Detail: JsonSerializer.Serialize(new { SourceRevision = workspace.Revision, request.State, Outputs = progress.Outputs }));
                return workspace with { AuditEvents = [.. workspace.AuditEvents, audit] };
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
