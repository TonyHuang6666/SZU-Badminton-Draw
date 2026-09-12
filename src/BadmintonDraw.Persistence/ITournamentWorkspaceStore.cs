using BadmintonDraw.Core.Tournaments;
namespace BadmintonDraw.Persistence;

public interface ITournamentWorkspaceStore
{
    TournamentWorkspace Create(string path, TournamentWorkspace workspace);
    TournamentWorkspace Read(string path);
    WorkspaceMutationResult Mutate(string path, long expectedRevision, Func<TournamentWorkspace, TournamentWorkspace> mutation);
    string CreateBackup(string path);
    TournamentWorkspace RestoreBackup(string path, string backupPath);
    TournamentWorkspace RecoverFromBackup(string path, string backupPath);
}
