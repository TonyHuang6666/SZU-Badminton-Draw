using System.Text.Json;
using BadmintonDraw.Desktop.Help;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace BadmintonDraw.HelpCompiler;

public sealed record HelpManifestEntry(string Path, string SourcePath, string Title, string Category);

public static class HelpCompiler
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseAutoIdentifiers(Markdig.Extensions.AutoIdentifiers.AutoIdentifierOptions.GitHub)
        .DisableHtml().Build();

    public static CompiledHelpLibrary CompileLibrary(string repositoryRoot, string manifestPath)
    {
        var entries = JsonSerializer.Deserialize<HelpManifestEntry[]>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions { RespectRequiredConstructorParameters = true })
            ?? throw new InvalidDataException($"Empty help manifest: {manifestPath}");
        var root = Path.GetFullPath(repositoryRoot) + Path.DirectorySeparatorChar;
        var documents = entries.Select(entry =>
        {
            var source = Path.GetFullPath(Path.Combine(root, entry.SourcePath));
            if (!source.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidDataException($"Help source is outside the repository: {entry.SourcePath}");
            try
            {
                var markdown = File.ReadAllText(source);
                return new HelpDocument(entry.Path, entry.SourcePath, entry.Title, entry.Category, markdown,
                    Markdown.ToPlainText(markdown, Pipeline).ReplaceLineEndings("\n"), CompileMarkdown(markdown));
            }
            catch (Exception error) when (error is IOException or NotSupportedException)
            {
                throw new InvalidDataException($"Could not compile help source '{entry.SourcePath}': {error.Message}", error);
            }
        }).ToArray();
        var library = new CompiledHelpLibrary(HelpLibraryCodec.CurrentVersion, documents);
        // Apply the same catalog validation as the runtime before producing a resource.
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(HelpLibraryCodec.Serialize(library)));
        return HelpLibraryCodec.Read(stream);
    }

    public static IReadOnlyList<HelpBlock> CompileMarkdown(string markdown) => Blocks(Markdown.Parse(markdown, Pipeline));

    private static IReadOnlyList<HelpBlock> Blocks(ContainerBlock container) => container
        .Where(block => block is not LinkReferenceDefinitionGroup and not LinkReferenceDefinition)
        .Select(Block).ToArray();

    private static HelpBlock Block(Block block) => block switch
    {
        HeadingBlock heading => new(HelpBlockKind.Heading)
            { Level = heading.Level, Anchor = heading.GetAttributes().Id, Inlines = Inlines(heading.Inline) },
        ParagraphBlock paragraph => new(HelpBlockKind.Paragraph) { Inlines = Inlines(paragraph.Inline) },
        CodeBlock code => new(HelpBlockKind.Code) { Text = code.Lines.ToString().ReplaceLineEndings("\n") },
        Table table => new(HelpBlockKind.Table) { Children = Blocks(table) },
        TableRow row => new(HelpBlockKind.TableRow) { IsHeader = row.IsHeader, Children = Blocks(row) },
        TableCell cell => new(HelpBlockKind.Container) { Children = Blocks(cell) },
        ListBlock list => new(HelpBlockKind.List) { IsOrdered = list.IsOrdered,
            OrderedStart = int.TryParse(list.OrderedStart, out var start) ? start : 1, Children = Blocks(list) },
        ListItemBlock item => new(HelpBlockKind.Container) { Children = Blocks(item) },
        QuoteBlock quote => new(HelpBlockKind.Quote) { Children = Blocks(quote) },
        ThematicBreakBlock => new(HelpBlockKind.Rule),
        _ => throw new NotSupportedException($"Unsupported help block {block.GetType().Name} at line {block.Line + 1}.")
    };

    private static IReadOnlyList<HelpInline> Inlines(ContainerInline? container) => container is null ? [] :
        container.Select(Inline).ToArray();

    private static HelpInline Inline(Inline inline) => inline switch
    {
        LiteralInline literal => new(HelpInlineKind.Text) { Text = literal.Content.ToString() },
        HtmlEntityInline entity => new(HelpInlineKind.Text) { Text = entity.Transcoded.ToString() },
        CodeInline code => new(HelpInlineKind.Code) { Text = code.Content },
        LineBreakInline line => new(HelpInlineKind.Break) { IsHard = line.IsHard },
        LinkInline link => new(link.IsImage ? HelpInlineKind.Image : HelpInlineKind.Link)
            { Href = link.Url ?? "", Children = Inlines(link) },
        EmphasisInline emphasis => new(HelpInlineKind.Emphasis)
            { DelimiterCount = emphasis.DelimiterCount, Children = Inlines(emphasis) },
        AutolinkInline link => new(HelpInlineKind.Link)
            { Href = link.Url, Children = [new(HelpInlineKind.Text) { Text = link.Url }] },
        _ => throw new NotSupportedException($"Unsupported help inline {inline.GetType().Name} at line {inline.Line + 1}.")
    };
}
