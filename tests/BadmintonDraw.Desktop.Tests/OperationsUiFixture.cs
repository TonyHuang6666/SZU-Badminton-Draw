using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using BadmintonDraw.Tests;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.Tests;

internal sealed class OperationsUiFixture : IDisposable
{
    internal WorkspaceResultImportFacadeFixture Data { get; }
    internal TournamentWorkspaceWorkflow Workflow { get; }
    internal AppShellViewModel Shell { get; }
    internal OperationsPageViewModel Page => (OperationsPageViewModel)Shell.CurrentPage;
    internal IReadOnlyList<string>? NextFiles { get; set; }
    internal string? NextOutput { get; set; }
    internal OperationsUiFixture(int projects = 1, int entrants = 2, Action<Action>? post = null,
        Func<Task<string?>>? outputPicker = null, OperationalPackageFileOperations? files = null,
        ITournamentWorkspaceStore? store = null)
    {
        Data = new(projects, entrants, store);
        Data.Store.Mutate(Data.Session.WorkspacePath, Data.Revision,
            w => w with { Projects = w.Projects.Select(p => p with { DisplayName = "同名项目" }).ToArray() });
        Workflow = new(Data.Store, operationalPackages: new(files)); Workflow.OpenWorkspace(Data.Session.WorkspacePath);
        Shell = new(Workflow, () => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null),
            new RecentWorkspaceStore(Data.PathFor("ops-recent.json")), post ?? (action => action()));
        // Parent lifecycle unit host; actual Window factory/template is separately exercised headlessly.
        Shell.RegisterPageFactory(WorkspaceRoute.Operations, s => new OperationsPageViewModel(Shell, s,
            () => Task.FromResult(NextFiles), outputPicker ?? (() => Task.FromResult(NextOutput))));
        Xunit.Assert.True(Shell.Navigate(WorkspaceRoute.Operations));
    }
    internal string Record(string name = "records.xlsx", bool fill = true)
    { Data.Workflow.OpenWorkspace(Workflow.CurrentSession!.WorkspacePath); return Data.Export(name, fill); }
    internal async Task Import(string path)
    {
        NextFiles = [path]; await Page.ResultImport.PickFilesCommand.ExecuteAsync();
        await Page.ResultImport.PreviewCommand.ExecuteAsync(); Page.ResultImport.Confirmed = true;
        await Page.ResultImport.ConfirmImportCommand.ExecuteAsync();
    }
    internal void PrepareExport(string name = "materials")
    { Page.Materials.OutputDirectory = Data.PathFor(name); Page.Materials.ScopeConfirmed = true; }
    public void Dispose() { Shell.Dispose(); Data.Dispose(); }
}
