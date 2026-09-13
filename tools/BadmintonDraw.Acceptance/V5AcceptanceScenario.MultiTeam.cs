using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using System.IO.Compression;
using System.Xml.Linq;
using static V5AcceptanceEvidence;

internal sealed partial class V5AcceptanceScenario
{
    private void Multiple()
    {
        Create(V5AcceptanceRosterFactory.Multiple(32, 16, 4), TournamentPurpose.FullTournament);
        V5AcceptanceRosterFactory.VerifyShared(Workspace, 32, 16);
        Require(Workspace.Projects.Select(p => p.MatchGraph!.Matches.Count).SequenceEqual([32, 24, 24]), "Moderate component graph counts differ.");
        Schedule("benchmark-exact-20-minutes", V5AcceptanceRosterFactory.ModerateResources(), false, 80);
        Schedule("mixed-20-25-30-minutes", V5AcceptanceRosterFactory.ModerateResources(), true, 80);
        var plan = Placements(Workspace); var package = Package("all-projects");
        var first = package.Outputs.First(o => o.Kind == OperationalMaterialKind.ProjectRecordExcel && o.ProjectId == Workspace.Projects[1].Id);
        // Genuine coherent numeric draft, no winner: coverage only, never a result.
        var draft = evidence.PathFor("imports/pending-numeric-draft.xlsx"); Directory.CreateDirectory(Path.GetDirectoryName(draft)!); File.Copy(first.Path, draft, false);
        using (var book = new XLWorkbook(draft)) { var sheet = book.Worksheet("对阵记录表"); sheet.Cell(V5AcceptanceResults.Rows(sheet)[0].Row, 9).Value = 21; book.Save(); }
        var pending = Import("numeric-draft-coverage", [draft]);
        Require(Workspace.Results.Count == 0 && pending.Evaluation.ProposedCounts.PendingRowCount > 0 && pending.Evaluation.Diagnostics.Any(d => d.Severity == ResultImportDiagnosticSeverity.Warning), "Numeric draft was not a warned coverage-only import.");
        var target = Workspace.Resources!.Days.Last().Date;
        var subset = Package("md-qualified-carry", [target], target, Workspace.Projects[1].Id);
        Require(subset.Scope.ProjectIds.SequenceEqual([Workspace.Projects[1].Id]) && subset.Counts.PendingCarryoverCount > 0 && subset.Outputs.Where(o => o.ProjectId.HasValue).All(o => o.ProjectId == Workspace.Projects[1].Id), "Qualified project carryover leaked another project.");
        var expected = V5AcceptanceResults.Fold(Workspace, target);
        V5AcceptanceResults.EmitExpected(evidence, "expected-results.csv", expected.Values);
        var files = FillProjectFiles(package, "complete", expected).Reverse().ToArray();
        var order = files.SelectMany(path => { using var book = new XLWorkbook(path); return V5AcceptanceResults.Rows(book.Worksheet("对阵记录表")).Select(r => r.Key).ToArray(); }).ToArray();
        Require(order.Length == 80 && order.Distinct().Count() == 80, "Per-project daily record coverage duplicated or omitted a node.");
        var indices = order.Select((key, index) => (key, index)).ToDictionary(x => x.key, x => x.index);
        Require(Workspace.Projects.Any(p => p.MatchGraph!.Matches.Any(n => n.Dependencies.Any(d => indices[new(p.Id, n.Id)] < indices[new(p.Id, d)]))), "Reversed batch failed to exercise dependent-before-prerequisite file order.");
        evidence.Json("actual-reversed-batch-order.json", new { Files = files, Keys = order });
        Import("reverse-whole-batch", files);
        Require(Placements(Workspace) == plan, "Actual played date or result import changed planned placements.");
        Require(Workspace.ImportLogs.SelectMany(l => l.Rows).Any(r => r.RecordDay != expected[r.Key].ActualDay), "Actual date regression did not differ from receipt date.");
        Finish(expected);
    }
    private void Team()
    {
        Create([new(EventDiscipline.Team, CompetitionMode.TeamRoundRobin, 29, 4, "v5-team-29")], TournamentPurpose.FullTournament);
        Require(Workspace.Projects.Single().Draw!.Result.Groups.Select(g => g.Participants.Count).Order().SequenceEqual([7, 7, 7, 8]), "Odd team grouping changed.");
        Schedule("team-91-aggregate-schedule", V5AcceptanceRosterFactory.ModerateResources(), false, 91);
        var package = Package("team"); var blocks = 0;
        foreach (var output in package.Outputs.Where(o => o.Kind == OperationalMaterialKind.TeamScoreExcel))
        {
            using var book = new XLWorkbook(output.Path); var sheet = book.Worksheet("团体记分表");
            var starts = sheet.Column(1).CellsUsed().Where(c => c.GetString().Contains("团体赛记分表", StringComparison.Ordinal)).Select(c => c.Address.RowNumber).ToArray();
            Require(starts.Length > 0 && starts.SequenceEqual(Enumerable.Range(0, starts.Length).Select(i => i * 18 + 1)), "Team score blocks are not exactly 18 rows.");
            foreach (var start in starts) for (var i = 0; i < 5; i++) Require(sheet.Cell(start + 8 + i, 1).GetString() == $"第{i + 1}场", "A team block lacks one of five submatch rows.");
            using var zip = ZipFile.OpenRead(output.Path); using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var breaks = XDocument.Load(stream).Descendants(ns + "rowBreaks").Elements(ns + "brk").Select(b => (int)b.Attribute("id")!).ToArray();
            Require(breaks.SequenceEqual(Enumerable.Range(1, starts.Length - 1).Select(i => i * 18)) && sheet.PageSetup.PaperSize == XLPaperSize.A4Paper, "Team block page breaks/A4 differ.");
            blocks += starts.Length;
        }
        Require(blocks == 91, "Team scoring blocks differ from 91 aggregate graph nodes.");
        evidence.Json("team-block-evidence.json", new { ActualAggregateNodes = 91, Blocks = blocks, RowsPerBlock = 18, SubmatchesPerBlock = 5, NativePagination = "NotRun" });
        var expected = V5AcceptanceResults.Fold(Workspace, Workspace.Resources!.Days.Last().Date);
        V5AcceptanceResults.EmitExpected(evidence, "expected-results.csv", expected.Values);
        Import("team-whole-batch", FillProjectFiles(package, "team", expected)); Finish(expected);
    }
    private string[] FillProjectFiles(OperationalPackageOutcome package, string name, IReadOnlyDictionary<WorkspaceMatchKey, V5ExpectedResult> expected) =>
        package.Outputs.Where(o => o.Kind == OperationalMaterialKind.ProjectRecordExcel).Select((o, i) =>
            V5AcceptanceResults.CopyFill(o.Path, evidence.PathFor($"imports/{name}-{i}.xlsx"), Workspace, expected)).ToArray();
    private void Scale(bool rejection)
    {
        Create(V5AcceptanceRosterFactory.Multiple(rejection ? 159 : 64, rejection ? 29 : 32, 8), TournamentPurpose.FullTournament);
        V5AcceptanceRosterFactory.VerifyShared(Workspace, rejection ? 159 : 64, rejection ? 29 : 32);
        var count = Workspace.Projects.Sum(p => p.MatchGraph!.Matches.Count);
        Require(count == (rejection ? 345 : 292), "Disclosed scale graph count changed.");
        if (!rejection) { Schedule("large-292-seven-days-mixed", V5AcceptanceRosterFactory.LargeResources(), true, 292); return; }
        var resources = V5AcceptanceRosterFactory.ModerateResources(); var policy = V5AcceptanceRosterFactory.Policy(Workspace, false);
        evidence.Json("infeasible-345-request.json", new { resources, policy, ActualGraphCount = count, Scenario = "159 MS / 29 MD / 29 XD, five days, cap four; intentionally infeasible" });
        var hash = Hash(Archive); var revision = Revision;
        Step("infeasible-345-safe-rejection", () =>
        {
            var error = Reject<WorkspaceCommandException>(() => workflow.GenerateSchedule(resources, policy, Revision));
            evidence.Json("infeasible-345-diagnosis.json", error.Error);
            Require(error.Error.Code == "schedule.generation-failed" && error.Error.SchedulingFailure is not null && Hash(Archive) == hash && Revision == revision && Workspace.Schedule is null, "Infeasible scale case did not safely return typed unchanged rejection.");
        });
    }
}
