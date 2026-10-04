using System.Text.Json;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core;
using static BadmintonDraw.Tests.TournamentSchedulerTestData;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class PresidentsCupSchedulingRegressionTests
{
    [Theory]
    [InlineData("--scheduling-audit")]
    [InlineData("--scheduling-audit INPUT")]
    [InlineData("--scheduling-audit INPUT --output OUTPUT --extra")]
    [InlineData("--scheduling-audit INPUT --output OUTPUT --output OUTPUT")]
    [InlineData("--output OUTPUT --scheduling-audit INPUT")]
    public void AuditCliRejectsMissingDuplicateUnknownOrReorderedArguments(string form)
    {
        var root = Path.Combine(Path.GetTempPath(), "szbd-args-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "input.szbd"); File.WriteAllText(input, "invalid archive");
            var output = Path.Combine(root, "output");
            var arguments = form.Split(' ').Select(value => value == "INPUT" ? input : value == "OUTPUT" ? output : value).ToArray();
            Assert.Equal(2, AuditProcess(arguments));
            Assert.False(Directory.Exists(output));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task KnownFeasibleLargeCompactGenerationCompletesWithDefaultBudget()
    {
        var known = JsonSerializer.Deserialize<KnownSchedulingWitness>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "KnownFeasible292.json")))!;
        var result = new TournamentScheduler().Generate(known.Request with { Policy = known.Policy with { Strategy = ScheduleAutoSchedulingStrategy.Compact } });
        Assert.True(result is TournamentSchedulingResult.Success, result is TournamentSchedulingResult.Failure f ? JsonSerializer.Serialize(f.Detail.Diagnostics) : "");
        var success = (TournamentSchedulingResult.Success)result;
        Assert.Equal(292, success.Schedule.Placements.Count);
        Assert.True(success.Quality.HardValidationComplete);
        Assert.Empty(success.Quality.Violations);
        await PresidentsCupFixture.ValidateExactAsync(known.Request, success.Schedule.Placements);
        PresidentsCupFixture.CheckIndependentSchedule(known with { Placements = success.Schedule.Placements.ToDictionary() });
    }

    [Fact]
    public async Task BalancedLargeComparisonNeverConfusesLimitedSearchWithImpossibility()
    {
        var known = JsonSerializer.Deserialize<KnownSchedulingWitness>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "KnownFeasible292.json")))!;
        var request = known.Request with { Policy = known.Policy with { Strategy = ScheduleAutoSchedulingStrategy.BalancedRelaxed } };
        var result = new TournamentScheduler().Generate(request);
        if (result is TournamentSchedulingResult.Success success)
        {
            Assert.Equal(292, success.Schedule.Placements.Count);
            await PresidentsCupFixture.ValidateExactAsync(request, success.Schedule.Placements);
        }
        else
        {
            var failure = Assert.IsType<TournamentSchedulingResult.Failure>(result).Detail;
            Assert.Contains(failure.Diagnostics!.FailureKind, new SchedulingFailureKind?[] { SchedulingFailureKind.SearchIncomplete, SchedulingFailureKind.ValidationIncomplete });
            Assert.Null(failure.CapacityEvidence);
            Assert.True(failure.Diagnostics.UsedWorkUnits[SchedulingRunPhase.Search] > 0);
        }
    }

    [Fact]
    public void ConcreteOutcomeOracleExhaustivelyChecksSmallWinnerLoserGraph()
    {
        var first = Node(1, Player("A"), Player("B"));
        var win = Node(2, new EntrantSource.WinnerOf(first.Id), Player("C"));
        var lose = Node(3, new EntrantSource.LoserOf(first.Id), Player("D"));
        MatchGraph[] graphs = [new(Id(1), "small", [first, win, lose])];
        var context = new GraphSchedulingCandidates(Request(graphs));
        var assignments = Enumerable.Range(0, 8).Select(mask => graphs[0].Matches.Select((n, bit) => (n.Id, Win: (mask & (1 << bit)) != 0))
            .ToDictionary(x => x.Id, x => x.Win)).ToArray();
        var resolved = assignments.Select(a => PresidentsCupFixture.Resolve(graphs, a)).ToArray();
        foreach (var a in graphs[0].Matches)
        foreach (var b in graphs[0].Matches.Where(n => n.Id != a.Id))
            Assert.Equal(resolved.Any(played => played[a.Id].Intersect(played[b.Id]).Any()), context.ShareCompatiblePlayer(a.Id, b.Id));
        Assert.False(context.ShareCompatiblePlayer(win.Id, lose.Id));
        Assert.Equal(2, resolved.Max(played => played.Values.Count(players => players.Contains("A"))));
    }

    [Fact]
    public void AuditRejectsDangerousAndNonemptyDestinationsWithoutChangingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "szbd-safe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "input.szbd"); File.WriteAllText(source, "sentinel");
            Assert.Equal(2, V5SchedulingAudit.Run(source, source));
            Assert.Equal(2, V5SchedulingAudit.Run(source, root));
            var full = Path.Combine(root, "nonempty"); Directory.CreateDirectory(full);
            File.WriteAllText(Path.Combine(full, "sentinel"), "retain");
            Assert.Equal(2, V5SchedulingAudit.Run(source, full));
            var link = Path.Combine(root, "alias"); Directory.CreateSymbolicLink(link, root);
            Assert.Equal(2, V5SchedulingAudit.Run(source, link));
            Assert.Equal("sentinel", File.ReadAllText(source));
            Assert.Equal("retain", File.ReadAllText(Path.Combine(full, "sentinel")));
            Directory.Delete(link);
            Assert.False(File.Exists(Path.Combine(root, "audit-results.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task KnownFeasibleLargeSchedulePassesAllHardRules()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "KnownFeasible292.json");
        Assert.True(File.Exists(path), "The complete known-feasible 292-match witness is missing.");
        var known = JsonSerializer.Deserialize<KnownSchedulingWitness>(File.ReadAllText(path))!;
        Assert.Equal(292, known.MatchGraphs.Sum(g => g.Matches.Count));
        Assert.Equal(292, known.Placements.Count);
        Assert.Equal(7, known.Resources.Days.Count);
        Assert.Equal(30, known.Resources.MinimumRestMinutes);
        Assert.Equal(4, known.Resources.MaxPlayerMatchesPerDay);
        await PresidentsCupFixture.ValidateExactAsync(known.Request, known.Placements);
        PresidentsCupFixture.CheckIndependentSchedule(known);
    }

    [Fact]
    public void AuditCopiesInputAndRetainsHashesWhenArchiveIsInvalid()
    {
        var root = Path.Combine(Path.GetTempPath(), "szbd-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "invalid.szbd");
            File.WriteAllText(source, "invalid archive sentinel");
            var output = Path.Combine(root, "audit");
            var result = AuditProcess(["--scheduling-audit", source, "--output", output]);
            Assert.Equal(1, result);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(output, "input-copy.szbd")));
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "audit-results.json")));
            Assert.Equal(report.RootElement.GetProperty("OriginalSha256Before").GetString(), report.RootElement.GetProperty("OriginalSha256After").GetString());
            Assert.False(report.RootElement.GetProperty("Completed").GetBoolean());
        }
        finally { Directory.Delete(root, true); }
    }

    private static int AuditProcess(string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false,
            RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "BadmintonDraw.Acceptance.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new TimeoutException("Audit CLI did not finish."); }
        Task.WaitAll(stdout, stderr);
        return process.ExitCode;
    }

    [Fact]
    public void FixedTwentyOneAppearanceWitnessResolvesIndependently()
    {
        var fixture = PresidentsCupFixture.Load();
        var witness = fixture.Witness;
        Assert.Equal(21, witness.MatchIds.Distinct().Count());
        Assert.Equal(new[] { 8, 7, 6 }, fixture.MatchGraphs.Select(g => g.Matches.Count(n => witness.MatchIds.Contains(n.Id))));
        var played = PresidentsCupFixture.Resolve(fixture.MatchGraphs, witness.OutcomeConditions);
        Assert.All(witness.MatchIds, id => Assert.Contains(witness.Player, played[id]));
    }

    [Fact]
    public void DefaultFourDayRequestRejectsBeforeSearch() => Reject(PresidentsCupFixture.Load().Request);

    [Fact]
    public void IncreasingCourtsDoesNotRemoveWitness()
    {
        var request = PresidentsCupFixture.Load().Request;
        Reject(request with { Resources = request.Resources with { RefereeCount = 48,
            Days = request.Resources.Days.Select(d => d with { Courts = Enumerable.Range(1, 48).Select(i => $"C{i}").ToArray() }).ToArray() } });
    }

    private static void Reject(TournamentSchedulingRequest request)
    {
        var failure = Assert.IsType<TournamentSchedulingResult.Failure>(new TournamentScheduler().Generate(request)).Detail;
        Assert.True(failure.Diagnostics!.FailureKind == SchedulingFailureKind.ProvenInfeasible,
            JsonSerializer.Serialize(failure.Diagnostics));
        Assert.Equal(0, failure.Diagnostics.UsedWorkUnits[SchedulingRunPhase.Search]);
        Assert.True(failure.CapacityEvidence!.RequiredLowerBound > failure.CapacityEvidence.CapacityUpperBound);
        var evidence = failure.CapacityEvidence;
        var played = PresidentsCupFixture.Resolve(request.MatchGraphs.ToArray(), evidence.OutcomeConditions);
        Assert.All(evidence.WitnessMatchIds, id => Assert.Contains(played[id], name => evidence.PlayerKey == "student:" + name));
    }

    // Catches lost/shared identities or topology during fixture anonymization.
    [Fact]
    public void AnonymousFixturePreservesTournamentCensus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PresidentsCup276.json");
        Assert.True(File.Exists(path), "The anonymous scale regression fixture is missing.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(new[] { "MatchGraphs", "Policy", "Resources", "Witness" }, document.RootElement.EnumerateObject().Select(p => p.Name).Order());
        var graphs = document.RootElement.GetProperty("MatchGraphs").Deserialize<MatchGraph[]>()!;
        Assert.Equal(new[] { 163, 74, 39 }, graphs.Select(g => g.Matches.Count));
        var memberships = graphs.SelectMany(g => g.Matches.SelectMany(n => new[] { n.SideA, n.SideB })
            .OfType<EntrantSource.Participant>().SelectMany(p => p.Players).DistinctBy(p => p.IdentityKey)
            .Select(p => (g.ProjectId, Player: p))).ToArray();
        var people = memberships.GroupBy(p => p.Player.IdentityKey).ToArray();
        Assert.Equal(235, people.Length);
        Assert.Equal(113, people.Count(g => g.Count() > 1));
        Assert.Equal(37, people.Count(g => g.Count() == 3));
        Assert.All(memberships, m => { Assert.Matches("^P[0-9]{4}$", m.Player.Name); Assert.Equal(m.Player.Name, m.Player.StudentId); });
        Assert.Equal(276, graphs.SelectMany(g => g.Matches).Select(n => n.Id).Distinct().Count());
        Assert.Equal(new[] { 24, 16, 16, 16 }, PresidentsCupFixture.Load().Resources.Days.Select(d => d.Courts.Count));
        foreach (var graph in graphs)
        {
            Assert.Matches("^anonymous-[1-3]$", graph.Revision);
            var ids = graph.Matches.Select(n => n.Id).ToHashSet();
            foreach (var node in graph.Matches)
            {
                Assert.Equal(graph.ProjectId, node.ProjectId);
                Assert.StartsWith("00000000-0000-0000-0000-", node.Id.ToString());
                Assert.Matches("^M[0-9]{4}$", node.OriginalMatchId);
                Assert.Matches("^Match-[0-9]{4}$", node.DisplayName);
                Assert.Matches("^Phase-[0-9]+$", node.Phase);
                Assert.Matches("^Group-[0-9]+$", node.GroupName);
                Assert.Empty(node.Note);
                var sources = new[] { node.SideA, node.SideB }.Select(s => s switch
                    { EntrantSource.WinnerOf winner => (Guid?)winner.MatchId, EntrantSource.LoserOf loser => loser.MatchId, _ => null }).OfType<Guid>().ToHashSet();
                Assert.True(sources.SetEquals(node.Dependencies));
                Assert.All(node.Dependencies, id => Assert.Contains(id, ids));
            }
        }
    }
}
