using System.Text.Json;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using static V5AcceptanceEvidence;

internal sealed partial class V5AcceptanceScenario(V5AcceptanceEvidence evidence)
{
    private TournamentWorkspaceWorkflow workflow = new();
    private TournamentWorkspace Workspace => workflow.CurrentSession!.Workspace;
    private long Revision => Workspace.Revision;
    private string Archive => evidence.PathFor("tournament.szbd");
    internal bool Completed { get; private set; }
    private object State() => workflow.CurrentSession is not { } session ? new { Open = false } :
        new { Open = true, session.Workspace.Stage, session.Workspace.Revision, session.RequiresReload,
            Results = session.Workspace.Results.Count, Receipts = session.Workspace.ImportLogs.Count, History = session.Workspace.ResultHistory.Count };
    private void Step(string name, Action action) => evidence.Phase(name, () => { action(); return State(); }, State);
    internal void Run(string flow)
    {
        switch (flow)
        {
            case "01-public-draw": PublicDraw(); break;
            case "02-single": Single(); break;
            case "03-multiple": Multiple(); break;
            case "04-team": Team(); break;
            case "05-faults": new V5AcceptanceFaultScenarios(evidence).Run(); break;
            case "06-large-292": Scale(false); break;
            case "07-rejection-345": Scale(true); break;
            default: throw new ArgumentException("Unknown internal scenario: " + flow);
        }
        Completed = true;
    }
    private void Create(V5RosterDefinition[] definitions, TournamentPurpose purpose, bool confirm = true)
    {
        evidence.Json("roster-specification.json", definitions);
        Step("create", () => workflow.CreateWorkspace(new("v5 合成验收赛事", definitions[0].Discipline == EventDiscipline.Team ? TournamentKind.Team : TournamentKind.Individual,
            purpose, definitions.Select(d => new WorkspaceProjectRequest(d.Discipline, d.Mode, "同名项目")).ToArray(), Archive)));
        for (var i = 0; i < definitions.Length; i++)
        {
            var ordinal = i; var path = V5AcceptanceRosterFactory.Write(evidence, definitions[i], i);
            Step("import-roster-" + i, () => workflow.ImportRoster(Workspace.Projects[ordinal].Id, path, Revision));
            Require(Workspace.Projects[i].Draw is null && Workspace.Projects[i].MatchGraph is null && Workspace.Schedule is null,
                "Roster import performed an implicit public draw or schedule.");
        }
        if (!confirm) return;
        for (var i = 0; i < definitions.Length; i++)
        {
            var ordinal = i;
            Step("preview-" + i, () => workflow.PreviewDraw(Workspace.Projects[ordinal].Id, definitions[ordinal].Settings, Revision));
            Step("confirm-" + i, () => workflow.ConfirmDraw(Workspace.Projects[ordinal].Id, Revision));
        }
        Require(Workspace.Stage == TournamentStage.DrawsConfirmed && Workspace.Schedule is null, "Explicit draw lifecycle did not confirm every project.");
        V5ArtifactValidator.Snapshot(evidence, "confirmed", Archive);
    }
    private void Schedule(string name, TournamentResourcePlan resources, bool mixed, int nodes)
    {
        var policy = V5AcceptanceRosterFactory.Policy(Workspace, mixed);
        evidence.Json(name + "-request.json", new { Workspace.Id, resources, policy, Graphs = Workspace.Projects.Select(p => p.MatchGraph).ToArray(), Projects = Workspace.Projects.Select(p => new { p.Id, p.Discipline, p.SortOrder }) });
        Step(name, () => workflow.GenerateSchedule(resources, policy, Revision));
        Require(Workspace.Projects.Sum(p => p.MatchGraph!.Matches.Count) == nodes && Workspace.Schedule!.Placements.Count == nodes,
            "Actual graph/schedule count differs from the disclosed fixture.");
        V5ArtifactValidator.Snapshot(evidence, name, Archive);
    }
    private OperationalPackageOutcome Package(string name, IReadOnlyList<DateOnly>? days = null, DateOnly? carry = null, Guid? project = null)
    {
        OperationalPackageOutcome? result = null;
        Step("package-" + name, () => result = workflow.ExportOperationalPackage(new(evidence.PathFor("materials/" + name), project, days, carry), Revision));
        Require(result!.AuditRecorded && Workspace.AuditEvents.Count(a => a.Id == result.AuditId) == 1, "Package audit is not actually committed exactly once.");
        Require(result.Counts.RequiredOutputCount == result.Outputs.Count && result.Outputs.All(o => File.Exists(o.Path) && new FileInfo(o.Path).Length == o.ByteLength && Hash(o.Path).Equals(o.Sha256, StringComparison.OrdinalIgnoreCase)), "Published package output hash/coverage is incomplete.");
        evidence.Json("package-" + name + "-record-bindings.json", result.Outputs.Where(o => o.Kind is OperationalMaterialKind.ProjectRecordExcel or OperationalMaterialKind.MergedRecordExcel)
            .Select(o => V5AcceptanceResults.InspectBindings(o.Path, Workspace)).ToArray());
        evidence.Json("package-" + name + ".json", new { result.SourceRevision, result.AuditId, result.ExportedAt, result.Scope, result.Counts, result.Outputs, result.Skips,
            ActualAudit = Workspace.AuditEvents.Single(a => a.Id == result.AuditId), ManifestAuditSemantics = "planned until archive audit commit" });
        return result;
    }
    private WorkspaceResultImportOutcome Import(string name, IReadOnlyList<string> files, bool corrections = false, string? reason = null)
    {
        WorkspaceResultImportOutcome? result = null;
        Step(name, () =>
        {
            var preview = workflow.PreviewResultImport(files);
            evidence.Json(name + "-preview.json", new { preview.SourceRevision, preview.Files, preview.Evaluation.Status, preview.Evaluation.ProposedCounts, preview.Evaluation.Diagnostics, preview.Evaluation.Corrections });
            result = workflow.ImportResults(preview, new(corrections, reason), Revision);
        });
        return result!;
    }
    private void Finish(IReadOnlyDictionary<WorkspaceMatchKey, V5ExpectedResult> expected)
    {
        V5AcceptanceResults.Verify(Workspace, expected, true);
        Step("fresh-reopen-completed", () => { workflow = new(); workflow.OpenWorkspace(Archive); });
        V5AcceptanceResults.Verify(Workspace, expected, true);
        V5ArtifactValidator.Snapshot(evidence, "completed", Archive);
    }
    internal static string Business(TournamentWorkspace w) => JsonSerializer.Serialize(new { w.Id, w.Name, w.Kind, w.Purpose, w.Stage, w.Projects, w.Resources, w.Schedule,
        Results = w.Results.Values.OrderBy(r => r.Key.ProjectId).ThenBy(r => r.Key.MatchId), w.ProcessedDays, w.ImportLogs, w.ResultHistory });
    private static string Placements(TournamentWorkspace w) => JsonSerializer.Serialize(w.Schedule!.Placements.OrderBy(p => p.Key));
}
