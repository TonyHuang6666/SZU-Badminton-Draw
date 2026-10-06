using Markdig;

namespace BadmintonDraw.Desktop.Help;

public sealed record HelpDocument(string Path, string SourcePath, string Title, string Category, string Markdown)
{
    public string PlainText => Markdig.Markdown.ToPlainText(Markdown, HelpCatalog.Pipeline);
}

public enum HelpLinkKind { Internal, External, Unavailable, Blocked }
public sealed record HelpLinkTarget(HelpLinkKind Kind, string? DocumentPath = null, string Anchor = "", Uri? ExternalUri = null);

/// <summary>The explicit catalog is also the allowlist for offline navigation. No filesystem reads occur at runtime.</summary>
public sealed class HelpCatalog
{
    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
        .UsePipeTables().UseAutoIdentifiers(Markdig.Extensions.AutoIdentifiers.AutoIdentifierOptions.GitHub)
        .DisableHtml().Build();

    private static readonly Lazy<IReadOnlyList<HelpDocument>> PackagedDocuments = new(LoadDocuments);
    public IReadOnlyList<HelpDocument> Documents => PackagedDocuments.Value;

    private static IReadOnlyList<HelpDocument> LoadDocuments()
    {
        (string Path, string SourcePath, string Title, string Category)[] entries =
        [
            ("README.md", "README.md", "深大羽协 · 赛事助手", "程序介绍"),
            ("index.md", "docs/index.md", "文档中心", "使用帮助"),
            ("usage.md", "docs/usage.md", "使用说明", "使用帮助"),
            ("scheduling.md", "docs/scheduling.md", "赛程编排", "使用帮助"),
            ("troubleshooting.md", "docs/troubleshooting.md", "故障处理", "使用帮助"),
            ("fairness.md", "docs/fairness.md", "公平性与公开审计", "使用帮助"),
            ("rules-compliance.md", "docs/rules-compliance.md", "竞赛规则对应关系", "使用帮助"),
            ("algorithm.md", "docs/algorithm.md", "算法说明", "技术参考"),
            ("architecture.md", "docs/architecture.md", "系统架构", "技术参考"),
            ("build.md", "docs/build.md", "构建、验收与打包", "技术参考"),
            ("releases/v5.0.0.md", "docs/releases/v5.0.0.md", "5.0.0 发布说明", "技术参考")
        ];
        return entries.Select(entry =>
        {
            using var stream = typeof(HelpCatalog).Assembly.GetManifestResourceStream("Help/" + entry.Path)
                ?? throw new InvalidOperationException($"未能读取内置帮助：{entry.Path}");
            using var reader = new StreamReader(stream);
            return new HelpDocument(entry.Path, entry.SourcePath, entry.Title, entry.Category, reader.ReadToEnd());
        }).ToArray();
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
