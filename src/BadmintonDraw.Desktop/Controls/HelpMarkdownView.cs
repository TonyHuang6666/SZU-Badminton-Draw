using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using BadmintonDraw.Desktop.Help;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace BadmintonDraw.Desktop.Controls;

/// <summary>Renders the trusted, packaged Markdown as selectable native controls, without a browser or HTML.</summary>
public sealed partial class HelpMarkdownView : UserControl
{
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Dictionary<string, Control> headings = new(StringComparer.Ordinal);
    private readonly List<SelectableTextBlock> textBlocks = [];
    private string currentQuery = "";
    private int searchIndex = -1;
    private double readingFontSize = 15;
    public event Action<string>? LinkClicked;

    public HelpMarkdownView()
    {
        Content = scroll;
        InitializeSelection();
    }

    public void SetDocument(string markdown, double fontSize)
    {
        ResetDocumentSelection();
        readingFontSize = fontSize;
        headings.Clear();
        textBlocks.Clear();
        searchIndex = -1;
        var body = BlockPanel();
        body.Margin = new Thickness(24, 16, 24, 28);
        foreach (var block in Markdown.Parse(markdown, HelpCatalog.Pipeline)) body.Children.Add(RenderBlock(block));
        scroll.Content = body;
        BuildSelectionDocument();
        scroll.Offset = default;
    }

    public bool ScrollToAnchor(string anchor)
    {
        if (anchor.Length == 0) { scroll.Offset = default; return true; }
        if (!headings.TryGetValue(anchor, out var heading)) return false;
        heading.BringIntoView();
        return true;
    }

    public (int Current, int Count) FindText(string query, bool next = false)
    {
        query = query.Trim();
        if (query != currentQuery || !next) searchIndex = -1;
        currentQuery = query;
        ClearDocumentSelection();
        var matches = new List<(SelectableTextBlock Text, int Start)>();
        foreach (var text in textBlocks)
        {
            text.SelectAll();
            var content = text.SelectedText ?? "";
            text.ClearSelection();
            if (query.Length == 0) continue;
            var offset = 0;
            while (content.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase) is var index && index >= 0)
            {
                matches.Add((text, index));
                offset = index + query.Length;
            }
        }
        if (matches.Count == 0) return (0, 0);
        searchIndex = (searchIndex + 1) % matches.Count;
        var match = matches[searchIndex];
        SelectRange(match.Text, match.Start, query.Length);
        match.Text.BringIntoView();
        return (searchIndex + 1, matches.Count);
    }

    private static StackPanel BlockPanel() => new() { Spacing = 12 };
    private SelectableTextBlock Text(string? text = null)
    {
        var block = new SelectableTextBlock
        {
            Text = text, FontSize = readingFontSize, TextWrapping = TextWrapping.Wrap,
            LineHeight = readingFontSize * 1.65
        };
        block.Bind(SelectableTextBlock.SelectionBrushProperty, this.GetResourceObservable("AppListSelectedBackgroundBrush"));
        block.Bind(SelectableTextBlock.SelectionForegroundBrushProperty, this.GetResourceObservable("AppSelectionTextBrush"));
        textBlocks.Add(block);
        separators[block] = "\n\n";
        return block;
    }

    private Control RenderBlock(Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var text = Text(InlineText(heading.Inline));
                text.FontSize = readingFontSize * (heading.Level == 1 ? 1.8 : heading.Level == 2 ? 1.4 : 1.15);
                text.LineHeight = double.NaN;
                text.FontWeight = FontWeight.SemiBold;
                text.Margin = new Thickness(0, heading.Level == 1 ? 0 : 12, 0, 2);
                if (heading.GetAttributes().Id is { } id) headings[id] = text;
                return text;
            }
            case ParagraphBlock paragraph:
            {
                if (HasHardLineBreak(paragraph.Inline)) return RenderParagraphLines(paragraph.Inline!);
                var text = Text();
                var position = 0;
                AppendInlines(text.Inlines!, paragraph.Inline, text, ref position);
                return text;
            }
            case CodeBlock code:
            {
                var text = Text(code.Lines.ToString());
                text.Classes.Add("help-code");
                text.FontFamily = FontFamily.Parse("Cascadia Mono,Menlo,Consolas,monospace");
                text.TextWrapping = TextWrapping.NoWrap;
                var border = new Border { Child = text, Padding = new Thickness(14), CornerRadius = new CornerRadius(6) };
                border.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppSurfaceMutedBrush"));
                return new ScrollViewer { Content = border, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            }
            case Table table:
                return RenderTable(table);
            case ListBlock list:
            {
                var panel = BlockPanel();
                var ordinal = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                var firstItem = true;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
                    var marker = Text(list.IsOrdered ? $"{ordinal++}." : "•");
                    separators[marker] = firstItem ? "\n\n" : "\n";
                    firstItem = false;
                    row.Children.Add(marker);
                    var firstContent = textBlocks.Count;
                    var contents = RenderContainer(item);
                    if (textBlocks.Count > firstContent) separators[textBlocks[firstContent]] = " ";
                    Grid.SetColumn(contents, 1); row.Children.Add(contents); panel.Children.Add(row);
                }
                return panel;
            }
            case QuoteBlock quote:
            {
                var border = new Border { Child = RenderContainer(quote), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(14, 4) };
                border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppAccentBrush"));
                return border;
            }
            case ThematicBreakBlock:
            {
                var border = new Border { Height = 1, Margin = new Thickness(0, 8) };
                border.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppBorderBrush"));
                return border;
            }
            case ContainerBlock container: return RenderContainer(container);
            case LeafBlock leaf: return Text(leaf.Lines.ToString());
            default: return new Border();
        }
    }

    private StackPanel RenderContainer(ContainerBlock container)
    {
        var panel = BlockPanel();
        foreach (var block in container) panel.Children.Add(RenderBlock(block));
        return panel;
    }

    private static bool HasHardLineBreak(ContainerInline? inlines) => inlines is not null && inlines.Any(inline =>
        inline is LineBreakInline { IsHard: true } || inline is ContainerInline nested && HasHardLineBreak(nested));

    private StackPanel RenderParagraphLines(ContainerInline inlines)
    {
        // Avalonia 12 can loop in its wrapped formatter when formatted runs contain an explicit newline.
        // Adjacent native blocks preserve the hard break; the document selection model joins their text.
        var panel = new StackPanel { Spacing = 0 };
        var line = Text();
        line.MinHeight = readingFontSize * 1.65;
        panel.Children.Add(line);
        var position = 0;
        Append(inlines, false, false, null);
        return panel;

        void AddText(string content, bool bold, bool italic, bool code, string? href)
        {
            if (content.Length == 0) return;
            var run = new Run(content);
            if (bold) run.FontWeight = FontWeight.Bold;
            if (italic) run.FontStyle = FontStyle.Italic;
            if (code)
            {
                run.FontFamily = FontFamily.Parse("Cascadia Mono,Menlo,Consolas,monospace");
                if (!bold) run.FontWeight = FontWeight.Medium;
            }
            if (href is null) line.Inlines!.Add(run);
            else
            {
                var span = CreateLinkSpan();
                span.Inlines.Add(run);
                line.Inlines!.Add(span);
                if (links.LastOrDefault() is { } previous && previous.Block == line && previous.Href == href &&
                    previous.Start + previous.Length == position)
                    links[^1] = previous with { Length = previous.Length + content.Length };
                else links.Add(new LinkRange(line, position, content.Length, href));
            }
            position += content.Length;
        }

        void Append(ContainerInline container, bool bold, bool italic, string? href)
        {
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal: AddText(literal.Content.ToString(), bold, italic, false, href); break;
                    case CodeInline code: AddText(code.Content, bold, italic, true, href); break;
                    case LineBreakInline { IsHard: true }:
                        line = Text();
                        line.MinHeight = readingFontSize * 1.65;
                        separators[line] = Environment.NewLine;
                        panel.Children.Add(line);
                        position = 0;
                        break;
                    case LineBreakInline: AddText(" ", bold, italic, false, href); break;
                    case LinkInline { IsImage: true } image: AddText(InlineText(image), bold, italic, false, href); break;
                    case LinkInline link: Append(link, bold, italic, link.Url ?? ""); break;
                    case EmphasisInline emphasis:
                        Append(emphasis, bold || emphasis.DelimiterCount >= 2, italic || emphasis.DelimiterCount == 1, href);
                        break;
                    case AutolinkInline link: AddText(link.Url, bold, italic, false, link.Url); break;
                    case ContainerInline nested: Append(nested, bold, italic, href); break;
                }
            }
        }
    }

    private Control RenderTable(Table table)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.Classes.Add("help-table");
        var columnCount = table.OfType<TableRow>().Select(r => r.Count).DefaultIfEmpty(1).Max();
        for (var c = 0; c < columnCount; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star) { MinWidth = 120 });
        var rowIndex = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var col = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                var firstContent = textBlocks.Count;
                var contents = RenderContainer(cell);
                if (textBlocks.Count > firstContent) separators[textBlocks[firstContent]] = col == 0 ? "\n" : "\t";
                if (row.IsHeader) contents.SetValue(TextElement.FontWeightProperty, FontWeight.SemiBold);
                var border = new Border { Child = contents, Padding = new Thickness(10), BorderThickness = new Thickness(0, 0, 1, 1) };
                border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppBorderBrush"));
                border.Bind(Border.BackgroundProperty, this.GetResourceObservable(row.IsHeader ? "AppTableHeaderBackgroundBrush" : "AppSurfaceBrush"));
                Grid.SetRow(border, rowIndex); Grid.SetColumn(border, col++); grid.Children.Add(border);
            }
            rowIndex++;
        }
        // A horizontal viewport keeps wide technical tables readable at the minimum window size.
        var viewport = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        viewport.SizeChanged += (_, _) => grid.Width = Math.Max(columnCount * 150, viewport.Bounds.Width);
        return viewport;
    }

    private void AppendInlines(InlineCollection target, ContainerInline? container, SelectableTextBlock owner, ref int position)
    {
        if (container is null) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString())); position += literal.Content.Length; break;
                case CodeInline code:
                    target.Add(new Run(code.Content) { FontFamily = FontFamily.Parse("Cascadia Mono,Menlo,Consolas,monospace"), FontWeight = FontWeight.Medium });
                    position += code.Content.Length;
                    break;
                case LineBreakInline line:
                    target.Add(new Run(line.IsHard ? Environment.NewLine : " "));
                    position += line.IsHard ? Environment.NewLine.Length : 1;
                    break;
                case LinkInline link:
                {
                    if (link.IsImage)
                    {
                        var label = InlineText(link);
                        target.Add(new Run(label)); position += label.Length; break;
                    }
                    var href = link.Url ?? "";
                    var span = CreateLinkSpan();
                    var start = position;
                    AppendInlines(span.Inlines, link, owner, ref position);
                    target.Add(span);
                    links.Add(new LinkRange(owner, start, position - start, href));
                    break;
                }
                case EmphasisInline emphasis:
                {
                    var span = new Span();
                    if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeight.Bold;
                    else span.FontStyle = FontStyle.Italic;
                    AppendInlines(span.Inlines, emphasis, owner, ref position); target.Add(span); break;
                }
                case ContainerInline nested: AppendInlines(target, nested, owner, ref position); break;
                case AutolinkInline link:
                {
                    var span = CreateLinkSpan();
                    span.Inlines.Add(new Run(link.Url));
                    target.Add(span);
                    links.Add(new LinkRange(owner, position, link.Url.Length, link.Url));
                    position += link.Url.Length;
                    break;
                }
            }
        }
    }

    private Span CreateLinkSpan()
    {
        // Keep the label in the paragraph's text flow and own the decoration on this UI dispatcher.
        var span = new Span
        {
            TextDecorations = new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Underline } }
        };
        span.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("AppAccentBrush"));
        return span;
    }

    private static string InlineText(ContainerInline? inline)
    {
        if (inline is null) return "";
        return string.Concat(inline.Select(child => child switch
        {
            LiteralInline literal => literal.Content.ToString(), CodeInline code => code.Content,
            LineBreakInline => " ", ContainerInline nested => InlineText(nested), AutolinkInline link => link.Url, _ => ""
        }));
    }
}
