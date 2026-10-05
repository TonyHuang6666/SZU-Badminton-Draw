using System.Text.Json;
using System.Text.Json.Serialization;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed record RecentWorkspaceEntry(string Path, DateTimeOffset? LastOpenedAt = null, DateTimeOffset? LastSavedAt = null)
{
    [JsonIgnore]
    public DateTimeOffset? LastActivityAt => LastOpenedAt is null ? LastSavedAt :
        LastSavedAt is null || LastOpenedAt >= LastSavedAt ? LastOpenedAt : LastSavedAt;
}

/// <summary>Only the local launch history; tournament data remain in the workflow-owned archive.</summary>
public sealed class RecentWorkspaceStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SZU-Badminton-Draw", "recent-workspaces.json");
    public IReadOnlyList<string> Read() => ReadEntries().Select(entry => entry.Path).ToArray();
    public IReadOnlyList<RecentWorkspaceEntry> ReadEntries()
    {
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        var entries = new List<RecentWorkspaceEntry>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            RecentWorkspaceEntry? entry = element.ValueKind switch
            {
                JsonValueKind.String => new(element.GetString() ?? ""),
                JsonValueKind.Object => element.Deserialize<RecentWorkspaceEntry>(Options),
                _ => null
            };
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path) || !System.IO.Path.IsPathFullyQualified(entry.Path) ||
                entries.Any(existing => string.Equals(existing.Path, entry.Path, StringComparison.Ordinal))) continue;
            entries.Add(entry);
            if (entries.Count == 10) break;
        }
        return entries;
    }
    public void Save(IReadOnlyList<string> paths)
        => Write(paths.Take(10).ToArray());
    public void Save(IReadOnlyList<RecentWorkspaceEntry> entries)
        => Write(entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Path) && System.IO.Path.IsPathFullyQualified(entry.Path))
            .DistinctBy(entry => entry.Path, StringComparer.Ordinal).Take(10).ToArray());
    private void Write<T>(T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
