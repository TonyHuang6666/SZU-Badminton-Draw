using System.Security.Cryptography;
using System.Text.Json;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Persistence;

public partial class TournamentWorkspaceStore
{
    /// <summary>Parse and hash the same captured bytes; never trust a separately re-read mutable backup path.</summary>
    public WorkspaceBackupSnapshot InspectBackup(string backupPath)
    {
        var full = Path.GetFullPath(backupPath);
        var directory = Directory.CreateTempSubdirectory("szbd-backup-snapshot-").FullName;
        try
        {
            var snapshot = Path.Combine(directory, "snapshot.szbd");
            files.Copy(full, snapshot);
            var hash = ContentHash(snapshot);
            return new(Read(snapshot), full, hash);
        }
        catch (WorkspaceStoreException) { throw; }
        catch (Exception exception) { throw new WorkspaceStoreException("BackupReadFailed", "无法完整读取备份：" + exception.Message, exception); }
        finally { Directory.Delete(directory, true); }
    }

    public WorkspaceRecoveryInspection InspectRecovery(string path, string backupPath) => Locked(path, full =>
    {
        RequireDifferentPath(full, backupPath);
        var hash = UnreadableHash(full);
        var backup = InspectBackup(backupPath);
        RequireHash(full, hash, "RecoverySourceChanged", "损坏文件已变化，请重新检查。");
        return new WorkspaceRecoveryInspection(full, hash, backup);
    });

    public WorkspaceMutationResult RestoreBackup(string path, WorkspaceRestoreRequest request) => Locked(path, full =>
    {
        ValidateConfirmation(request.BackupHash, request.BackupWorkspaceId, request.Reason);
        RequireDifferentPath(full, request.BackupPath);
        var hash = ContentHash(full);
        var current = Read(full);
        RequireHash(full, hash, "RecoverySourceChanged", "目标工作区在读取修订号期间变化，请重新预览。");
        if (current.Revision != request.ExpectedRevision)
            throw new WorkspaceStoreException("RevisionConflict", "工作区已变化，请重新预览并确认恢复。");
        var backup = ConfirmBackup(request.BackupPath, request.BackupHash, request.BackupWorkspaceId);
        if (current.Id != backup.Workspace.Id) throw new WorkspaceStoreException("WorkspaceIdentityMismatch", "备份属于其他赛事。");
        var next = AddRestoreAudit(backup.Workspace, "WorkspaceRestored", request.Reason,
            new { BackupHash = backup.ContentHash, PreviousRevision = current.Revision, BackupWorkspaceId = backup.Workspace.Id });
        return Commit(full, next, current, null, true, beforePublish: preserved =>
            VerifyRestoreSources(full, hash, backup, preserved));
    });

    public WorkspaceMutationResult RecoverFromBackup(string path, WorkspaceRecoveryRequest request) => Locked(path, full =>
    {
        ValidateConfirmation(request.BackupHash, request.BackupWorkspaceId, request.Reason);
        if (!ValidHash(request.ExpectedCorruptContentHash))
            throw new WorkspaceStoreException("RecoveryConfirmationInvalid", "必须确认损坏文件的完整哈希。");
        RequireDifferentPath(full, request.BackupPath);
        var hash = UnreadableHash(full);
        if (!SameHash(hash, request.ExpectedCorruptContentHash))
            throw new WorkspaceStoreException("RecoverySourceChanged", "损坏文件已变化，请重新预览并确认。");
        var backup = ConfirmBackup(request.BackupPath, request.BackupHash, request.BackupWorkspaceId);
        var next = AddRestoreAudit(backup.Workspace, "WorkspaceRecovered", request.Reason,
            new { CorruptContentHash = hash, BackupHash = backup.ContentHash, BackupWorkspaceId = backup.Workspace.Id });
        // The corrupt source provides no trusted revision. Preserve the existing high-water stale-session defense.
        var revision = Math.Max(checked(backup.Workspace.Revision + 1), DateTimeOffset.UtcNow.UtcTicks);
        return Commit(full, next, backup.Workspace, null, true, revision, preserved =>
        {
            RequireUnreadable(full);
            VerifyRestoreSources(full, hash, backup, preserved);
        });
    });

    private WorkspaceBackupSnapshot ConfirmBackup(string path, string expectedHash, Guid expectedId)
    {
        var backup = InspectBackup(path);
        if (!SameHash(backup.ContentHash, expectedHash)) throw new WorkspaceStoreException("BackupChanged", "选定备份内容已变化，请重新预览。");
        if (backup.Workspace.Id != expectedId) throw new WorkspaceStoreException("WorkspaceIdentityMismatch", "备份赛事身份与确认内容不符。");
        return backup;
    }

    private static void VerifyRestoreSources(string formal, string expectedHash, WorkspaceBackupSnapshot backup, string? preserved)
    {
        RequireHash(formal, expectedHash, "RecoverySourceChanged", "目标工作区在恢复期间变化，未覆盖新内容。");
        RequireHash(backup.FullPath, backup.ContentHash, "BackupChanged", "备份在恢复期间变化，请重新确认。");
        if (preserved is null) throw new WorkspaceStoreException("BackupFailed", "缺少被替换文件的恢复副本。");
        RequireHash(preserved, expectedHash, "BackupFailed", "被替换文件的恢复副本校验失败。");
    }

    private string UnreadableHash(string path)
    {
        var hash = ContentHash(path);
        RequireUnreadable(path);
        RequireHash(path, hash, "RecoverySourceChanged", "损坏文件在读取期间变化，请重新检查。");
        return hash;
    }

    private void RequireUnreadable(string path)
    {
        try { Read(path); }
        catch (WorkspaceStoreException exception) when (exception.Code == "InvalidWorkspace") { return; }
        throw new WorkspaceStoreException("RecoveryNotRequired", "工作区可读，请使用正常恢复操作。");
    }

    private static TournamentWorkspace AddRestoreAudit(TournamentWorkspace workspace, string action, string reason, object source) =>
        workspace with { AuditEvents = [..workspace.AuditEvents,
            new(Guid.NewGuid(), action, DateTimeOffset.UtcNow, Detail: JsonSerializer.Serialize(new { Reason = reason.Trim(), Source = source }))] };

    private static void ValidateConfirmation(string hash, Guid id, string reason)
    {
        if (!ValidHash(hash) || id == Guid.Empty || string.IsNullOrWhiteSpace(reason))
            throw new WorkspaceStoreException("RecoveryConfirmationInvalid", "恢复需要备份哈希、赛事身份和明确的确认原因。");
    }

    private static bool ValidHash(string hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static bool SameHash(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static void RequireDifferentPath(string formal, string backup)
    {
        if (string.Equals(formal, Path.GetFullPath(backup), StringComparison.OrdinalIgnoreCase))
            throw new WorkspaceStoreException("BackupSourceIsDestination", "请选择独立的备份文件，不能把目标工作区本身作为备份。");
    }
    private static string ContentHash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new WorkspaceStoreException("RecoveryReadFailed", "无法校验恢复源文件：" + exception.Message, exception); }
    }
    private static void RequireHash(string path, string expectedHash, string code, string message)
    { if (!SameHash(ContentHash(path), expectedHash)) throw new WorkspaceStoreException(code, message); }
}
