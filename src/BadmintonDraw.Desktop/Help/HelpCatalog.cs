namespace BadmintonDraw.Desktop.Help;

public enum HelpLinkKind { Internal, External, Unavailable, Blocked }
public sealed record HelpLinkTarget(HelpLinkKind Kind, string? DocumentPath = null, string Anchor = "", Uri? ExternalUri = null);

/// <summary>The explicit catalog is also the allowlist for offline navigation. No filesystem reads occur at runtime.</summary>
public sealed class HelpCatalog
{
    private static readonly Lazy<IReadOnlyList<HelpDocument>> PackagedDocuments = new(LoadDocuments);
    public IReadOnlyList<HelpDocument> Documents => PackagedDocuments.Value;

    private static IReadOnlyList<HelpDocument> LoadDocuments()
    {
        using var stream = typeof(HelpCatalog).Assembly.GetManifestResourceStream("Help/library.json")
            ?? throw new InvalidOperationException("未能读取内置帮助文档。");
        return HelpLibraryCodec.Read(stream).Documents;
    }

    public HelpLinkTarget ResolveLink(string currentPath, string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return new(HelpLinkKind.Blocked);
        string value;
        try { value = Uri.UnescapeDataString(href.Trim()); }
        catch (UriFormatException) { return new(HelpLinkKind.Blocked); }
        if (value.Any(char.IsControl) || value.Contains('\\') || value.StartsWith('/') || value.Contains('%'))
            return new(HelpLinkKind.Blocked);
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            return absolute.Scheme is "https" or "http" && !string.IsNullOrEmpty(absolute.Host)
                ? new(HelpLinkKind.External, ExternalUri: absolute) : new(HelpLinkKind.Blocked);
        if (value.Contains(':')) return new(HelpLinkKind.Blocked);

        var parts = value.Split('#', 2);
        var anchor = parts.Length > 1 ? parts[1] : "";
        var current = Documents.SingleOrDefault(d => d.Path == currentPath);
        if (current is null) return new(HelpLinkKind.Unavailable);
        // Relative Markdown links use the repository layout, not the resource IDs.
        var path = current.SourcePath;
        if (parts[0].Length > 0)
        {
            var segments = current.SourcePath.Split('/').SkipLast(1).ToList();
            foreach (var segment in parts[0].Split('/'))
            {
                if (segment is "" or ".") continue;
                if (segment == "..")
                {
                    if (segments.Count == 0) return new(HelpLinkKind.Unavailable);
                    segments.RemoveAt(segments.Count - 1);
                }
                else segments.Add(segment);
            }
            path = string.Join('/', segments);
        }
        var document = Documents.SingleOrDefault(d => d.SourcePath == path);
        return document is not null
            ? new(HelpLinkKind.Internal, document.Path, anchor) : new(HelpLinkKind.Unavailable);
    }
}
