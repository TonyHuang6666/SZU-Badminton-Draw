using System.Text.Json;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class TournamentWorkspaceWorkflow
{
    public WorkspaceRestorePreview PreviewRestoreBackup(string backupPath) => WithCapturedSession(captured =>
    {
        var source = ReadRecoverySource(captured, captured.Workspace.Revision);
        RequireSeparateBackup(captured.WorkspacePath, backupPath);
        var backup = BackupInfo(store.InspectBackup(backupPath));
        Require(backup.Workspace.Id == source.Id, "WorkspaceIdentityMismatch", "选定备份属于其他赛事，不能覆盖当前工作区。");
        return new WorkspaceRestorePreview(this, captured, RecoverySourceIdentity(source), backup);
    });

    public WorkspaceCommandResult RestoreBackup(WorkspaceRestorePreview preview, string reason, long expectedRevision) =>
        WithCapturedSession(captured =>
        {
            Require(preview is not null && ReferenceEquals(preview.Owner, this) && ReferenceEquals(preview.Source, captured),
                "recovery.preview-session-changed", "恢复预览所属会话已变化，请重新预览备份。");
            Require(preview!.SourceRevision == expectedRevision, "recovery.preview-stale", "请按当前修订重新预览并确认恢复。");
            RequireRecoveryReason(reason);
            var source = ReadRecoverySource(captured, expectedRevision);
            Require(RecoverySourceIdentity(source) == preview.SourceIdentity, "recovery.source-changed", "工作区内容已变化，请重新预览恢复。");
            try
            {
                var result = store.RestoreBackup(captured.WorkspacePath, new(preview.Backup.FullPath,
                    preview.Backup.ContentHash, preview.Backup.Workspace.Id, expectedRevision, reason.Trim()));
                InvalidateScheduleEditingSession();
                return Publish(result.Workspace, captured.WorkspacePath, result.BackupPath);
            }
            catch (WorkspaceStoreException exception) when (exception.Committed)
            {
                // WithCapturedSession refreshes the durable snapshot after this invalidation, before notifying observers.
                InvalidateScheduleEditingSession();
                throw;
            }
        });

    public WorkspaceRecoveryPreview PreviewRecovery(string workspacePath, string backupPath)
    {
        var captured = CurrentSession;
        lock (sessionGate)
        {
            try
            {
                RequireRecoverySession(captured);
                var inspection = store.InspectRecovery(workspacePath, backupPath);
                return new(this, captured, inspection.FullPath, inspection.CorruptContentHash, BackupInfo(inspection.BackupSnapshot));
            }
            catch (Exception exception) { throw WorkspaceCommandException.From(exception); }
        }
    }

    public WorkspaceCommandResult RecoverFromBackup(WorkspaceRecoveryPreview preview, string reason)
    {
        var captured = CurrentSession;
        lock (sessionGate)
        {
            try
            {
                RequireRecoverySession(captured);
                Require(preview is not null && ReferenceEquals(preview.Owner, this) && ReferenceEquals(preview.Source, captured),
                    "recovery.preview-session-changed", "恢复预览所属会话已变化，请重新检查损坏文件与备份。");
                RequireRecoveryReason(reason);
                var result = store.RecoverFromBackup(preview!.WorkspacePath, new(preview.Backup.FullPath,
                    preview.Backup.ContentHash, preview.Backup.Workspace.Id, preview.CorruptContentHash, reason.Trim()));
                InvalidateScheduleEditingSession();
                return Publish(result.Workspace, preview.WorkspacePath, result.BackupPath);
            }
            catch (Exception exception)
            {
                if (exception is WorkspaceStoreException { Committed: true } && preview is not null)
                {
                    InvalidateScheduleEditingSession();
                    // Recovery has an explicit target and may have begun without a readable current session.
                    RefreshAfterCommit(new(preview.Backup.Workspace, preview.WorkspacePath));
                }
                throw WorkspaceCommandException.From(exception);
            }
        }
    }

    public WorkspaceBackupOutcome CreateBackup(long expectedRevision)
    {
        string? manualPath = null;
        WorkspaceBackupInfo? backup = null;
        try
        {
            return WithCapturedSession(captured =>
            {
                var source = ReadRecoverySource(captured, expectedRevision);
                var identity = RecoverySourceIdentity(source);
                // CreateBackup acquires its own file lease: it must not be nested inside Mutate.
                manualPath = store.CreateBackup(captured.WorkspacePath);
                backup = BackupInfo(store.InspectBackup(manualPath));
                Require(RecoverySourceIdentity(backup.Workspace) == identity, "backup.source-changed",
                    "创建副本期间工作区已变化；副本已保留，请检查其赛事与修订后重新操作。");
                var command = CommitChange(captured, expectedRevision, current =>
                {
                    Require(RecoverySourceIdentity(current) == identity, "backup.source-changed",
                        "备份已创建，但正式工作区已变化，未写入本次备份审计。");
                    return Audit(current, "WorkspaceBackupCreated", detail: JsonSerializer.Serialize(new
                        { backup.FullPath, backup.ContentHash, SourceRevision = backup.Workspace.Revision }));
                });
                return new WorkspaceBackupOutcome(command, backup);
            });
        }
        catch (WorkspaceCommandException exception)
        {
            // If copying itself failed, the store may still report a real but unvalidated partial file.
            throw new WorkspaceBackupException(exception.Error, manualPath ?? exception.Error.BackupPath, backup, exception);
        }
    }

    private TournamentWorkspace ReadRecoverySource(WorkspaceSession captured, long expectedRevision)
    {
        Require(captured.Workspace.Revision == expectedRevision, "RevisionConflict", "当前界面修订已变化，请重新打开并检查。");
        var durable = store.Read(captured.WorkspacePath);
        RequireWorkspaceIdentity(durable, captured);
        Require(durable.Revision == expectedRevision, "RevisionConflict", "工作区已被其他操作修改，请重新打开并检查。");
        Require(RecoverySourceIdentity(durable) == RecoverySourceIdentity(captured.Workspace),
            "recovery.source-changed", "正式工作区与当前界面内容不一致，请重新打开。");
        return durable;
    }

    private void RequireRecoverySession(WorkspaceSession? captured)
    {
        RequireOutsideNotification();
        Require(ReferenceEquals(captured, currentSession), "workspace.session-changed", "当前工作区已切换，请重新检查恢复目标。");
    }
    private static void RequireRecoveryReason(string reason) => Require(!string.IsNullOrWhiteSpace(reason),
        "recovery.reason-required", "请填写恢复原因并确认选定备份的赛事身份。");
    private static void RequireSeparateBackup(string workspacePath, string backupPath) => Require(
        !string.Equals(workspacePath, Path.GetFullPath(backupPath), StringComparison.OrdinalIgnoreCase),
        "BackupSourceIsDestination", "请选择独立备份文件，不能把目标工作区本身作为备份。");
    private static WorkspaceBackupInfo BackupInfo(WorkspaceBackupSnapshot snapshot) =>
        new(snapshot.FullPath, snapshot.ContentHash, snapshot.Workspace);
    private static string RecoverySourceIdentity(TournamentWorkspace workspace) => Fingerprint(new
    {
        workspace.Id, workspace.Name, workspace.Kind, workspace.Purpose, workspace.Stage, workspace.Projects,
        workspace.Resources, workspace.Schedule,
        Results = workspace.Results.OrderBy(p => p.Key.ProjectId).ThenBy(p => p.Key.MatchId),
        workspace.ProcessedDays, workspace.ImportLogs, workspace.ResultHistory, workspace.AuditEvents,
        workspace.CreatedAt, workspace.UpdatedAt, workspace.Revision
    });
}
