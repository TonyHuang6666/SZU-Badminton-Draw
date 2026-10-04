using System.Security.Cryptography;
using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;

namespace BadmintonDraw.Tests;

internal sealed class WorkspaceOperationalPackageFixture : IDisposable
{
    internal static readonly DateOnly FirstDay = new(2026, 9, 20);
    internal string DirectoryPath { get; } = Directory.CreateTempSubdirectory("workspace-operational-").FullName;
    internal string Archive => Path.Combine(DirectoryPath, "赛事.szbd");
    internal string Output => Path.Combine(DirectoryPath, "materials");
    internal TournamentWorkspaceWorkflow Workflow { get; }
    internal TournamentWorkspace Workspace => Workflow.CurrentSession!.Workspace;
    internal ITournamentWorkspaceStore Store { get; }

    internal WorkspaceOperationalPackageFixture(int projects = 1, int entrants = 2, bool team = false,
        bool roundRobin = false, OperationalPackageFileOperations? files = null,
        ITournamentWorkspaceStore? store = null, Func<TournamentWorkspace, TournamentWorkspace>? transform = null)
    {
        Store = store ?? new TournamentWorkspaceStore();
        var items = Enumerable.Range(0, projects).Select(index =>
        {
            var discipline = team ? EventDiscipline.Team : new[] { EventDiscipline.MenSingles, EventDiscipline.MenDoubles, EventDiscipline.MixedDoubles }[index];
            var doubles = discipline is EventDiscipline.MenDoubles or EventDiscipline.MixedDoubles;
            var mode = team ? (roundRobin ? CompetitionMode.TeamRoundRobin : CompetitionMode.TeamKnockout)
                : (roundRobin ? CompetitionMode.SinglesRoundRobin : CompetitionMode.SinglesKnockout);
            var roster = Enumerable.Range(1, entrants).Select(i => new DrawParticipant(team ? $"队伍{i}" : doubles ? $"参赛{i} / 搭档{i}" : $"参赛{i}",
                PrimaryName: doubles ? $"参赛{i}" : null, PartnerName: doubles ? $"搭档{i}" : null, TeamName: team ? $"队伍{i}" : null,
                PrimaryStudentId: team ? null : $"{index}-player-{i}", PartnerStudentId: doubles ? $"{index}-partner-{i}" : null)).ToArray();
            var draw = new DrawService().Generate(roster, new(mode, team ? EventKind.Team : doubles ? EventKind.Doubles : EventKind.Singles,
                1, "operational-package"));
            var project = TournamentProject.Create(discipline, mode, index, "同名项目") with
            { Id = Guid.Parse($"a1111111-1111-4111-8111-{index + 1:000000000000}") };
            return project with { Roster = new(roster, $"名单-{index}.xlsx", $"roster-hash-{index}", []),
                Draw = new(draw, draw.Audit.GeneratedAt.AddSeconds(1)), MatchGraph = MatchGraphFactory.Create(project.Id, draw, 20 + index * 5) };
        }).ToArray();
        var resources = new TournamentResourcePlan(Enumerable.Range(0, 3).Select(i =>
            new ScheduleDaySettings(FirstDay.AddDays(i), new(9, 0), new(18, 0), ["B1", "B2", "B3"])).ToArray(), 3, 0, 30);
        var generated = new TournamentScheduler().Generate(new(items.Select(p => p.MatchGraph!).ToArray(), resources,
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])) { ScheduleRevision = 1 });
        var schedule = Xunit.Assert.IsType<TournamentSchedulingResult.Success>(generated).Schedule;
        var workspace = new TournamentWorkspace(Guid.Parse("b1111111-1111-4111-8111-111111111111"), "运营材料验收",
            team ? TournamentKind.Team : TournamentKind.Individual, TournamentPurpose.FullTournament,
            TournamentStage.ScheduleReady, items, resources, schedule, new Dictionary<WorkspaceMatchKey, TournamentMatchResult>(),
            [], items[0].Draw!.Result.Audit.GeneratedAt, items[0].Draw!.ConfirmedAt!.Value, 1);
        if (transform is not null) workspace = transform(workspace);
        Store.Create(Archive, workspace);
        Workflow = new(Store, operationalPackages: new(files));
        Workflow.OpenWorkspace(Archive);
    }

    internal WorkspaceMatchKey FirstKey => new(Workspace.Projects[0].Id, Workspace.Projects[0].MatchGraph!.Matches[0].Id);
    internal string Record(string name, IReadOnlyList<WorkspaceRecordExportRow> rows, bool fill = false)
    {
        var path = Path.Combine(DirectoryPath, name + ".xlsx");
        new WorkspaceMatchRecordWriter().Write(path, new(Workspace), rows);
        if (fill)
        {
            using var book = new XLWorkbook(path); var sheet = book.Worksheet("对阵记录表");
            for (var i = 0; i < rows.Count; i++)
            { var r = i + 6; sheet.Cell(r, 9).Value = "21-10"; sheet.Cell(r, 10).Value = 20;
                sheet.Cell(r, 12).Value = "A"; sheet.Cell(r, 21).Value = "正常"; sheet.Cell(r, 22).Value = rows[i].RecordDay.ToString("yyyy-MM-dd"); }
            book.Save();
        }
        return path;
    }
    internal void Import(string path)
    { var preview = Workflow.PreviewResultImport([path]); Workflow.ImportResults(preview, new(false, null), Workspace.Revision); }
    internal static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    internal static string Business(TournamentWorkspace source) => JsonSerializer.Serialize(source with { AuditEvents = [], Revision = 0, UpdatedAt = source.CreatedAt });
    internal void Retain(string name, TournamentWorkspace before, OperationalPackageOutcome outcome)
    {
        var root = Environment.GetEnvironmentVariable("SZBD_OPERATIONAL_FIXTURE_DIR");
        if (string.IsNullOrWhiteSpace(root)) return;
        var destination = Path.Combine(Path.GetFullPath(root), name);
        Directory.CreateDirectory(destination);
        foreach (var output in outcome.Outputs) File.Copy(output.Path, Path.Combine(destination, Path.GetFileName(output.Path)), false);
        new TournamentWorkspaceStore().Create(Path.Combine(destination, "source.szbd"), before);
        File.Copy(Archive, Path.Combine(destination, "saved.szbd"), false);
        var nodes = before.Projects.SelectMany(p => p.MatchGraph!.Matches).ToArray();
        File.WriteAllText(Path.Combine(destination, "fixture-evidence.json"), JsonSerializer.Serialize(new
        {
            Scenario = name, ExpectedWorkspaceId = before.Id, ExpectedSourceRevision = before.Revision,
            ExpectedTotalGraphMatches = nodes.Length,
            ExpectedGraphMatches = nodes.Select(n => new { n.ProjectId, MatchId = n.Id, n.ExpectedDurationMinutes,
                Placement = before.Schedule!.Placements[n.Id] }),
            ActiveReceiptRows = before.ImportLogs.Where(l => l.VoidedAt is null).SelectMany(l => l.Rows.Select(r => new
                { ImportLogId = l.Id, l.ContentHash, r.Key, r.RecordDay, r.HadResult, r.Location })),
            Actual = new { outcome.AuditId, outcome.Counts, outcome.Scope },
            Files = Directory.GetFiles(destination).Order().Select(path => new { FileName = Path.GetFileName(path),
                ByteLength = new FileInfo(path).Length, Sha256 = Hash(path) }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public void Dispose() => Directory.Delete(DirectoryPath, true);
}
