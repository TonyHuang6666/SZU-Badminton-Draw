using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.Help;
using BadmintonDraw.Desktop.Controls;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class OfflineHelpTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Fact]
    public void PackagedCatalogLoadsEveryCurrentDocumentWithoutShippingArchives()
    {
        var catalog = new HelpCatalog();
        var pages = catalog.Documents.ToArray();
        Assert.Equal(12, pages.Length);
        Assert.Contains(pages, p => p.Path == "README.md" && p.Category == "程序介绍");
        Assert.Contains(pages, p => p.Path == "releases/v5.0.1.md" && p.Category == "技术参考");
        Assert.Contains(pages, p => p.Path == "releases/v5.0.0.md");
        Assert.All(pages, p => Assert.StartsWith("# ", p.Markdown));
        Assert.DoesNotContain(typeof(App).Assembly.GetManifestResourceNames(), p => p.Contains("archive", StringComparison.OrdinalIgnoreCase));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BadmintonDraw.sln"))) root = root.Parent;
        Assert.NotNull(root);
        // This catches a stale copied document or a wrong MSBuild resource mapping.
        Assert.All(pages, page => Assert.Equal(File.ReadAllText(Path.Combine(root.FullName,
            page.Path == "README.md" ? "README.md" : Path.Combine("docs", page.Path))), page.Markdown));
    }

    [Fact]
    public void SearchFindsBodyTextAndKeepsUserAndTechnicalCategories()
    {
        var model = new HelpViewModel();
        model.SearchText = "SQLite";
        Assert.Contains(model.TechnicalDocuments, p => p.Path == "architecture.md");
        model.SearchText = " 没有任何匹配的帮助词组abcdef ";
        Assert.True(model.HasNoSearchResults);
        model.SearchText = "";
        Assert.Contains(model.UserDocuments, p => p.Path == "usage.md");
        Assert.DoesNotContain(model.UserDocuments, p => p.Path == "build.md");
        Assert.Contains(model.TechnicalDocuments, p => p.Path == "build.md");
    }

    [Fact]
    public void SearchIncludesIntroductionEvenWhenNoUsageOrTechnicalDocumentMatches()
    {
        var model = new HelpViewModel { SearchText = "BFSZU.cpp" };
        Assert.False(model.HasNoSearchResults);
        Assert.Empty(model.UserDocuments);
        Assert.Empty(model.TechnicalDocuments);
    }

    [Theory]
    [InlineData("usage.md", "scheduling.md#选手兼项与负荷核对", "Internal", "scheduling.md", "选手兼项与负荷核对")]
    [InlineData("releases/v5.0.0.md", "../usage.md#设置种子", "Internal", "usage.md", "设置种子")]
    [InlineData("usage.md", "#设置种子", "Internal", "usage.md", "设置种子")]
    [InlineData("README.md", "docs/usage.md#设置种子", "Internal", "usage.md", "设置种子")]
    [InlineData("README.md", "docs/releases/v5.0.1.md", "Internal", "releases/v5.0.1.md", "")]
    [InlineData("README.md", "docs/releases/v5.0.0.md", "Internal", "releases/v5.0.0.md", "")]
    [InlineData("README.md", "#项目起源与自主研发", "Internal", "README.md", "项目起源与自主研发")]
    [InlineData("index.md", "../README.md", "Internal", "README.md", "")]
    [InlineData("releases/v5.0.0.md", "../../README.md", "Internal", "README.md", "")]
    [InlineData("README.md", "LICENSE", "Unavailable", null, "")]
    [InlineData("README.md", "samples/v5/README.md", "Unavailable", null, "")]
    [InlineData("README.md", "docs/archive/index.md", "Unavailable", null, "")]
    [InlineData("README.md", "../README.md", "Unavailable", null, "")]
    [InlineData("usage.md", "https://example.com/docs", "External", null, "")]
    [InlineData("usage.md", "../src/example.cs", "Unavailable", null, "")]
    [InlineData("usage.md", "archive/index.md", "Unavailable", null, "")]
    [InlineData("usage.md", "file:///etc/passwd", "Blocked", null, "")]
    [InlineData("usage.md", "javascript:alert(1)", "Blocked", null, "")]
    [InlineData("usage.md", "data:text/html,hello", "Blocked", null, "")]
    [InlineData("usage.md", "//example.com", "Blocked", null, "")]
    [InlineData("usage.md", "%66ile%3A%2F%2F%2Fetc/passwd", "Blocked", null, "")]
    public void LinksStayInTheOfflineLibraryUnlessTheyAreExplicitWebUrls(string current, string href, string kind, string? path, string anchor)
    {
        var catalog = new HelpCatalog();
        var target = catalog.ResolveLink(current, href);
        Assert.Equal(kind, target.Kind.ToString());
        Assert.Equal(path, target.DocumentPath);
        Assert.Equal(anchor, target.Anchor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RenderedHelpSupportsTablesCodeLinksAndFontSize(bool dark) => ui.Dispatch(() =>
    {
        var window = new HelpWindow();
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var model = Assert.IsType<HelpViewModel>(window.DataContext);
            model.Navigate("docs/usage.md#设置种子");
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal("usage.md", model.SelectedDocument.Path);
            Assert.Contains(window.GetVisualDescendants().OfType<Grid>(), c => c.Classes.Contains("help-table"));
            Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text?.Contains("种子") == true);
            var paragraph = window.GetVisualDescendants().OfType<SelectableTextBlock>()
                .First(t => ReadableText(t).Contains("赛程编排说明", StringComparison.Ordinal));
            ClickText(window, paragraph, "赛程编排说明");
            Assert.Equal("scheduling.md", model.SelectedDocument.Path);
            model.Navigate("build.md"); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Classes.Contains("help-code") && t.Text!.Contains("dotnet"));
            var before = window.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Classes.Contains("help-code")).FontSize;
            window.FindControl<Button>("HelpIncreaseFont")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(window.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Classes.Contains("help-code")).FontSize > before);
            Assert.True(window.FindControl<Button>("HelpCopyText")!.IsEnabled);
            Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, window.ActualThemeVariant);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task InternalChapterLinksScrollToTheHeadingAndUnavailableLinksExplainWhy() => ui.Dispatch(() =>
    {
        var window = new HelpWindow();
        try
        {
            window.Show(); window.UpdateLayout();
            var model = Assert.IsType<HelpViewModel>(window.DataContext);
            model.Navigate("docs/usage.md#设置种子");
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var reader = window.FindControl<HelpMarkdownView>("HelpDocument")!;
            var scroll = Assert.IsType<ScrollViewer>(reader.Content);
            Assert.True(scroll.Offset.Y > 0);
            Assert.DoesNotContain("未找到", model.StatusText);
            model.Navigate("archive/index.md");
            Assert.Equal("usage.md", model.SelectedDocument.Path);
            Assert.Contains("未内置", model.StatusText);
            model.Navigate("file:///tmp/test");
            Assert.Equal("usage.md", model.SelectedDocument.Path);
            Assert.Contains("未打开", model.StatusText);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task CopyButtonWritesTheCurrentReadableDocumentToTheClipboard() => ui.Dispatch(async () =>
    {
        var window = new HelpWindow();
        try
        {
            window.Show(); window.UpdateLayout();
            var model = Assert.IsType<HelpViewModel>(window.DataContext);
            window.FindControl<Button>("HelpCopyText")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var copied = await window.Clipboard!.TryGetTextAsync();
            Assert.StartsWith("深大羽协 · 赛事助手", copied);
            Assert.Contains("项目起源与自主研发", copied);
            Assert.DoesNotContain("[使用说明](docs/usage.md)", copied);
            Assert.Contains("已复制", model.StatusText);
            return 0;
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData(15)]
    [InlineData(24)]
    public Task InlineLinksAndParagraphGlyphsFitTheMeasuredLineHeight(double fontSize) => ui.Dispatch(() =>
    {
        var reader = new HelpMarkdownView();
        reader.SetDocument("本目录的使用说明与技术文档按当前程序编写。功能范围见[发布说明](releases/v5.0.0.md)。", fontSize);
        var window = new Window { Content = reader, Width = 760, Height = 250 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var text = Assert.Single(reader.GetVisualDescendants().OfType<SelectableTextBlock>());
            Assert.All(text.TextLayout.TextLines, line => Assert.True(line.Extent <= line.Height + 1,
                $"Glyph extent {line.Extent} exceeds line height {line.Height}; baseline {line.Baseline}."));
            var linkStart = ReadableText(text).IndexOf("发布说明", StringComparison.Ordinal);
            Assert.True(linkStart >= 0);
            Assert.All(text.TextLayout.HitTestTextRange(linkStart, "发布说明".Length), bounds =>
                Assert.True(bounds.Top >= -1 && bounds.Bottom <= text.Bounds.Height + 1));
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task SearchLocatesAndSelectsTheMatchingTextInTheOpenDocument() => ui.Dispatch(() =>
    {
        var window = new HelpWindow();
        try
        {
            window.Show();
            var model = Assert.IsType<HelpViewModel>(window.DataContext);
            model.Navigate("docs/architecture.md");
            window.FindControl<TextBox>("HelpSearch")!.Text = "SQLite";
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.SelectedText == "SQLite");
            var next = window.FindControl<Button>("HelpFindNext");
            Assert.NotNull(next); Assert.True(next.IsVisible);
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.SelectedText == "SQLite");
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task SearchFindsVisibleLinkLabelsInTechnicalDocuments() => ui.Dispatch(() =>
    {
        var window = new HelpWindow();
        try
        {
            window.Show();
            var model = Assert.IsType<HelpViewModel>(window.DataContext);
            model.Navigate("docs/algorithm.md");
            window.FindControl<TextBox>("HelpSearch")!.Text = "DrawService";
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.SelectedText == "DrawService");
            Assert.Contains("第 1 /", model.StatusText);
            var reader = window.FindControl<HelpMarkdownView>("HelpDocument")!;
            Assert.True(Assert.IsType<ScrollViewer>(reader.Content).Offset.Y > 0);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task IntroductionOpensByDefaultAndAllThreeCategoriesNavigateAndSearch(bool dark) => ui.Dispatch(() =>
    {
        var window = new HelpWindow { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var model = Assert.IsType<HelpViewModel>(window.DataContext);
            Assert.Equal("README.md", model.SelectedDocument.Path);
            var intro = window.FindControl<ListBox>("HelpIntroDocuments");
            var usage = window.FindControl<ListBox>("HelpUserDocuments")!;
            var technical = window.FindControl<ListBox>("HelpTechnicalDocuments")!;
            Assert.NotNull(intro);
            Assert.Same(model.SelectedDocument, intro.SelectedItem);
            Assert.True(intro.TranslatePoint(default, window)!.Value.Y < usage.TranslatePoint(default, window)!.Value.Y);

            var linkParagraph = window.GetVisualDescendants().OfType<SelectableTextBlock>()
                .First(t => ReadableText(t).Contains("使用说明", StringComparison.Ordinal));
            ClickText(window, linkParagraph, "使用说明");
            Assert.Equal("usage.md", model.SelectedDocument.Path);
            Assert.Null(intro.SelectedItem);
            Assert.Null(technical.SelectedItem);
            Assert.Same(model.SelectedDocument, usage.SelectedItem);

            technical.SelectedItem = technical.Items.OfType<HelpDocument>().Single(d => d.Path == "architecture.md");
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal("architecture.md", model.SelectedDocument.Path);
            Assert.Null(intro.SelectedItem);
            Assert.Null(usage.SelectedItem);

            window.FindControl<TextBox>("HelpSearch")!.Text = "BFSZU.cpp";
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.False(model.HasNoSearchResults);
            Assert.Empty(usage.Items);
            Assert.Empty(technical.Items);
            intro.SelectedItem = Assert.Single(intro.Items.OfType<HelpDocument>());
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal("README.md", model.SelectedDocument.Path);
            Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.SelectedText == "BFSZU.cpp");
            Assert.Null(usage.SelectedItem);
            Assert.Null(technical.SelectedItem);

            var reader = window.FindControl<HelpMarkdownView>("HelpDocument")!;
            var scroll = Assert.IsType<ScrollViewer>(reader.Content);
            var documentBody = scroll.Content;
            var offset = scroll.Offset;
            window.FindControl<TextBox>("HelpSearch")!.Text = "没有任何匹配的帮助词组abcdef";
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(model.HasNoSearchResults);
            Assert.Null(intro.SelectedItem);
            window.FindControl<TextBox>("HelpSearch")!.Text = "";
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.False(model.HasNoSearchResults);
            Assert.Same(model.SelectedDocument, intro.SelectedItem);
            Assert.Same(documentBody, scroll.Content);
            Assert.Equal(offset, scroll.Offset);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData("正文 **强调** `code` [跳转目标](usage.md) 尾文")]
    [InlineData("正文 **强调**  \n[跳转目标](usage.md) 尾文")]
    public Task StyledLinksKeepTheirExactClickableRangeAfterFormattedText(string markdown) => ui.Dispatch(async () =>
    {
        var reader = new HelpMarkdownView();
        reader.SetDocument(markdown, 15);
        string? clicked = null;
        reader.LinkClicked += href => clicked = href;
        var window = new Window { Content = reader, Width = 760, Height = 250 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var blocks = reader.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
            var paragraph = Assert.Single(blocks, t => ReadableText(t).Contains("跳转目标", StringComparison.Ordinal));
            ClickText(window, paragraph, "跳转目标");
            Assert.Equal("usage.md", clicked);
            Assert.All(blocks.SelectMany(b => b.TextLayout.TextLines), line => Assert.True(line.Extent <= line.Height + 1));
            reader.Focus();
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control);
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.C, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            var copied = await window.Clipboard!.TryGetTextAsync();
            Assert.Equal(markdown.Contains('\n') ? $"正文 强调{Environment.NewLine}跳转目标 尾文" : "正文 强调 code 跳转目标 尾文", copied);
            return 0;
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    private static string ReadableText(SelectableTextBlock text) => text.Text ?? text.Inlines?.Text ?? "";

    private static void ClickText(Window window, SelectableTextBlock text, string label)
    {
        text.BringIntoView(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var start = ReadableText(text).IndexOf(label, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var character = text.TextLayout.HitTestTextPosition(start + 1);
        var point = text.TranslatePoint(character.Center, window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
    }

    public void Dispose() => ui.Dispose();
}
