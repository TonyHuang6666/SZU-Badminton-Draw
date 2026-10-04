using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Persistence;

/// <summary>Local, copy-first scheduling evidence. Raw input and diagnostics belong only in the selected output directory.</summary>
public static class V5SchedulingAudit
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true,
        Converters = { new JsonStringEnumConverter() } };

    public static int Run(string inputPath, string outputDirectory)
    {
        string? root = null, before = null, after = null;
        var cases = new List<object>();
        var failures = new List<string>();
        var completed = false;
        FileStream? claim = null;
        var input = inputPath;
        try
        {
            input = ResolvePath(inputPath);
            var output = Path.TrimEndingDirectorySeparator(ResolvePath(outputDirectory));
            if (!File.Exists(input)) throw new FileNotFoundException("Audit input does not exist.");
            if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Output may not contain or replace the input.");
            if (File.Exists(output) || Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                throw new ArgumentException("Audit output must be a new or empty directory.");
            Directory.CreateDirectory(output);
            claim = new FileStream(Path.Combine(output, ".scheduling-audit-owner"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            root = output;
            before = Hash(input);
            var copy = Path.Combine(root, "input-copy.szbd");
            File.Copy(input, copy, false);
            if (Hash(copy) != before) throw new IOException("Input changed while being copied.");
            var workspace = new TournamentWorkspaceStore().Read(copy);
            var graphs = workspace.Projects.Select(p => p.MatchGraph ?? throw new InvalidDataException("Every project needs a match graph.")).ToArray();
            var resources = new TournamentResourcePlan(Enumerable.Range(3, 4).Select(day =>
                new ScheduleDaySettings(new(2026, 10, day), new(14, 0), new(18, 0),
                    Enumerable.Range(1, day == 3 ? 24 : 16).Select(i => $"C{i}").ToArray())).ToArray(), null, 30, 4);
            var policy = new TournamentSchedulingPolicy(ScheduleAutoSchedulingStrategy.Compact, [], false, [], [])
            { ProjectTimings = graphs.ToDictionary(g => g.ProjectId, _ => new ProjectMatchTiming(30)) };
            var request = new TournamentSchedulingRequest(graphs, resources, policy);
            cases.Add(RunCase(root, "01-four-days-rest30-cap4", request, failures));
            cases.Add(RunCase(root, "02-four-days-48courts", request with { Resources = resources with { RefereeCount = 48,
                Days = resources.Days.Select(day => day with { Courts = Enumerable.Range(1, 48).Select(i => $"C{i}").ToArray() }).ToArray() } }, failures));
            cases.Add(RunCase(root, "03-four-days-rest0-cap8", request with
                { Resources = resources with { MinimumRestMinutes = 0, MaxPlayerMatchesPerDay = 8 } }, failures));

            var known = JsonSerializer.Deserialize<KnownSchedulingWitness>(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "KnownFeasible292.json")), JsonOptions)!;
            var knownDirectory = Path.Combine(root, "04-known292-witness");
            Directory.CreateDirectory(knownDirectory);
            Write(knownDirectory, "request.json", known.Request);
            Write(knownDirectory, "placements.json", known.Placements);
            var witnessCheck = V5SchedulingAuditValidation.Check(knownDirectory);
            Write(knownDirectory, "exact-validation.json", witnessCheck);
            cases.Add(new { Name = "04-known292-witness", Status = "SuppliedCompleteWitness", ExactValidation = witnessCheck });
            if (witnessCheck.Status != "Valid") failures.Add("Supplied 292 witness was not independently verified by the exact validator.");
            foreach (var strategy in new[] { ScheduleAutoSchedulingStrategy.Compact, ScheduleAutoSchedulingStrategy.BalancedRelaxed })
                cases.Add(RunCase(root, "known292-" + strategy, known.Request with { Policy = known.Policy with { Strategy = strategy } }, failures));
            completed = true;
        }
        catch (Exception error) { failures.Add(error.ToString()); Console.Error.WriteLine(error.Message); }
        finally
        {
            if (root is not null)
            {
                try
                {
                    try { after = Hash(input); }
                    catch (Exception error) { completed = false; failures.Add("Cannot hash original after audit: " + error.Message); }
                    if (before != after) { completed = false; failures.Add("Original input hash changed during audit."); }
                    Write(root, "audit-results.json", new { Completed = completed, OriginalSha256Before = before,
                        OriginalSha256After = after, Defaults = TournamentSchedulingOptions.Default,
                        Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                        OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription, Cases = cases, Failures = failures,
                        CacheDefinition = "Per-cache entry peaks, not sum or bytes; proof memo is work-budget bounded, other measured caches obey MaxCacheEntries.",
                        IndependentCoverage = "Exact public validator in isolated process. Test oracle separately replays conditional witnesses and enumerates small graphs. Legacy Snapshot resource/dependency checks alone do not cover conditional player rules." });
                }
                catch (Exception error) { completed = false; Console.Error.WriteLine("Cannot finalize audit: " + error.Message); }
            }
            claim?.Dispose();
        }
        return completed && failures.Count == 0 ? 0 : root is null ? 2 : 1;
    }

    private static object RunCase(string root, string name, TournamentSchedulingRequest request, List<string> failures)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        Write(directory, "request.json", request);
        var watch = Stopwatch.StartNew();
        var result = new TournamentScheduler().Generate(request);
        watch.Stop();
        var diagnostics = result is TournamentSchedulingResult.Success success ? success.Diagnostics : ((TournamentSchedulingResult.Failure)result).Detail.Diagnostics;
        Write(directory, "result.json", result);
        object validation = new { Status = "NotApplicable" };
        object[] days = [];
        if (result is TournamentSchedulingResult.Success generated)
        {
            Write(directory, "placements.json", generated.Schedule.Placements);
            var check = V5SchedulingAuditValidation.Check(directory);
            validation = check;
            if (check.Status != "Valid") failures.Add(name + ": generated Success is " + check.Status + " under independent exact validation.");
            Write(directory, "exact-validation.json", validation);
            days = request.Resources.Days.Select(day =>
            {
                var matches = generated.Schedule.Placements.Values.Where(p => p.DayLabel == day.DayLabel).ToArray();
                var minutes = matches.Sum(p => (p.EndTime - p.StartTime).TotalMinutes);
                var capacity = ScheduleResourceCalculator.CalculateDayCapacityMinutes(request.Resources, day);
                return (object)new { day.DayLabel, MatchCount = matches.Length, Minutes = minutes, CapacityMinutes = capacity,
                    Utilization = capacity == 0 ? (double?)null : minutes / capacity };
            }).ToArray();
        }
        var outcome = new { Name = name, Status = result is TournamentSchedulingResult.Success ? "Success" : diagnostics?.FailureKind?.ToString(),
            ElapsedMilliseconds = watch.ElapsedMilliseconds, Diagnostics = diagnostics, Days = days, ExactValidation = validation };
        Write(directory, "summary.json", outcome);
        Console.WriteLine($"{name}: {outcome.Status}, {watch.ElapsedMilliseconds}ms");
        return outcome;
    }

    private static string ResolvePath(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new FileInfo(current);
            if (info.LinkTarget is not null)
                current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Cannot resolve symbolic link.");
        }
        return current;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    internal static void Write(string directory, string name, object value) =>
        File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, value.GetType(), JsonOptions));
}

public sealed record KnownSchedulingWitness(MatchGraph[] MatchGraphs, TournamentResourcePlan Resources,
    TournamentSchedulingPolicy Policy, Dictionary<Guid, MatchPlacement> Placements)
{
    [JsonIgnore] public TournamentSchedulingRequest Request => new(MatchGraphs, Resources, Policy);
}
