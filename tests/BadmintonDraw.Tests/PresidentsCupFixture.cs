using System.Text.Json;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using Xunit;

namespace BadmintonDraw.Tests;

internal sealed record PresidentsCupFixture(MatchGraph[] MatchGraphs,
    TournamentResourcePlan Resources, TournamentSchedulingPolicy Policy, PresidentsCupWitness Witness)
{
    internal static PresidentsCupFixture Load() => JsonSerializer.Deserialize<PresidentsCupFixture>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PresidentsCup276.json")))!;
    internal TournamentSchedulingRequest Request => new(MatchGraphs, Resources, Policy);

    internal static async Task ValidateExactAsync(TournamentSchedulingRequest request, IReadOnlyDictionary<Guid, MatchPlacement> placements)
    {
        var root = Path.Combine(Path.GetTempPath(), "szbd-exact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "request.json"), JsonSerializer.Serialize(request));
            File.WriteAllText(Path.Combine(root, "placements.json"), JsonSerializer.Serialize(placements));
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "BadmintonDraw.Acceptance.dll"));
            start.Environment["SZBD_V5_EXACT_VALIDATION_DIRECTORY"] = root;
            using var child = System.Diagnostics.Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await child.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { child.Kill(true); await child.WaitForExitAsync(); throw new TimeoutException("Exact validation is unverified after 30 seconds."); }
            Assert.True(child.ExitCode == 0, await stderr);
            using var result = JsonDocument.Parse(await stdout);
            Assert.True(result.RootElement.GetProperty("IsValid").GetBoolean(), result.RootElement.ToString());
        }
        finally { Directory.Delete(root, true); }
    }

    // Structural/resource checks are exhaustive. Player checks below replay 64
    // concrete assignments, a sampled independent check, not a universal proof.
    internal static void CheckIndependentSchedule(KnownSchedulingWitness known)
    {
        var nodes = known.MatchGraphs.SelectMany(g => g.Matches).ToArray();
        var placements = known.Placements;
        Assert.True(nodes.Select(n => n.Id).ToHashSet().SetEquals(placements.Keys));
        long At(MatchPlacement p, bool end = false) => DateOnly.ParseExact(p.DayLabel, "yyyy-MM-dd").ToDateTime(end ? p.EndTime : p.StartTime).Ticks;
        foreach (var node in nodes)
        {
            var p = placements[node.Id]; var day = known.Resources.Days.Single(d => d.DayLabel == p.DayLabel);
            Assert.Contains(p.Court, day.Courts); Assert.True(p.StartTime >= day.DayStart && p.EndTime <= day.DayEnd);
            Assert.Equal(TimeSpan.FromMinutes(known.Policy.ProjectTimings[node.ProjectId].MatchMinutes), p.EndTime - p.StartTime);
            foreach (var dependency in node.Dependencies) Assert.True(At(placements[dependency], true) <= At(p));
            foreach (var block in day.UnavailableCourtWindows ?? [])
                Assert.False(block.Courts.Contains(p.Court) && p.StartTime < block.EndTime && block.StartTime < p.EndTime);
            foreach (var other in placements.Values.Where(o => o.MatchId != p.MatchId && o.DayLabel == p.DayLabel && o.Court == p.Court))
                Assert.False(p.StartTime < other.EndTime && other.StartTime < p.EndTime);
            if (node.IsChampionshipFinal)
                Assert.All(nodes.Where(n => n.ProjectId == node.ProjectId && n.IsPlacementPlayoff), n => Assert.True(At(placements[n.Id]) <= At(p)));
        }
        foreach (var day in known.Resources.Days)
        {
            var ps = placements.Values.Where(p => p.DayLabel == day.DayLabel).ToArray();
            var boundaries = ps.SelectMany(p => new[] { p.StartTime, p.EndTime })
                .Concat((day.RefereeCapacityWindows ?? []).SelectMany(w => new[] { w.StartTime, w.EndTime }))
                .Concat((day.UnavailableCourtWindows ?? []).SelectMany(w => new[] { w.StartTime, w.EndTime })).Distinct().Order().ToArray();
            foreach (var t in boundaries)
            {
                var available = day.Courts.Count(c => !(day.UnavailableCourtWindows ?? []).Any(w => w.Courts.Contains(c) && w.StartTime <= t && t < w.EndTime));
                var referees = (day.RefereeCapacityWindows ?? []).Where(w => w.StartTime <= t && t < w.EndTime)
                    .Select(w => w.RefereeCount).Append(known.Resources.RefereeCount ?? day.Courts.Count).Min();
                Assert.True(ps.Count(p => p.StartTime <= t && t < p.EndTime) <= Math.Min(available, referees));
            }
        }
        var random = new Random(292276);
        for (var sample = 0; sample < 64; sample++)
        {
            var assignment = nodes.ToDictionary(n => n.Id, _ => sample < 2 ? sample == 0 : random.Next(2) == 0);
            var played = Resolve(known.MatchGraphs, assignment);
            foreach (var person in played.SelectMany(m => m.Value.Select(player => (Player: player, Placement: placements[m.Key]))).GroupBy(x => x.Player))
            {
                Assert.All(person.GroupBy(p => p.Placement.DayLabel), day => Assert.True(day.Count() <= known.Resources.MaxPlayerMatchesPerDay));
                var sequence = person.OrderBy(p => At(p.Placement)).ToArray();
                for (var i = 1; i < sequence.Length; i++)
                    Assert.True(At(sequence[i].Placement) - At(sequence[i - 1].Placement, true) >= TimeSpan.FromMinutes(known.Resources.MinimumRestMinutes).Ticks);
            }
        }
    }

    // Independent oracle: play the bracket using concrete side-A-wins booleans.
    // It does not call scheduling path construction, compatibility or proof helpers.
    internal static IReadOnlyDictionary<Guid, string[]> Resolve(MatchGraph[] graphs, IReadOnlyDictionary<Guid, bool> outcomes)
    {
        var nodes = graphs.SelectMany(g => g.Matches).ToDictionary(n => n.Id);
        var resolved = new Dictionary<Guid, (string[] A, string[] B)>();
        string[] Side(EntrantSource source)
        {
            if (source is EntrantSource.Participant participant) return participant.Players.Select(p => p.Name).ToArray();
            var id = source is EntrantSource.WinnerOf winner ? winner.MatchId : ((EntrantSource.LoserOf)source).MatchId;
            var match = Match(id);
            var winnerIsA = outcomes.GetValueOrDefault(id, true);
            return (source is EntrantSource.WinnerOf) == winnerIsA ? match.A : match.B;
        }
        (string[] A, string[] B) Match(Guid id)
        {
            if (!resolved.TryGetValue(id, out var match))
                resolved[id] = match = (Side(nodes[id].SideA), Side(nodes[id].SideB));
            return match;
        }
        return nodes.Keys.ToDictionary(id => id, id => { var match = Match(id); return match.A.Concat(match.B).ToArray(); });
    }
}

internal sealed record PresidentsCupWitness(string Player, Guid[] MatchIds, Dictionary<Guid, bool> OutcomeConditions);
