using BadmintonDraw.Core.Tournaments;
namespace BadmintonDraw.Persistence;

public sealed record WorkspaceMutationResult(TournamentWorkspace Workspace, string BackupPath);

/// <summary>File boundary for durable copies and atomic same-directory publication.</summary>
public class WorkspaceFileOperations
{
    public virtual void Copy(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output); output.Flush(true);
    }
    public virtual void Publish(string candidate, string destination, bool overwrite) => File.Move(candidate, destination, overwrite);
}
