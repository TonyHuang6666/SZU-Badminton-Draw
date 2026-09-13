using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Tests;

internal sealed class WorkspaceTimedDrawTestFixture : IDisposable
{
    internal string DirectoryPath { get; } = Directory.CreateTempSubdirectory("workspace-timed-draw-").FullName;
    internal TournamentWorkspace Workspace { get; }
    internal TournamentProject Project => Workspace.Projects[0];
    internal DrawResult Draw => Project.Draw!.Result;
    internal string PathFor(string name) => Path.Combine(DirectoryPath, name + ".xlsx");
    internal DrawExportContext ExportContext => Source(Workspace, Project);
    internal static DrawExportContext Source(TournamentWorkspace workspace, TournamentProject project) => new(
        workspace.Id, workspace.Name, workspace.Revision, project.Id, project.DisplayName, project.Draw!.ConfirmedAt,
        project.Roster!.SourceFileName, project.Roster.ContentHash, Guid.Parse("f32844b5-914b-48fb-bb50-d5ef061c448f"),
        project.Draw.ConfirmedAt!.Value.AddMinutes(1), project.Draw.Result.Audit.RandomSeed, project.Draw.Result.Audit.InputHash);

    internal WorkspaceTimedDrawTestFixture(CompetitionMode mode, int count, int groups = 1,
        KnockoutGoal goal = KnockoutGoal.OneQualifierPerGroup, PlacementPlayoff playoff = PlacementPlayoff.None,
        IReadOnlyList<DrawParticipant>? source = null)
    {
        var team = mode is CompetitionMode.TeamKnockout or CompetitionMode.TeamRoundRobin;
        var roster = source ?? Enumerable.Range(1, count).Select(i => new DrawParticipant($"参赛{i}【甲】胜者",
            TeamName: team ? $"独立队伍{i}" : null, PartnerTeamName: team && i <= 2 ? "共同单位" : null,
            PrimaryStudentId: team ? null : $"student-{i}")).ToArray();
        var draw = new DrawService().Generate(roster, new(mode, team ? EventKind.Team : EventKind.Singles,
            groups, "timed-layout-fixture", KnockoutGoal: goal, PlacementPlayoff: playoff));
        var project = TournamentProject.Create(team ? EventDiscipline.Team : EventDiscipline.MenSingles, mode, 0, "同名项目");
        var graph = MatchGraphFactory.Create(project.Id, draw, 30);
        var confirmed = draw.Audit.GeneratedAt.AddSeconds(1);
        project = project with { Roster = new(roster, "真实测试名单.xlsx", "test-roster-sha256", []), Draw = new(draw, confirmed), MatchGraph = graph };
        var days = new[] { new ScheduleDaySettings(new(2026, 9, 14), new(8, 0), new(23, 0), ["B1"]) };
        var resources = new TournamentResourcePlan(days, 1, 0, 99);
        var generated = new TournamentScheduler().Generate(new([graph], resources,
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])) { ScheduleRevision = 3 });
        var schedule = Xunit.Assert.IsType<TournamentSchedulingResult.Success>(generated).Schedule;
        Workspace = new(Guid.NewGuid(), "绑定真实比赛身份的时间图", team ? TournamentKind.Team : TournamentKind.Individual,
            TournamentPurpose.FullTournament, TournamentStage.ScheduleReady, [project], resources,
            schedule,
            new Dictionary<WorkspaceMatchKey, TournamentMatchResult>(), [], draw.Audit.GeneratedAt, confirmed, 3);
        TournamentWorkspaceRules.Validate(Workspace);
    }

    internal string Export(TournamentWorkspace? workspace = null, TournamentProject? project = null, DrawExportContext? source = null)
    {
        workspace ??= Workspace; project ??= workspace.Projects[0]; source ??= Source(workspace, project);
        var path = PathFor("timed");
        new DrawResultExcelWriter().WriteTimed(path, new(workspace), project.Id, source);
        return path;
    }
    internal MatchPlacement Placement(MatchNode node) => Workspace.Schedule!.Placements[node.Id];
    internal static string TimeText(MatchPlacement placement) => $"{placement.DayLabel} {placement.StartTime:HH:mm}-{placement.EndTime:HH:mm}";
    public void Dispose() => Directory.Delete(DirectoryPath, true);
}
