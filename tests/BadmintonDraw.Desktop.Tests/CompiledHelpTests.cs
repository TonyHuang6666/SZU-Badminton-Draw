using System.Text;
using System.Text.Json;
using BadmintonDraw.Desktop.Help;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class CompiledHelpTests
{
    [Fact]
    public void DesktopCanReadHelpWithoutReferencingTheMarkdownCompiler()
    {
        Assert.DoesNotContain(typeof(App).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "Markdig" or "BadmintonDraw.HelpCompiler");
    }

    [Fact]
    public void CompiledDocumentRoundTripPreservesAnchorsStylesAndLinkTargets()
    {
        const string markdown = "# 设置种子\n\n# 设置种子\n\n正文 **强调 *嵌套*** `code` [目标](usage.md#设置种子)  \n下一行\n\n| 项目 | 说明 |\n| --- | --- |\n| 单打 | 比赛 |";
        var blocks = HelpCompiler.HelpCompiler.CompileMarkdown(markdown);
        var page = new HelpDocument("test.md", "test.md", "测试", "使用帮助", markdown, "测试", blocks);
        var json = HelpLibraryCodec.Serialize(new(1, [page]));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var copy = Assert.Single(HelpLibraryCodec.Read(stream).Documents);
        Assert.Equal("设置种子", copy.Blocks[0].Anchor);
        Assert.Equal("设置种子-1", copy.Blocks[1].Anchor);
        var inlines = copy.Blocks[2].Inlines!;
        var strong = Assert.Single(inlines, i => i.Kind == HelpInlineKind.Emphasis);
        Assert.Equal(2, strong.DelimiterCount);
        Assert.Contains(strong.Children!, i => i.Kind == HelpInlineKind.Emphasis && i.DelimiterCount == 1);
        Assert.Contains(inlines, i => i.Kind == HelpInlineKind.Code && i.Text == "code");
        Assert.Contains(inlines, i => i.Kind == HelpInlineKind.Link && i.Href == "usage.md#设置种子");
        Assert.Contains(inlines, i => i.Kind == HelpInlineKind.Break && i.IsHard);
        var table = Assert.Single(copy.Blocks, b => b.Kind == HelpBlockKind.Table);
        Assert.True(table.Children![0].IsHeader);
        Assert.Equal("单打", table.Children[1].Children![0].Children![0].Inlines![0].Text);
        Assert.Equal(json, HelpLibraryCodec.Serialize(new(1, [copy])));
    }

    [Theory]
    [InlineData("{\"Version\":2,\"Documents\":[]}")]
    [InlineData("{\"Version\":1,\"Documents\":[]}")]
    public void InvalidLibraryDoesNotSilentlyBecomeAnEmptyHelpWindow(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => HelpLibraryCodec.Read(stream));
    }

    [Fact]
    public void CompilerReadsFreshSourcesAndFailsWhenASourceDisappears()
    {
        var directory = Directory.CreateTempSubdirectory("compiled-help-test-");
        try
        {
            var manifest = Path.Combine(directory.FullName, "manifest.json");
            var source = Path.Combine(directory.FullName, "test.md");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new[]
                { new HelpCompiler.HelpManifestEntry("test.md", "test.md", "测试", "使用帮助") }));
            File.WriteAllText(source, "# 第一版\n\n正文。");
            var first = HelpCompiler.HelpCompiler.CompileLibrary(directory.FullName, manifest);
            Assert.Equal("# 第一版\n\n正文。", Assert.Single(first.Documents).Markdown);
            Assert.Equal(HelpLibraryCodec.Serialize(first), HelpLibraryCodec.Serialize(
                HelpCompiler.HelpCompiler.CompileLibrary(directory.FullName, manifest)));
            File.WriteAllText(source, "# 第二版\n\n新增内容。");
            var second = HelpCompiler.HelpCompiler.CompileLibrary(directory.FullName, manifest);
            Assert.Contains("新增内容", Assert.Single(second.Documents).PlainText);
            File.Delete(source);
            var failure = Assert.Throws<InvalidDataException>(() =>
                HelpCompiler.HelpCompiler.CompileLibrary(directory.FullName, manifest));
            Assert.Contains("test.md", failure.Message);
        }
        finally { directory.Delete(recursive: true); }
    }
}
