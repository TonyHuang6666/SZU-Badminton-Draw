using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BadmintonDraw.Workflows.Tournaments;

internal sealed class V5AcceptanceEvidence
{
    private const string Marker = ".v5-acceptance-owner.json";
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal string Root { get; }
    internal string Repository { get; }
    internal string Token { get; }
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private V5AcceptanceEvidence(string root, string repository, string token) { Root = root; Repository = repository; Token = token; }
    internal static V5AcceptanceEvidence Claim(string root, string repository)
    {
        ValidateOutput(root, repository);
        Require(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "Output directory is not empty; nothing was deleted.");
        Directory.CreateDirectory(root); var token = Guid.NewGuid().ToString("N");
        CreateText(Path.Combine(root, Marker), JsonSerializer.Serialize(new { Token = token, Root = root, Scenario = "root" }));
        return new(root, repository, token);
    }
    internal string CreateChild(string scenario)
    {
        var root = Path.Combine(Root, scenario); Require(!Directory.Exists(root) && !File.Exists(root), "Child output already exists.");
        Directory.CreateDirectory(root); CreateText(Path.Combine(root, Marker), JsonSerializer.Serialize(new { Token, Root = root, Scenario = scenario }));
        return root;
    }
    internal static V5AcceptanceEvidence OpenChild(string root, string repository, string scenario, string? token)
    {
        ValidateOutput(root, repository);
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, Marker)));
        Require(token is { Length: 32 } && marker.RootElement.GetProperty("Token").GetString() == token &&
            marker.RootElement.GetProperty("Root").GetString() == root && marker.RootElement.GetProperty("Scenario").GetString() == scenario,
            "Internal worker does not own this output.");
        Require(!File.Exists(Path.Combine(root, "acceptance-results.json")), "Completed child cannot be rerun in place.");
        CreateText(Path.Combine(root, ".worker-started"), DateTimeOffset.UtcNow.ToString("O"));
        return new(root, repository, token!);
    }
    private static void ValidateOutput(string root, string repository)
    {
        root = Path.GetFullPath(root); var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool Within(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        Require(!File.Exists(root) && !Within(repository, root) && !root.Equals(home, StringComparison.OrdinalIgnoreCase), "Output is a file, home, repository root or ancestor.");
        foreach (var folder in new[] { "src", "tests", "tools", "samples", ".git" })
            Require(!Within(root, Path.Combine(repository, folder)), "Output lies in a protected repository directory.");
        for (var item = new DirectoryInfo(root); item is not null; item = item.Parent)
            Require(item.LinkTarget is null || IsMacOsTemporaryAlias(item),
                "Symlink-indirected output is not accepted: " + item.FullName);
    }

    // macOS ships /tmp as a root-owned, fixed alias of /private/tmp. Accept only that OS
    // convention; user-controlled links anywhere else in the output ancestry remain rejected.
    private static bool IsMacOsTemporaryAlias(DirectoryInfo item) =>
        OperatingSystem.IsMacOS() && item.FullName == "/tmp" && item.LinkTarget == "private/tmp";
    internal static string FindRepository()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "BadmintonDraw.sln"))) return folder.FullName;
        throw new InvalidOperationException("Cannot locate BadmintonDraw.sln.");
    }
    internal string PathFor(string name) => Path.Combine(Root, name);
    internal void Json(string name, object value) => Text(name, JsonSerializer.Serialize(value, JsonOptions));
    internal void Text(string name, string value) => CreateText(PathFor(name), value);
    internal static void CreateText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes(text); stream.Write(bytes); stream.Flush(true);
    }
    internal T Phase<T>(string name, Func<T> action, Func<object?>? state = null)
    {
        var timer = Stopwatch.StartNew(); using var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime;
        Console.WriteLine("START " + name); object? detail = null; var success = false;
        try { var result = action(); detail = result; success = true; return result; }
        catch (Exception error)
        {
            detail = error switch { WorkspaceCommandException e => (object)e.Error, OperationalPackageExportException e => new { e.Error, e.Outputs, e.AttemptedOutputPath, e.RetainedStagingDirectory },
                WorkspaceBackupException e => new { e.Error, e.ManualBackupPath, e.Backup }, _ => new { Type = error.GetType().Name, error.Message } };
            throw;
        }
        finally
        {
            var entry = new { Phase = name, Success = success, ElapsedMilliseconds = timer.ElapsedMilliseconds,
                CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds, State = state?.Invoke(), Detail = detail };
            File.AppendAllText(PathFor("phases.jsonl"), JsonSerializer.Serialize(entry) + "\n");
            Console.WriteLine($"END {name}: {(success ? "passed" : "failed")} {timer.ElapsedMilliseconds}ms");
        }
    }
    internal void FinalizeReport(bool success, IReadOnlyList<string> failures, IReadOnlyList<object> outcomes)
    {
        var artifacts = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(p => p != PathFor("artifact-inventory.json") && p != PathFor("acceptance-results.json") && p != PathFor("evidence-sha256.txt"))
            .Order(StringComparer.Ordinal).Select(p => new { Path = Path.GetRelativePath(Root, p), Role = Role(p), Length = new FileInfo(p).Length, Sha256 = Hash(p) }).ToArray();
        AtomicJson("artifact-inventory.json", artifacts);
        AtomicJson("acceptance-results.json", new { Success = success, StartedAt = started, CompletedAt = DateTimeOffset.UtcNow,
            ElapsedMilliseconds = watch.ElapsedMilliseconds, Failures = failures, Outcomes = outcomes, ArtifactCount = artifacts.Length,
            Office = "NotRun", PdfVisual = "NotRun", Windows = "NotRun", Native = "NotRun", PhysicalPrinting = "NotRun" });
        CreateText(PathFor("evidence-sha256.txt"), Hash(PathFor("artifact-inventory.json")) + "  artifact-inventory.json\n" +
            Hash(PathFor("acceptance-results.json")) + "  acceptance-results.json\n");
    }
    private void AtomicJson(string name, object value)
    {
        var path = PathFor(name); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        CreateText(temporary, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temporary, path, true);
    }
    private static string Role(string path) => path.Contains("faults", StringComparison.Ordinal) ? "fault evidence" :
        path.EndsWith(".backup.szbd") ? "backup" : path.EndsWith(".candidate.szbd") ? "candidate" :
        path.Contains("imports", StringComparison.Ordinal) ? "edited import" : path.Contains("materials", StringComparison.Ordinal) ? "original export" :
        path.Contains("rosters", StringComparison.Ordinal) ? "synthetic source" : "evidence/archive";
    internal static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input)); }
    internal static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static T Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected rejection: " + typeof(T).Name); }
    internal static string Csv(params object?[] cells) => string.Join(",", cells.Select(c => "\"" + (c?.ToString() ?? "").Replace("\"", "\"\"") + "\""));
    internal sealed record ProcessResult(int ExitCode, bool TimedOut, long ElapsedMilliseconds, string Stdout, string Stderr);
    internal static async Task<ProcessResult> Child(string file, IReadOnlyList<string> arguments, string cwd,
        TimeSpan timeout, Dictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = cwd, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        if (environment is not null) foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        var timer = Stopwatch.StartNew(); using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start owned process.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout); var expired = false;
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException)
        {
            expired = true; process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        return new(process.ExitCode, expired, timer.ElapsedMilliseconds, await stdout.WaitAsync(TimeSpan.FromSeconds(10)), await stderr.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
