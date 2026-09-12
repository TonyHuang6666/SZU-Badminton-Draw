using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Tests;

internal static class WorkspaceRecordExportTestData
{
    internal const string ComplexName = "王【小明】 \"双引号\" / 空 格";
    internal static TournamentWorkspace Create()
    {
        var basis = TournamentWorkspaceRulesTests.Fixture(TournamentStage.ScheduleReady);
        var projects = new List<TournamentProject>();
        var placements = new Dictionary<Guid, MatchPlacement>();
        var resources = new TournamentResourcePlan([new(new(2026, 9, 13), new(9, 0), new(18, 0), ["B1", "B2"]),
            new(new(2026, 9, 14), new(9, 0), new(18, 0), ["B1", "B2"])], 2, 30, 6);
        for (var index = 0; index < 2; index++)
        {
            var discipline = index == 0 ? EventDiscipline.MenSingles : EventDiscipline.WomenSingles;
            var project = TournamentProject.Create(discipline, CompetitionMode.SinglesKnockout, index);
            var names = new[] { ComplexName, "某某胜者", "=1+1", "第四位 / 选手" };
            var roster = names.Select((name, i) => new DrawParticipant(name, PrimaryStudentId: $"{index}-{i}")).ToArray();
            var players = roster.Select(p => ProjectEntrantIdentity.Create(discipline, p)).ToArray();
            var first = new MatchNode(Guid.NewGuid(), project.Id, "first", 1, 1, "首轮", "第1场", players[0], players[1], 30, []);
            var second = new MatchNode(Guid.NewGuid(), project.Id, "second", 2, 1, "首轮", "第2场", players[2], players[3], 30, []);
            var winnerLoser = new MatchNode(Guid.NewGuid(), project.Id, "winner-loser", 3, 1, "后续", "胜负交叉场", new EntrantSource.WinnerOf(first.Id), new EntrantSource.LoserOf(second.Id), 30, [first.Id, second.Id]);
            var loserWinner = new MatchNode(Guid.NewGuid(), project.Id, "loser-winner", 4, 1, "后续", "负胜交叉场", new EntrantSource.LoserOf(first.Id), new EntrantSource.WinnerOf(second.Id), 30, [first.Id, second.Id]);
            var nodes = new[] { first, second, winnerLoser, loserWinner };
            project = project with
            {
                Roster = new(roster, "synthetic.xlsx", "synthetic-hash", []),
                Draw = basis.Projects[0].Draw! with { Result = basis.Projects[0].Draw!.Result with { Groups = [new(1, roster)] } },
                MatchGraph = new(project.Id, "graph-" + index, nodes)
            };
            projects.Add(project);
            foreach (var node in nodes)
            {
                var start = node.Order == 1 ? new TimeOnly(9, 0, 0).Add(TimeSpan.FromTicks(1234)) : new TimeOnly(9 + node.Order, 0);
                placements.Add(node.Id, new(node.Id, node.Order <= 2 ? "2026-09-13" : "2026-09-14", start, start.AddMinutes(30), "B" + (index + 1)));
            }
        }
        var workspace = basis with { Projects = projects, Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>(), Resources = resources,
            Schedule = new(placements, resources, basis.Schedule!.Policy, projects.ToDictionary(p => p.Id, p => p.MatchGraph!.Revision), basis.Revision) };
        TournamentWorkspaceRules.Validate(workspace);
        return workspace;
    }
}
