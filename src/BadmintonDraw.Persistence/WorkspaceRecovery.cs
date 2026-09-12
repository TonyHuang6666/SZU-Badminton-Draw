using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Persistence;

public sealed record WorkspaceBackupSnapshot(TournamentWorkspace Workspace, string FullPath, string ContentHash);
public sealed record WorkspaceRestoreRequest(string BackupPath, string BackupHash, Guid BackupWorkspaceId, long ExpectedRevision, string Reason);
public sealed record WorkspaceRecoveryInspection(string FullPath, string CorruptContentHash, WorkspaceBackupSnapshot BackupSnapshot);
public sealed record WorkspaceRecoveryRequest(string BackupPath, string BackupHash, Guid BackupWorkspaceId, string ExpectedCorruptContentHash, string Reason);
