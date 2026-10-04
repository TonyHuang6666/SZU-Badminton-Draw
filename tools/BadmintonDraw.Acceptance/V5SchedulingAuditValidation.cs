using System.Reflection;
using System.Text.Json;
using BadmintonDraw.Core.Scheduling;

internal static class V5SchedulingAuditValidation
{
    internal const string WorkerVariable = "SZBD_V5_EXACT_VALIDATION_DIRECTORY";
    internal sealed record Result(string Status, bool? IsValid, V5AcceptanceEvidence.ProcessResult Process);

    internal static Result Check(string directory)
    {
        var process = V5AcceptanceEvidence.Child("dotnet", [Assembly.GetExecutingAssembly().Location], directory,
            TimeSpan.FromSeconds(30), new() { [WorkerVariable] = directory }).GetAwaiter().GetResult();
        if (process.TimedOut) return new("UnverifiedTimeout", null, process);
        if (process.ExitCode != 0) return new("UnverifiedError", null, process);
        try
        {
            using var json = JsonDocument.Parse(process.Stdout);
            var valid = json.RootElement.GetProperty("IsValid").GetBoolean();
            return new(valid ? "Valid" : "Invalid", valid, process);
        }
        catch (JsonException) { return new("UnverifiedError", null, process); }
    }

    internal static int Worker(string directory)
    {
        try
        {
            var request = JsonSerializer.Deserialize<TournamentSchedulingRequest>(File.ReadAllText(Path.Combine(directory, "request.json")), V5SchedulingAudit.JsonOptions)!;
            var placements = JsonSerializer.Deserialize<Dictionary<Guid, MatchPlacement>>(File.ReadAllText(Path.Combine(directory, "placements.json")), V5SchedulingAudit.JsonOptions)!;
            var validation = new TournamentPlacementValidator(request).ValidateSchedule(placements);
            Console.WriteLine(JsonSerializer.Serialize(validation, V5SchedulingAudit.JsonOptions));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
