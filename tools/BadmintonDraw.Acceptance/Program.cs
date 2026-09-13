using System.Reflection;
using System.Text.Json;

return await V5AcceptanceProgram.Run(args);

internal static class V5AcceptanceProgram
{
    internal static readonly string[] Flows = ["01-public-draw", "02-single", "03-multiple", "04-team", "05-faults", "06-large-292", "07-rejection-345"];
    internal static async Task<int> Run(string[] args)
    {
        V5AcceptanceEvidence? evidence = null;
        var success = false; var failures = new List<string>(); var outcomes = new List<object>();
        try
        {
            var repository = V5AcceptanceEvidence.FindRepository();
            var output = ParseOutput(args, repository);
            var worker = Environment.GetEnvironmentVariable("SZBD_V5_ACCEPTANCE_WORKER");
            evidence = worker is null ? V5AcceptanceEvidence.Claim(output, repository) :
                V5AcceptanceEvidence.OpenChild(output, repository, worker, Environment.GetEnvironmentVariable("SZBD_V5_ACCEPTANCE_TOKEN"));
            Console.WriteLine("Acceptance output: " + evidence.Root);
            if (worker is not null)
            {
                V5AcceptanceEvidence.Require(Flows.Contains(worker), "Unknown internal scenario.");
                var scenario = new V5AcceptanceScenario(evidence);
                scenario.Run(worker);
                evidence.Phase("scenario-milestone", () => { V5AcceptanceEvidence.Require(scenario.Completed, "Scenario did not reach its required milestone."); return new { worker, scenario.Completed }; });
            }
            else
            {
                var commit = await V5AcceptanceEvidence.Child("git", ["rev-parse", "HEAD"], repository, TimeSpan.FromSeconds(10));
                var dirty = await V5AcceptanceEvidence.Child("git", ["status", "--porcelain"], repository, TimeSpan.FromSeconds(10));
                evidence.Json("source-environment.json", new { Commit = commit, Dirty = dirty, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription, Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() });
                V5AcceptanceEvidence.Require(commit.ExitCode == 0 && dirty.ExitCode == 0, "Source metadata unavailable.");
                var audit = await V5AcceptanceEvidence.Child("dotnet", ["list", "BadmintonDraw.sln", "package", "--vulnerable", "--include-transitive", "--format", "json", "--output-version", "1"], repository, TimeSpan.FromMinutes(2));
                evidence.Json("dependency-audit-process.json", audit); evidence.Text("dependency-audit.json", audit.Stdout);
                V5AcceptanceEvidence.Require(audit.ExitCode == 0 && !audit.TimedOut && !audit.Stdout.Contains("\"advisoryurl\"", StringComparison.OrdinalIgnoreCase), "Dependency audit failed, timed out, or found vulnerabilities.");
                using (var parsed = JsonDocument.Parse(audit.Stdout)) V5AcceptanceEvidence.Require(parsed.RootElement.TryGetProperty("projects", out _), "Dependency audit is not a project result.");
                foreach (var flow in Flows)
                {
                    var directory = evidence.CreateChild(flow);
                    var result = await V5AcceptanceEvidence.Child("dotnet", [Assembly.GetExecutingAssembly().Location, "--output", directory], repository,
                        TimeSpan.FromMinutes(flow == "06-large-292" ? 4 : flow == "07-rejection-345" ? 3 : 10),
                        new() { ["SZBD_V5_ACCEPTANCE_WORKER"] = flow, ["SZBD_V5_ACCEPTANCE_TOKEN"] = evidence.Token });
                    evidence.Json(flow + "-process.json", result);
                    Console.WriteLine($"{flow}: exit={result.ExitCode}, timeout={result.TimedOut}, elapsed={result.ElapsedMilliseconds}ms");
                    var reportPath = Path.Combine(directory, "acceptance-results.json"); var passed = false;
                    if (File.Exists(reportPath))
                    { using var report = JsonDocument.Parse(File.ReadAllText(reportPath)); passed = report.RootElement.GetProperty("Success").GetBoolean(); }
                    outcomes.Add(new { Flow = flow, Passed = passed, result.ExitCode, result.TimedOut, result.ElapsedMilliseconds });
                    if (result.ExitCode != 0 || result.TimedOut || !passed) failures.Add(flow + ": missing successful non-no-op milestone; inspect retained child report/stdout/stderr.");
                }
                V5AcceptanceEvidence.Require(outcomes.Count == Flows.Length && failures.Count == 0, "One or more required acceptance scenarios failed.");
            }
            success = true;
        }
        catch (Exception error) { failures.Add(error.ToString()); Console.Error.WriteLine(error); }
        finally
        {
            if (evidence is not null)
            {
                if (Environment.GetEnvironmentVariable("SZBD_V5_ACCEPTANCE_WORKER") is not null)
                {
                    try { evidence.Phase("managed-artifact-readback", () => { V5ArtifactValidator.ReadArtifacts(evidence); return new { Readback = "Enumerated every XLSX/PDF, external QA NotRun" }; }); }
                    catch (Exception error) { success = false; failures.Add("Managed artifact readback: " + error); Console.Error.WriteLine(error); }
                }
                try { evidence.FinalizeReport(success, failures, outcomes); }
                catch (Exception error) { success = false; Console.Error.WriteLine("Evidence finalization failed: " + error); }
            }
        }
        return success ? 0 : evidence is null ? 2 : 1;
    }
    private static string ParseOutput(string[] args, string repository)
    {
        if (args.Length == 0) return Path.Combine(repository, "artifacts", "acceptance", "v5.0.0", $"run-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        if (args.Length != 2 || args[0] != "--output" || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Usage: BadmintonDraw.Acceptance [--output <new-or-empty-directory>]. Unknown, duplicate and missing arguments are rejected.");
        return Path.GetFullPath(args[1], repository);
    }
}
