using System.Security.Cryptography;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.Tests;

internal sealed class RecoveryUiFixture : IDisposable
{
    public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("recovery-ui-").FullName;
    public RecoveryFaultFiles Files { get; } = new();
    public RecoveryFaultStore Store { get; }
    public TournamentWorkspaceWorkflow Workflow { get; }
    public AppShellViewModel Shell { get; }
    public RecoveryUiFixture(Func<Task<string?>>? targetPicker = null, Func<Task<string?>>? backupPicker = null,
        Func<Task<string?>>? openPicker = null)
    {
        Store = new(Files); Workflow = new(Store);
        Shell = new(Workflow, openPicker ?? (() => Task.FromResult<string?>(null)), _ => Task.FromResult<string?>(null),
            new RecentWorkspaceStore(PathFor("recent.json")), action => action(), targetPicker, backupPicker);
    }
    public string PathFor(string name) => Path.Combine(DirectoryPath, name);
    public CreateWorkspaceRequest Request(string name) => new(name, TournamentKind.Individual,
        TournamentPurpose.PublicDrawOnly, [new(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout)], PathFor(name + ".szbd"));
    public async Task<string> CreateCurrent()
    {
        await Shell.CreateWorkspaceAsync(Request("current"));
        Files.Published = false;
        return new TournamentWorkspaceStore().CreateBackup(Shell.WorkspacePath);
    }
    public (string Target, string Backup) CorruptTarget()
    {
        var request = Request("damaged"); new TournamentWorkspaceWorkflow().CreateWorkspace(request);
        var backup = new TournamentWorkspaceStore().CreateBackup(request.WorkspacePath);
        File.WriteAllText(request.WorkspacePath, "damaged exact bytes");
        return (request.WorkspacePath, backup);
    }
    public static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    public void Dispose() { Shell.Dispose(); Directory.Delete(DirectoryPath, true); }
}

internal sealed class RecoveryFaultFiles : WorkspaceFileOperations
{
    public bool FailPublish { get; set; }
    public bool PartialBackup { get; set; }
    public bool Published { get; set; }
    public override void Copy(string source, string destination)
    {
        if (PartialBackup && destination.EndsWith(".backup.szbd", StringComparison.Ordinal))
        { File.WriteAllText(destination, "partial copy"); throw new IOException("copy failed"); }
        base.Copy(source, destination);
    }
    public override void Publish(string candidate, string destination, bool overwrite)
    {
        if (FailPublish) throw new IOException("publish failed");
        base.Publish(candidate, destination, overwrite); Published = true;
    }
}

internal sealed class RecoveryFaultStore(RecoveryFaultFiles files) : TournamentWorkspaceStore(files)
{
    public bool FailCommittedRead { get; set; }
    public Action<string>? BeforeRead { get; set; }
    public override TournamentWorkspace Read(string path)
    {
        BeforeRead?.Invoke(path);
        if (FailCommittedRead && files.Published && !path.EndsWith(".candidate.szbd", StringComparison.Ordinal))
            throw new WorkspaceStoreException("InvalidWorkspace", "post-commit read failure");
        return base.Read(path);
    }
}
