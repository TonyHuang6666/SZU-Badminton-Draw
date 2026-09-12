using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed record WorkspaceBackupInfo(string FullPath, string ContentHash, TournamentWorkspace Workspace);
public sealed record WorkspaceBackupOutcome(WorkspaceCommandResult Command, WorkspaceBackupInfo Backup);

/// <summary>A surviving path on failure is not evidence of a complete or valid backup.</summary>
public sealed class WorkspaceBackupException(WorkspaceError error, string? manualBackupPath,
    WorkspaceBackupInfo? backup, Exception innerException) : Exception(error.Message, innerException)
{
    public WorkspaceError Error { get; } = error;
    public string? ManualBackupPath { get; } = manualBackupPath;
    public WorkspaceBackupInfo? Backup { get; } = backup;
}

public sealed class WorkspaceRestorePreview
{
    internal WorkspaceRestorePreview(TournamentWorkspaceWorkflow owner, WorkspaceSession source,
        string sourceIdentity, WorkspaceBackupInfo backup)
    { Owner = owner; Source = source; SourceIdentity = sourceIdentity; Backup = backup; }
    internal TournamentWorkspaceWorkflow Owner { get; }
    internal WorkspaceSession Source { get; }
    internal string SourceIdentity { get; }
    public string WorkspacePath => Source.WorkspacePath;
    public long SourceRevision => Source.Workspace.Revision;
    public WorkspaceBackupInfo Backup { get; }
}

public sealed class WorkspaceRecoveryPreview
{
    internal WorkspaceRecoveryPreview(TournamentWorkspaceWorkflow owner, WorkspaceSession? source,
        string workspacePath, string corruptContentHash, WorkspaceBackupInfo backup)
    { Owner = owner; Source = source; WorkspacePath = workspacePath; CorruptContentHash = corruptContentHash; Backup = backup; }
    internal TournamentWorkspaceWorkflow Owner { get; }
    internal WorkspaceSession? Source { get; }
    public string WorkspacePath { get; }
    public string CorruptContentHash { get; }
    public WorkspaceBackupInfo Backup { get; }
}
