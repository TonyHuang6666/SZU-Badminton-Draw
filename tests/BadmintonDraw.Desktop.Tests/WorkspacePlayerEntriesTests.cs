using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Scheduling;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspacePlayerEntriesTests
{
    // A roster is the registration record: elimination must not remove a registered second project.
    [Fact]
    public void RegisteredProjectsRemainAfterEliminationAndIncludeDoublesPartners()
    {
        var fixture = new EntryFixture();
        var singles = fixture.Project("单打", EventDiscipline.MenSingles, fixture.A, fixture.B);
        var doubles = fixture.Project("双打", EventDiscipline.MenDoubles, fixture.Pair(fixture.C, fixture.A), fixture.Pair(fixture.D, fixture.B));
        var first = fixture.Match(singles, fixture.A, fixture.B, 1, 9, 0);
        fixture.Match(doubles, fixture.Pair(fixture.C, fixture.A), fixture.Pair(fixture.D, fixture.B), 1, 10, 0);
        fixture.Result(first, fixture.B, fixture.A);

        var row = WorkspacePlayerEntries.Build(fixture.Session()).Single(p => p.StudentId == "A");

        Assert.Equal(new[] { "单打", "双打" }, row.ProjectNames);
        Assert.Equal(2, row.ProjectCount);
        Assert.Equal(1, row.CompletedCount);
        Assert.Equal(1, row.PendingCount);
        Assert.Contains("C / A", row.ConfirmedMatches.Single(m => m.ProjectName == "双打").OwnSide);
    }

    // Merging by name would falsely give both same-name students the other's project.
    [Fact]
    public void SameNameDifferentStudentIdsStaySeparate()
    {
        var fixture = new EntryFixture();
        var one = fixture.Person("同名", "one");
        var two = fixture.Person("同名", "two");
        fixture.Project("共同", EventDiscipline.MenSingles, one, two);
        fixture.Project("one专属", EventDiscipline.MenSingles, one, fixture.C);
        fixture.Project("two专属", EventDiscipline.MenSingles, two, fixture.D);

        var rows = WorkspacePlayerEntries.Build(fixture.Session());

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "共同", "one专属" }, rows.Single(p => p.StudentId == "one").ProjectNames);
        Assert.Equal(new[] { "共同", "two专属" }, rows.Single(p => p.StudentId == "two").ProjectNames);
    }

    // Projected candidate identities must never be counted as confirmed participation or risk.
    [Fact]
    public void UnresolvedWinnerCandidatesArePotentialAndCannotClaimNoRisk()
    {
        var fixture = new EntryFixture();
        var knockout = fixture.Project("淘汰", EventDiscipline.MenSingles, fixture.A, fixture.B, fixture.C);
        var other = fixture.Project("另项", EventDiscipline.MenSingles, fixture.A, fixture.D);
        var first = fixture.Match(knockout, fixture.A, fixture.B, 1, 9, 0);
        var final = fixture.Match(knockout, new EntrantSource.WinnerOf(first.Id), fixture.C, 2, 10, 0);
        fixture.Match(other, fixture.A, fixture.D, 1, 10, 0);

        var row = WorkspacePlayerEntries.Build(fixture.Session()).Single(p => p.StudentId == "A");

        Assert.Equal(2, row.ConfirmedMatches.Count);
        var potential = Assert.Single(row.PotentialMatches);
        Assert.Equal(final.Id, potential.Key.MatchId);
        Assert.True(potential.IsPotential);
        Assert.Equal("后续可能参加（结果未定）", potential.Status);
        Assert.Equal(0, row.ConflictCount);
        Assert.Contains("尚不能判定无风险", row.RiskSummary);
    }

    // Results must move only the winner to WinnerOf and only the loser to LoserOf.
    [Fact]
    public void WinnerAndLoserOutcomesResolveAndRemoveEliminatedPotential()
    {
        var fixture = new EntryFixture();
        var knockout = fixture.Project("淘汰", EventDiscipline.MenSingles, fixture.A, fixture.B, fixture.C, fixture.D);
        fixture.Project("登记另项", EventDiscipline.MenSingles, fixture.A, fixture.B);
        var first = fixture.Match(knockout, fixture.A, fixture.B, 1, 9, 0);
        var final = fixture.Match(knockout, new EntrantSource.WinnerOf(first.Id), fixture.C, 2, 10, 0);
        var placement = fixture.Match(knockout, new EntrantSource.LoserOf(first.Id), fixture.D, 3, 11, 0);
        fixture.Result(first, fixture.B, fixture.A);

        var rows = WorkspacePlayerEntries.Build(fixture.Session());
        var a = rows.Single(p => p.StudentId == "A");
        var b = rows.Single(p => p.StudentId == "B");

        Assert.Empty(a.PotentialMatches);
        Assert.Empty(b.PotentialMatches);
        Assert.Equal(new[] { first.Id, placement.Id }, a.ConfirmedMatches.Select(m => m.Key.MatchId));
        Assert.Equal(new[] { first.Id, final.Id }, b.ConfirmedMatches.Select(m => m.Key.MatchId));
    }

    // Candidate recursion must narrow when an earlier result eliminates an entrant.
    [Fact]
    public void EarlierResultRemovesEliminatedCandidateFromUnresolvedLaterRounds()
    {
        var fixture = new EntryFixture();
        var knockout = fixture.Project("淘汰", EventDiscipline.MenSingles, fixture.A, fixture.B, fixture.C, fixture.D);
        fixture.Project("登记另项", EventDiscipline.MenSingles, fixture.A, fixture.B);
        var first = fixture.Match(knockout, fixture.A, fixture.B, 1, 9, 0);
        var second = fixture.Match(knockout, new EntrantSource.WinnerOf(first.Id), fixture.C, 2, 10, 0);
        var final = fixture.Match(knockout, new EntrantSource.WinnerOf(second.Id), fixture.D, 3, 11, 0);
        fixture.Result(first, fixture.B, fixture.A);

        var rows = WorkspacePlayerEntries.Build(fixture.Session());
        var eliminated = rows.Single(p => p.StudentId == "A");
        var advanced = rows.Single(p => p.StudentId == "B");

        Assert.Empty(eliminated.PotentialMatches);
        Assert.Equal(first.Id, Assert.Single(eliminated.ConfirmedMatches).Key.MatchId);
        Assert.Equal(new[] { first.Id, second.Id }, advanced.ConfirmedMatches.Select(m => m.Key.MatchId));
        Assert.Equal(final.Id, Assert.Single(advanced.PotentialMatches).Key.MatchId);
    }

    // Counting a player once per unresolved side would duplicate this single possible appearance.
    [Fact]
    public void CandidateOnBothUnresolvedSidesHasOnePotentialAppearance()
    {
        var fixture = new EntryFixture();
        var knockout = fixture.Project("淘汰", EventDiscipline.MenSingles, fixture.A, fixture.B);
        fixture.Project("登记另项", EventDiscipline.MenSingles, fixture.A, fixture.C);
        var first = fixture.Match(knockout, fixture.A, fixture.B, 1, 9, 0);
        var rematch = fixture.Match(knockout, new EntrantSource.WinnerOf(first.Id), new EntrantSource.LoserOf(first.Id), 2, 10, 0);

        var row = WorkspacePlayerEntries.Build(fixture.Session()).Single(p => p.StudentId == "A");

        Assert.Equal(rematch.Id, Assert.Single(row.PotentialMatches).Key.MatchId);
        Assert.Single(row.ConfirmedMatches);
    }

    // Adjacent-only overlap checking would miss the first versus third overlapping pair.
    [Fact]
    public void AllConfirmedOverlapPairsAreCountedAndWarningsReachBothMatches()
    {
        var fixture = new EntryFixture();
        foreach (var name in new[] { "一", "二", "三" })
        {
            var project = fixture.Project(name, EventDiscipline.MenSingles, fixture.A, fixture.B);
            fixture.Match(project, fixture.A, fixture.B, 1, 9, 0, 60);
        }
        var row = WorkspacePlayerEntries.Build(fixture.Session()).Single(p => p.StudentId == "A");

        Assert.Equal(3, row.ConflictCount);
        Assert.Equal(0, row.RestWarningCount);
        Assert.Equal(-60d, row.MinimumRestMinutes);
        Assert.All(row.ConfirmedMatches, m => { Assert.Equal(2, m.ConflictCount); Assert.NotEmpty(m.Warnings); });
    }

    // Comparing across midnight would invent rest warnings and a false minimum interval.
    [Fact]
    public void MinimumRestUsesOnlySameDayAdjacentConfirmedMatchesInDateOrder()
    {
        var fixture = new EntryFixture();
        var laterProject = fixture.Project("先登记", EventDiscipline.MenSingles, fixture.A, fixture.B);
        var earlierProject = fixture.Project("后登记", EventDiscipline.MenSingles, fixture.A, fixture.C);
        fixture.Match(laterProject, fixture.A, fixture.B, 1, 10, 0, 20);
        fixture.Match(earlierProject, fixture.A, fixture.C, 1, 9, 30, 20);
        fixture.Match(laterProject, fixture.A, fixture.B, 2, 9, 0, 20, 1);

        var row = WorkspacePlayerEntries.Build(fixture.Session()).Single(p => p.StudentId == "A");

        Assert.Equal(new[] { "2026-10-04", "2026-10-04", "2026-10-05" }, row.ConfirmedMatches.Select(m => m.Placement.DayLabel));
        Assert.Equal(new TimeOnly(9, 30), row.ConfirmedMatches[0].Placement.StartTime);
        Assert.Equal(10d, row.MinimumRestMinutes);
        Assert.Equal(1, row.RestWarningCount);
        Assert.Equal(0, row.ConfirmedMatches[2].RestWarningCount);
    }

    // An intervening overlapping match must not hide a short gap to the long match that ends later.
    [Fact]
    public void RestWarningsIncludeNonAdjacentPairsAfterAnOverlappingMatch()
    {
        var fixture = new EntryFixture();
        var longProject = fixture.Project("长场", EventDiscipline.MenSingles, fixture.A, fixture.B);
        var shortProject = fixture.Project("重叠短场", EventDiscipline.MenSingles, fixture.A, fixture.C);
        var laterProject = fixture.Project("后场", EventDiscipline.MenSingles, fixture.A, fixture.D);
        var longMatch = fixture.Match(longProject, fixture.A, fixture.B, 1, 9, 0, 60);
        var shortMatch = fixture.Match(shortProject, fixture.A, fixture.C, 1, 9, 10, 20);
        var laterMatch = fixture.Match(laterProject, fixture.A, fixture.D, 1, 10, 10, 20);

        var row = WorkspacePlayerEntries.Build(fixture.Session()).Single(p => p.StudentId == "A");

        Assert.Equal(1, row.ConflictCount);
        Assert.Equal(1, row.RestWarningCount);
        Assert.Equal(1, row.ConfirmedMatches.Single(m => m.Key.MatchId == longMatch.Id).RestWarningCount);
        Assert.Equal(0, row.ConfirmedMatches.Single(m => m.Key.MatchId == shortMatch.Id).RestWarningCount);
        Assert.Equal(1, row.ConfirmedMatches.Single(m => m.Key.MatchId == laterMatch.Id).RestWarningCount);
    }

    // Midnight is only a date boundary, not evidence of an adequate rest interval.
    [Fact]
    public void RestWarningsUseActualDatesAcrossMidnightButMinimumRestKeepsSameDayScope()
    {
        var fixture = new EntryFixture();
        var firstProject = fixture.Project("午夜前", EventDiscipline.MenSingles, fixture.A, fixture.B);
        var secondProject = fixture.Project("午夜后", EventDiscipline.MenSingles, fixture.A, fixture.C);
        fixture.Match(firstProject, fixture.A, fixture.B, 1, 23, 40, 19);
        fixture.Match(secondProject, fixture.A, fixture.C, 1, 0, 1, 19, 1);

        var row = WorkspacePlayerEntries.Build(fixture.Session()).Single(p => p.StudentId == "A");

        Assert.Equal(0, row.ConflictCount);
        Assert.Equal(1, row.RestWarningCount);
        Assert.Null(row.MinimumRestMinutes);
        Assert.All(row.ConfirmedMatches, match => Assert.Equal(1, match.RestWarningCount));
    }

    [Fact]
    public void NoSameDayPairHasUnknownMinimumRest()
    {
        var fixture = new EntryFixture();
        var first = fixture.Project("一", EventDiscipline.MenSingles, fixture.A, fixture.B);
        var second = fixture.Project("二", EventDiscipline.MenSingles, fixture.A, fixture.C);
        fixture.Match(first, fixture.A, fixture.B, 1, 17, 0);
        fixture.Match(second, fixture.A, fixture.C, 1, 9, 0, 20, 1);

        var row = WorkspacePlayerEntries.Build(fixture.Session()).Single(p => p.StudentId == "A");

        Assert.Null(row.MinimumRestMinutes);
        Assert.Equal(0, row.RestWarningCount);
    }

    // Touching end/start is not an overlap; exactly the rest threshold must not warn.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(20, 0)]
    public void SameDayRestBoundaryHasExactWarningsWithoutOverlap(int gapMinutes, int expectedWarnings)
    {
        var fixture = new EntryFixture();
        var first = fixture.Project("一", EventDiscipline.MenSingles, fixture.A, fixture.B);
        var second = fixture.Project("二", EventDiscipline.MenSingles, fixture.A, fixture.C);
        fixture.Match(first, fixture.A, fixture.B, 1, 9, 0);
        fixture.Match(second, fixture.A, fixture.C, 1, 9, 20 + gapMinutes);

        var row = WorkspacePlayerEntries.Build(fixture.Session()).Single(p => p.StudentId == "A");

        Assert.Equal(0, row.ConflictCount);
        Assert.Equal((double)gapMinutes, row.MinimumRestMinutes);
        Assert.Equal(expectedWarnings, row.RestWarningCount);
    }

    [Fact]
    public void BuildDoesNotChangePublishedWorkspaceOrSchedule()
    {
        var fixture = new EntryFixture();
        var one = fixture.Project("一", EventDiscipline.MenSingles, fixture.A, fixture.B);
        fixture.Project("二", EventDiscipline.MenSingles, fixture.A, fixture.C);
        fixture.Match(one, fixture.A, fixture.B, 1, 9, 0);
        var session = fixture.Session();
        var schedule = session.Workspace.Schedule;
        var results = session.Workspace.Results;

        Assert.Single(WorkspacePlayerEntries.Build(session));

        Assert.Same(schedule, session.Workspace.Schedule);
        Assert.Same(results, session.Workspace.Results);
        Assert.Equal(7, session.Workspace.Revision);
    }

    private sealed class EntryFixture
    {
        public EntrantSource.Participant A { get; } = PersonValue("A", "A");
        public EntrantSource.Participant B { get; } = PersonValue("B", "B");
        public EntrantSource.Participant C { get; } = PersonValue("C", "C");
        public EntrantSource.Participant D { get; } = PersonValue("D", "D");
        private readonly List<TournamentProject> projects = [];
        private readonly List<MatchNode> matches = [];
        private readonly Dictionary<Guid, MatchPlacement> placements = [];
        private readonly Dictionary<WorkspaceMatchKey, TournamentMatchResult> results = [];
        private static readonly DateOnly FirstDay = new(2026, 10, 4);
        public EntrantSource.Participant Person(string name, string studentId) => PersonValue(name, studentId);
        private static EntrantSource.Participant PersonValue(string name, string id)
        {
            CrossEventPlayerIdentity[] players = [new(name, id)];
            return new(ProjectEntrantIdentity.IdentityKey(players), name, players);
        }
        public EntrantSource.Participant Pair(EntrantSource.Participant first, EntrantSource.Participant second)
        {
            var players = first.Players.Concat(second.Players).ToArray();
            return new(ProjectEntrantIdentity.IdentityKey(players), first.DisplayName + " / " + second.DisplayName, players);
        }
        public TournamentProject Project(string name, EventDiscipline discipline, params EntrantSource.Participant[] entrants)
        {
            var roster = entrants.Select(e => new DrawParticipant(e.DisplayName, PrimaryName: e.Players[0].Name,
                PrimaryStudentId: e.Players[0].StudentId, PartnerName: e.Players.Count > 1 ? e.Players[1].Name : null,
                PartnerStudentId: e.Players.Count > 1 ? e.Players[1].StudentId : null)).ToArray();
            var project = new TournamentProject(Guid.NewGuid(), discipline, name, CompetitionMode.SinglesKnockout,
                new(roster, "fixture.xlsx", "fixture", []), null, null, projects.Count);
            projects.Add(project);
            return project;
        }
        public MatchNode Match(TournamentProject project, EntrantSource a, EntrantSource b, int order,
            int hour, int minute, int duration = 20, int day = 0)
        {
            var dependencies = new[] { a, b }.Select(source => source switch
            {
                EntrantSource.WinnerOf winner => (Guid?)winner.MatchId,
                EntrantSource.LoserOf loser => loser.MatchId,
                _ => null
            }).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
            var node = new MatchNode(Guid.NewGuid(), project.Id, order.ToString(), order, 1, "淘汰赛", "比赛" + order, a, b, duration, dependencies);
            matches.Add(node);
            var time = new TimeOnly(hour, minute);
            placements.Add(node.Id, new(node.Id, FirstDay.AddDays(day).ToString("yyyy-MM-dd"), time, time.AddMinutes(duration), "B1"));
            return node;
        }
        public void Result(MatchNode match, EntrantSource.Participant winner, EntrantSource.Participant loser)
        {
            var key = new WorkspaceMatchKey(match.ProjectId, match.Id);
            results.Add(key, new(key, winner, loser, "21:10", 20, DateTimeOffset.UtcNow));
        }
        public WorkspaceSession Session()
        {
            var resources = new TournamentResourcePlan([new(FirstDay, new(0, 0), new(23, 59), ["B1"]),
                new(FirstDay.AddDays(1), new(0, 0), new(23, 59), ["B1"])], 1, 20, 8);
            var graphs = projects.Select(project => project with { MatchGraph = new(project.Id, "fixture", matches.Where(m => m.ProjectId == project.Id).ToArray()) }).ToArray();
            var schedule = new TournamentSchedule(placements, resources, new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []),
                graphs.ToDictionary(p => p.Id, _ => "fixture"), 4);
            return new(new(Guid.NewGuid(), "fixture", TournamentKind.Individual, TournamentPurpose.FullTournament,
                TournamentStage.ScheduleReady, graphs, resources, schedule, results, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7), "fixture.szbd");
        }
    }
}
