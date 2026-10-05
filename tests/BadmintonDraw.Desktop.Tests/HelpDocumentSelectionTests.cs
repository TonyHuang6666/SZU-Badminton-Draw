using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.Controls;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class HelpDocumentSelectionTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DragAcrossParagraphsCopiesTheEntireRangeInReadingOrder(bool backwards) => ui.Dispatch(async () =>
    {
        var reader = new HelpMarkdownView();
        reader.SetDocument("第一段说明文字。\n\n第二段补充内容。\n\n第三段结束。", 15);
        var window = new Window { Content = reader, Width = 700, Height = 400 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var blocks = reader.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
            var start = At(window, blocks[0], 3);
            var end = At(window, blocks[2], 3);
            Drag(window, backwards ? end : start, backwards ? start : end);
            Assert.Equal("说明文字。", blocks[0].SelectedText);
            Assert.Equal("第二段补充内容。", blocks[1].SelectedText);
            Assert.Equal("第三段", blocks[2].SelectedText);
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("说明文字。\n\n第二段补充内容。\n\n第三段", await window.Clipboard!.TryGetTextAsync());
            return 0;
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData("# 标题\n\n第一段包含**加粗**和[帮助链接](usage.md)。\n\n第二段结束。", "标题\n\n第一段包含加粗和帮助链接。\n\n第二段结束。")]
    [InlineData("- 第一项\n- 第二项\n\n结束。", "• 第一项\n• 第二项\n\n结束。")]
    [InlineData("| 项目 | 说明 |\n| --- | --- |\n| 单打 | 比赛 |\n| 双打 | 名单 |", "项目\t说明\n单打\t比赛\n双打\t名单")]
    [InlineData("开始。\n\n```text\n第一行\n第二行\n```\n\n结束。", "开始。\n\n第一行\n第二行\n\n结束。")]
    public Task SelectAllCopiesVisibleTextAndPreservesStructuralSeparators(string markdown, string expected) => ui.Dispatch(async () =>
    {
        var reader = new HelpMarkdownView();
        reader.SetDocument(markdown, 15);
        var window = new Window { Content = reader, Width = 700, Height = 400 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            reader.Focus();
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(expected, await window.Clipboard!.TryGetTextAsync());
            return 0;
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task DragThroughLinksCopiesLabelsWithoutOpeningThemAndClickStillOpens() => ui.Dispatch(async () =>
    {
        var reader = new HelpMarkdownView();
        reader.SetDocument("先看[帮助文档](usage.md)再继续。\n\n后续说明。", 15);
        var navigated = new List<string>();
        reader.LinkClicked += navigated.Add;
        var window = new Window { Content = reader, Width = 700, Height = 400 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var blocks = reader.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
            Drag(window, At(window, blocks[0], 3), At(window, blocks[1], 2));
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("助文档再继续。\n\n后续", await window.Clipboard!.TryGetTextAsync());
            Assert.Empty(navigated);
            var point = At(window, blocks[0], 4);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal(new[] { "usage.md" }, navigated);
            return 0;
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task SearchAndDocumentChangesReplaceOldSelection() => ui.Dispatch(async () =>
    {
        var reader = new HelpMarkdownView();
        reader.SetDocument("第一段。\n\n第二段查询词。", 15);
        var window = new Window { Content = reader, Width = 700, Height = 400 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            reader.Focus();
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
            reader.FindText("查询词");
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("查询词", await window.Clipboard!.TryGetTextAsync());
            reader.SetDocument("新文档。", 24);
            window.UpdateLayout();
            await window.Clipboard!.SetTextAsync("保留剪贴板");
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            Assert.Equal("保留剪贴板", await window.Clipboard!.TryGetTextAsync());
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("新文档。", await window.Clipboard!.TryGetTextAsync());
            return 0;
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task LinksRemainReachableByKeyboard() => ui.Dispatch(() =>
    {
        var reader = new HelpMarkdownView();
        reader.SetDocument("请看[帮助文档](usage.md)和[编排说明](scheduling.md)。", 15);
        var navigated = new List<string>();
        reader.LinkClicked += navigated.Add;
        var window = new Window { Content = reader, Width = 700, Height = 400 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            reader.Focus();
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Assert.Contains(reader.GetVisualDescendants().OfType<SelectableTextBlock>(), b => b.SelectedText == "帮助文档");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.Equal(new[] { "usage.md", "scheduling.md" }, navigated);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task RightClickCopyPreservesAllHighlightedParagraphs() => ui.Dispatch(async () =>
    {
        var reader = new HelpMarkdownView();
        reader.SetDocument("第一段说明文字。\n\n第二段补充内容。", 15);
        var window = new Window { Content = reader, Width = 700, Height = 400 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var blocks = reader.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
            Drag(window, At(window, blocks[0], 3), At(window, blocks[1], 3));
            var outsideSelection = At(window, blocks[0], 0);
            window.MouseDown(outsideSelection, MouseButton.Right);
            window.MouseUp(outsideSelection, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("说明文字。", blocks[0].SelectedText);
            Assert.Equal("第二段", blocks[1].SelectedText);
            Assert.True(reader.ContextMenu!.IsOpen);
            reader.ContextMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "复制"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("说明文字。\n\n第二段", await window.Clipboard!.TryGetTextAsync());
            return 0;
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task DraggingAtTheViewportEdgeScrollsAndKeepsSelecting() => ui.Dispatch(async () =>
    {
        var reader = new HelpMarkdownView();
        reader.SetDocument(string.Join("\n\n", Enumerable.Range(1, 50).Select(i => $"第{i}段文档说明。")), 15);
        var window = new Window { Content = reader, Width = 700, Height = 300 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var blocks = reader.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
            var start = At(window, blocks[0], 0);
            var end = new Point(start.X, reader.Bounds.Height - 2);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            await Task.Delay(250);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            window.MouseUp(end, MouseButton.Left);
            Assert.True(Assert.IsType<ScrollViewer>(reader.Content).Offset.Y > 0);
            Assert.Equal("第1段文档说明。", blocks[0].SelectedText);
            Assert.True(blocks.Count(b => !string.IsNullOrEmpty(b.SelectedText)) > 5);
            return 0;
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    private static Point At(Window window, SelectableTextBlock text, int index)
    {
        var bounds = text.TextLayout.HitTestTextPosition(index);
        return text.TranslatePoint(new Point(bounds.X + 0.1, bounds.Y + bounds.Height / 2), window)!.Value;
    }

    private static void Drag(Window window, Point start, Point end)
    {
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    public void Dispose() => ui.Dispose();
}
