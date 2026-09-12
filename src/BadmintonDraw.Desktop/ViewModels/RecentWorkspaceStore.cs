using System.Text.Json;

namespace BadmintonDraw.Desktop.ViewModels;

/// <summary>Only the local launch history; tournament data remain in the workflow-owned archive.</summary>
public sealed class RecentWorkspaceStore(string path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SZU-Badminton-Draw", "recent-workspaces.json");
    public IReadOnlyList<string> Read() => !File.Exists(path) ? [] :
        (JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [])
        .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p)).Distinct().Take(10).ToArray();
    public void Save(IReadOnlyList<string> paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(paths.Take(10).ToArray()));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
