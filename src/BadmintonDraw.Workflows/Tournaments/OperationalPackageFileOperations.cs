namespace BadmintonDraw.Workflows.Tournaments;

public class OperationalPackageFileOperations
{
    public virtual Stream OpenRead(string path) => File.OpenRead(path);
    public virtual void Publish(string stagedPath, string destination, bool overwrite) => File.Move(stagedPath, destination, overwrite);
    public virtual void DeleteStagingDirectory(string path) => Directory.Delete(path, true);
}
