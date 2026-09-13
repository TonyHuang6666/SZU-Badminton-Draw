using System.ComponentModel;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using BadmintonDraw.Tests;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.Tests;

internal sealed class ResultImportUiFixture : IDisposable
{
    internal WorkspaceResultImportFacadeFixture Data { get; }
    internal ImportUiFiles Files { get; } = new();
    internal ImportUiStore Store { get; }
    internal AppShellViewModel Shell { get; }
    internal WorkspaceResultImportViewModel ViewModel { get; }
    internal IReadOnlyList<string>? NextPick { get; set; }
    internal ResultImportUiFixture(int projects = 2, int entrants = 4,
        Func<Task<IReadOnlyList<string>?>>? picker = null, Action<Action>? post = null)
    {
        Store = new(Files); Data = new(projects, entrants, Store);
        Data.Store.Mutate(Data.Session.WorkspacePath, Data.Revision,
            w => w with { Projects = w.Projects.Select(p => p with { DisplayName = "同名项目" }).ToArray() });
        Data.Workflow.OpenWorkspace(Data.Session.WorkspacePath); Data.Store.Mutations = 0;
        Files.Armed = false; Files.Published = false;
        Shell = new(Data.Workflow, () => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null),
            new RecentWorkspaceStore(Data.PathFor("recent.json")), post ?? (action => action()));
        ViewModel = new(Shell, Shell.CurrentSession!, picker ?? (() => Task.FromResult(NextPick)));
        Shell.PropertyChanged += ForwardSession; // Leaf host: the future real parent forwards the same lifecycle.
    }
    private void ForwardSession(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Shell.CurrentSession) && Shell.CurrentSession is { } session) ViewModel.RefreshSession(session);
        if (args.PropertyName == nameof(Shell.IsBusy)) ViewModel.RefreshAvailability();
    }
    internal async Task Choose(params string[] paths)
    { NextPick = paths; await ViewModel.PickFilesCommand.ExecuteAsync(); }
    internal async Task Preview(params string[] paths)
    { await Choose(paths); await ViewModel.PreviewCommand.ExecuteAsync(); }
    internal async Task Accept()
    { ViewModel.Confirmed = true; await ViewModel.ConfirmImportCommand.ExecuteAsync(); }
    public void Dispose() { Shell.PropertyChanged -= ForwardSession; ViewModel.Dispose(); Shell.Dispose(); Data.Dispose(); }
}

internal sealed class ImportUiFiles : WorkspaceFileOperations
{
    internal bool Armed { get; set; }
    internal bool Published { get; set; }
    internal bool FailPublish { get; set; }
    internal bool PartialBackup { get; set; }
    public override void Copy(string source, string destination)
    {
        if (Armed && PartialBackup && destination.EndsWith(".backup.szbd", StringComparison.Ordinal))
        { File.WriteAllText(destination, "partial backup"); throw new IOException("injected partial backup"); }
        base.Copy(source, destination);
    }
    public override void Publish(string candidate, string destination, bool overwrite)
    {
        if (Armed && FailPublish) throw new IOException("injected publish failure");
        base.Publish(candidate, destination, overwrite); if (Armed) Published = true;
    }
}

internal sealed class ImportUiStore(ImportUiFiles files) : TournamentWorkspaceStore(files)
{
    internal Action<string>? BeforeRead { get; set; }
    internal bool FailCommittedRead { get; set; }
    internal int CommittedReadFailuresRemaining { get; set; }
    public override TournamentWorkspace Read(string path)
    {
        BeforeRead?.Invoke(path);
        if (files.Published && (FailCommittedRead || CommittedReadFailuresRemaining > 0) && !path.EndsWith(".candidate.szbd", StringComparison.Ordinal))
        { CommittedReadFailuresRemaining--; throw new WorkspaceStoreException("InvalidWorkspace", "injected committed re-read failure"); }
        return base.Read(path);
    }
}
