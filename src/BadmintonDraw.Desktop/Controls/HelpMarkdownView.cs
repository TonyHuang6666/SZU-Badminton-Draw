using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using BadmintonDraw.Desktop.Help;

namespace BadmintonDraw.Desktop.Controls;

/// <summary>Renders compiled help documents as selectable native controls.</summary>
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

    public void SetDocument(IReadOnlyList<HelpBlock> blocks, double fontSize)
    {
        ResetDocumentSelection();
        readingFontSize = fontSize;
        headings.Clear();
        textBlocks.Clear();
        searchIndex = -1;
        var body = BlockPanel();
        body.Margin = new Thickness(24, 16, 24, 28);
        foreach (var block in blocks) body.Children.Add(RenderBlock(block));
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

    private Control RenderBlock(HelpBlock block)
    {
        switch (block.Kind)
        {
            case HelpBlockKind.Heading:
            {
                var text = Text(InlineText(block.Inlines));
                text.FontSize = readingFontSize * (block.Level == 1 ? 1.8 : block.Level == 2 ? 1.4 : 1.15);
                text.LineHeight = double.NaN;
                text.FontWeight = FontWeight.SemiBold;
                text.Margin = new Thickness(0, block.Level == 1 ? 0 : 12, 0, 2);
                if (block.Anchor is { } id) headings[id] = text;
                return text;
            }
            case HelpBlockKind.Paragraph:
            {
                if (HasHardLineBreak(block.Inlines)) return RenderParagraphLines(block.Inlines!);
                var text = Text();
                var position = 0;
                AppendInlines(text.Inlines!, block.Inlines, text, ref position);
                return text;
            }
            case HelpBlockKind.Code:
            {
                var text = Text(block.Text);
                text.Classes.Add("help-code");
                text.FontFamily = FontFamily.Parse("Cascadia Mono,Menlo,Consolas,monospace");
                text.TextWrapping = TextWrapping.NoWrap;
                var border = new Border { Child = text, Padding = new Thickness(14), CornerRadius = new CornerRadius(6) };
                border.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppSurfaceMutedBrush"));
                return new ScrollViewer { Content = border, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            }
            case HelpBlockKind.Table:
                return RenderTable(block);
            case HelpBlockKind.List:
            {
                var panel = BlockPanel();
                var ordinal = block.OrderedStart;
                var firstItem = true;
                foreach (var item in block.Children ?? [])
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
                    var marker = Text(block.IsOrdered ? $"{ordinal++}." : "•");
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
            case HelpBlockKind.Quote:
            {
                var border = new Border { Child = RenderContainer(block), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(14, 4) };
                border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AppAccentBrush"));
                return border;
            }
            case HelpBlockKind.Rule:
            {
                var border = new Border { Height = 1, Margin = new Thickness(0, 8) };
                border.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppBorderBrush"));
                return border;
            }
            case HelpBlockKind.Container: return RenderContainer(block);
            default: throw new InvalidDataException($"不支持的帮助文档块：{block.Kind}");
        }
    }

    private StackPanel RenderContainer(HelpBlock container)
    {
        var panel = BlockPanel();
        foreach (var block in container.Children ?? []) panel.Children.Add(RenderBlock(block));
        return panel;
    }

    private static bool HasHardLineBreak(IReadOnlyList<HelpInline>? inlines) => inlines is not null && inlines.Any(inline =>
        inline is { Kind: HelpInlineKind.Break, IsHard: true } || HasHardLineBreak(inline.Children));

    private StackPanel RenderParagraphLines(IReadOnlyList<HelpInline> inlines)
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

        void Append(IReadOnlyList<HelpInline> container, bool bold, bool italic, string? href)
        {
            foreach (var inline in container)
            {
                switch (inline.Kind)
                {
                    case HelpInlineKind.Text: AddText(inline.Text ?? "", bold, italic, false, href); break;
                    case HelpInlineKind.Code: AddText(inline.Text ?? "", bold, italic, true, href); break;
                    case HelpInlineKind.Break when inline.IsHard:
                        line = Text();
                        line.MinHeight = readingFontSize * 1.65;
                        separators[line] = Environment.NewLine;
                        panel.Children.Add(line);
                        position = 0;
                        break;
                    case HelpInlineKind.Break: AddText(" ", bold, italic, false, href); break;
                    case HelpInlineKind.Image: AddText(InlineText(inline.Children), bold, italic, false, href); break;
                    case HelpInlineKind.Link: Append(inline.Children ?? [], bold, italic, inline.Href ?? ""); break;
                    case HelpInlineKind.Emphasis:
                        Append(inline.Children ?? [], bold || inline.DelimiterCount >= 2, italic || inline.DelimiterCount == 1, href);
                        break;
                    default: throw new InvalidDataException($"不支持的帮助行内内容：{inline.Kind}");
                }
            }
        }
    }

    private Control RenderTable(HelpBlock table)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.Classes.Add("help-table");
        var columnCount = (table.Children ?? []).Select(r => r.Children?.Count ?? 0).DefaultIfEmpty(1).Max();
        for (var c = 0; c < columnCount; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star) { MinWidth = 120 });
        var rowIndex = 0;
        foreach (var row in table.Children ?? [])
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var col = 0;
            foreach (var cell in row.Children ?? [])
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

    private void AppendInlines(InlineCollection target, IReadOnlyList<HelpInline>? container, SelectableTextBlock owner, ref int position)
    {
        if (container is null) return;
        foreach (var inline in container)
        {
            switch (inline.Kind)
            {
                case HelpInlineKind.Text:
                    target.Add(new Run(inline.Text ?? "")); position += inline.Text?.Length ?? 0; break;
                case HelpInlineKind.Code:
                    target.Add(new Run(inline.Text ?? "") { FontFamily = FontFamily.Parse("Cascadia Mono,Menlo,Consolas,monospace"), FontWeight = FontWeight.Medium });
                    position += inline.Text?.Length ?? 0;
                    break;
                case HelpInlineKind.Break:
                    target.Add(new Run(inline.IsHard ? Environment.NewLine : " "));
                    position += inline.IsHard ? Environment.NewLine.Length : 1;
                    break;
                case HelpInlineKind.Image:
                    var label = InlineText(inline.Children);
                    target.Add(new Run(label)); position += label.Length; break;
                case HelpInlineKind.Link:
                {
                    var href = inline.Href ?? "";
                    var span = CreateLinkSpan();
                    var start = position;
                    AppendInlines(span.Inlines, inline.Children, owner, ref position);
                    target.Add(span);
                    links.Add(new LinkRange(owner, start, position - start, href));
                    break;
                }
                case HelpInlineKind.Emphasis:
                {
                    var span = new Span();
                    if (inline.DelimiterCount >= 2) span.FontWeight = FontWeight.Bold;
                    else span.FontStyle = FontStyle.Italic;
                    AppendInlines(span.Inlines, inline.Children, owner, ref position); target.Add(span); break;
                }
                default: throw new InvalidDataException($"不支持的帮助行内内容：{inline.Kind}");
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

    private static string InlineText(IReadOnlyList<HelpInline>? inline)
    {
        if (inline is null) return "";
        return string.Concat(inline.Select(child => child.Kind switch
        {
            HelpInlineKind.Text or HelpInlineKind.Code => child.Text,
            HelpInlineKind.Break => " ", _ => InlineText(child.Children)
        }));
    }
}
