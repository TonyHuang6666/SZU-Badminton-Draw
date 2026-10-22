using System.Text.Json;
using System.Text.Json.Serialization;

namespace BadmintonDraw.Desktop.Help;

// Shared as source with the build tool. This format contains document data only.
public sealed record HelpDocument(string Path, string SourcePath, string Title, string Category,
    string Markdown, string PlainText, IReadOnlyList<HelpBlock> Blocks);

public enum HelpBlockKind { Heading, Paragraph, Code, Table, TableRow, Container, List, Quote, Rule }
public enum HelpInlineKind { Text, Code, Break, Link, Image, Emphasis }

public sealed record HelpBlock(HelpBlockKind Kind)
{
    public string? Text { get; init; }
    public string? Anchor { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Level { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsHeader { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsOrdered { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int OrderedStart { get; init; }
    public IReadOnlyList<HelpBlock>? Children { get; init; }
    public IReadOnlyList<HelpInline>? Inlines { get; init; }
}

public sealed record HelpInline(HelpInlineKind Kind)
{
    public string? Text { get; init; }
    public string? Href { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsHard { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DelimiterCount { get; init; }
    public IReadOnlyList<HelpInline>? Children { get; init; }
}

public sealed record CompiledHelpLibrary(int Version, IReadOnlyList<HelpDocument> Documents);

public static class HelpLibraryCodec
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter<HelpBlockKind>(allowIntegerValues: false),
            new JsonStringEnumConverter<HelpInlineKind>(allowIntegerValues: false) }
    };

    public static string Serialize(CompiledHelpLibrary library) => JsonSerializer.Serialize(library, Options);

    public static CompiledHelpLibrary Read(Stream stream)
    {
        var library = JsonSerializer.Deserialize<CompiledHelpLibrary>(stream, Options)
            ?? throw new InvalidDataException("内置帮助数据为空。");
        if (library.Version != CurrentVersion)
            throw new InvalidDataException($"不支持的内置帮助格式版本：{library.Version}");
        if (library.Documents is null || library.Documents.Count == 0 ||
            library.Documents.Any(d => d is null || string.IsNullOrWhiteSpace(d.Path) ||
                string.IsNullOrWhiteSpace(d.SourcePath) || string.IsNullOrWhiteSpace(d.Title) ||
                string.IsNullOrWhiteSpace(d.Category) || d.Markdown is null || d.PlainText is null || d.Blocks is null) ||
            library.Documents.Select(d => d.Path).Distinct(StringComparer.Ordinal).Count() != library.Documents.Count ||
            library.Documents.Select(d => d.SourcePath).Distinct(StringComparer.Ordinal).Count() != library.Documents.Count)
            throw new InvalidDataException("内置帮助目录缺失或重复。");
        return library;
    }
}
