using BadmintonDraw.Core.Tournaments;
namespace BadmintonDraw.Persistence;

public interface ITournamentWorkspaceStore
{
    TournamentWorkspace Create(string path, TournamentWorkspace workspace);
    TournamentWorkspace Read(string path);
    WorkspaceMutationResult Mutate(string path, long expectedRevision, Func<TournamentWorkspace, TournamentWorkspace> mutation);
    string CreateBackup(string path);
    WorkspaceBackupSnapshot InspectBackup(string backupPath);
    WorkspaceRecoveryInspection InspectRecovery(string path, string backupPath);
    WorkspaceMutationResult RestoreBackup(string path, WorkspaceRestoreRequest request);
    WorkspaceMutationResult RecoverFromBackup(string path, WorkspaceRecoveryRequest request);
}
