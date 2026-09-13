using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Tests;

internal static class WorkspaceMaterialTestFixture
{
    internal const string LiteralPlayer = "李  / 明【甲】胜者";
    internal static readonly DateOnly FirstDay = new(2026, 9, 19);
    internal static readonly DateOnly SecondDay = new(2026, 9, 20);

    internal static TournamentWorkspace Create(bool team = false, bool teamKnockout = false)
    {
        var disciplines = team ? new[] { EventDiscipline.Team } : [EventDiscipline.MenSingles, EventDiscipline.MenDoubles];
        var projects = disciplines.Select((discipline, index) =>
        {
            var mode = team ? teamKnockout ? CompetitionMode.TeamKnockout : CompetitionMode.TeamRoundRobin : CompetitionMode.SinglesKnockout;
            var kind = team ? EventKind.Team : discipline == EventDiscipline.MenDoubles ? EventKind.Doubles : EventKind.Singles;
            var roster = Enumerable.Range(0, 4).Select(i =>
            {
                var name = team ? $"学院 {i} / 校队【甲】胜者" : i == 0 ? LiteralPlayer : $"选手{index}-{i}";
                return new DrawParticipant(kind == EventKind.Doubles ? name + " / 搭档" + i : name,
                    PrimaryName: name, PartnerName: kind == EventKind.Doubles ? "搭 档 / " + i : null,
                    TeamName: team ? name : null,
                    PrimaryStudentId: $"{index}-{i}-a", PartnerStudentId: kind == EventKind.Doubles ? $"{index}-{i}-b" : null);
            }).ToArray();
            var project = TournamentProject.Create(discipline, mode, index);
            var draw = new DrawService().Generate(roster, new(mode, kind, 1, "score-fixture", KnockoutGoal: KnockoutGoal.Champion,
                PlacementPlayoff: team ? PlacementPlayoff.None : PlacementPlayoff.ThirdPlace));
            return project with { Roster = new(roster, "计分表虚拟名单.xlsx", "score-fixture-hash", []),
                Draw = new(draw, draw.Audit.GeneratedAt.AddSeconds(1)), MatchGraph = MatchGraphFactory.Create(project.Id, draw) };
        }).ToArray();
        var workspace = TournamentWorkspace.Create("计分材料验收", team ? TournamentKind.Team : TournamentKind.Individual,
            TournamentPurpose.FullTournament, projects.Select(p => p with { Draw = null, MatchGraph = null }).ToArray());
        var precise = new TimeOnly(9, 0).Add(TimeSpan.FromTicks(1234));
        var resources = new TournamentResourcePlan([new(FirstDay, precise, new(18, 0), ["B1", "B2"]),
            new(SecondDay, new(9, 0), new(18, 0), ["B1", "B2"])], 2, 15, 6);
        var request = new TournamentSchedulingRequest(projects.Select(p => p.MatchGraph!).ToArray(), resources,
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []));
        var generated = new TournamentScheduler().Generate(request);
        if (generated is not TournamentSchedulingResult.Success success)
            throw new InvalidOperationException(((TournamentSchedulingResult.Failure)generated).Detail.Message);
        workspace = workspace with { Projects = projects, Stage = TournamentStage.ScheduleReady, Resources = resources, Schedule = success.Schedule };
        TournamentWorkspaceRules.Validate(workspace);
        return workspace;
    }

    internal static MatchNode LiteralRoot(TournamentProject project) => project.MatchGraph!.Matches.Single(n =>
        n.Dependencies.Count == 0 && new[] { n.SideA, n.SideB }.OfType<EntrantSource.Participant>()
            .Any(p => p.Players.Any(player => player.Name == LiteralPlayer)));

    internal static TournamentWorkspace CompleteRoots(TournamentWorkspace workspace, DateOnly? actualDay)
    {
        var results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>();
        foreach (var project in workspace.Projects)
        foreach (var root in project.MatchGraph!.Matches.Where(n => n.Dependencies.Count == 0))
        {
            var key = new WorkspaceMatchKey(project.Id, root.Id);
            results[key] = new(key, (EntrantSource.Participant)root.SideA, (EntrantSource.Participant)root.SideB,
                workspace.Kind == TournamentKind.Team ? "3-1" : "21-10", 18, workspace.UpdatedAt.AddMinutes(1))
            { ActualPlayedDay = actualDay };
        }
        var completed = workspace with { Stage = TournamentStage.InProgress, Results = results };
        TournamentWorkspaceRules.Validate(completed);
        return completed;
    }

    internal static TournamentWorkspace WithLongCourt(TournamentWorkspace workspace)
    {
        var longCourt = string.Concat(Enumerable.Repeat("深圳大学粤海校区综合体育馆羽毛球场地", 8));
        var resources = workspace.Resources! with
        { Days = workspace.Resources.Days.Select(d => d with { Courts = [longCourt, "B2"] }).ToArray() };
        var result = workspace with { Resources = resources, Schedule = workspace.Schedule! with { Resources = resources,
            Placements = workspace.Schedule.Placements.ToDictionary(p => p.Key,
                p => p.Value with { Court = p.Value.Court == "B1" ? longCourt : p.Value.Court }) } };
        TournamentWorkspaceRules.Validate(result);
        return result;
    }
}
