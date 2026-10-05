using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace BadmintonDraw.Desktop.Controls;

public sealed partial class HelpMarkdownView
{
    private sealed record TextRange(SelectableTextBlock Block, int Start, string Text);
    private sealed record LinkRange(SelectableTextBlock Block, int Start, int Length, string Href);
    private readonly List<TextRange> selectionBlocks = [];
    private readonly List<LinkRange> links = [];
    private readonly Dictionary<SelectableTextBlock, string> separators = [];
    private readonly DispatcherTimer selectionScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private string documentText = "";
    private int selectionAnchor;
    private int selectionEnd;
    private IPointer? selectionPointer;
    private Point pressPoint;
    private Point dragPoint;
    private bool hasDragged;
    private LinkRange? pressedLink;
    private LinkRange? keyboardLink;
    private LinkRange? contextLink;
    public event Action<string>? CopyStatusChanged;

    private void InitializeSelection()
    {
        Focusable = true;
        AutomationProperties.SetName(this, "文档正文");
        AddHandler(PointerPressedEvent, BeginSelection, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, MoveSelection, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, EndSelection, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, SelectionKeyDown, RoutingStrategies.Tunnel);
        PointerCaptureLost += (_, _) => StopSelecting();
        DetachedFromVisualTree += (_, _) => StopSelecting();
        selectionScrollTimer.Tick += (_, _) => ScrollSelection();

        var copy = new MenuItem { Header = "复制" };
        copy.Click += async (_, _) => await CopySelectionAsync();
        var all = new MenuItem { Header = "全选" };
        all.Click += (_, _) => { Focus(); keyboardLink = null; SetSelection(0, documentText.Length); };
        var open = new MenuItem { Header = "打开链接" };
        open.Click += (_, _) => { if (contextLink is { } link) LinkClicked?.Invoke(link.Href); };
        var menu = new ContextMenu { ItemsSource = new object[] { copy, all, open } };
        menu.Opening += (_, _) =>
        {
            copy.IsEnabled = selectionAnchor != selectionEnd;
            all.IsEnabled = documentText.Length > 0;
            open.IsVisible = contextLink is not null;
        };
        ContextMenu = menu;
    }

    private void ResetDocumentSelection()
    {
        StopSelecting();
        links.Clear();
        separators.Clear();
        selectionBlocks.Clear();
        documentText = "";
        selectionAnchor = selectionEnd = 0;
        keyboardLink = contextLink = null;
        ClearValue(AutomationProperties.HelpTextProperty);
    }

    private void BuildSelectionDocument()
    {
        var text = new StringBuilder();
        foreach (var block in textBlocks)
        {
            // The reader owns focus and selection; a child must not clear its range on focus changes.
            block.Focusable = false;
            block.SelectAll();
            var content = block.SelectedText ?? "";
            block.ClearSelection();
            if (text.Length > 0) text.Append(separators.GetValueOrDefault(block, "\n\n"));
            selectionBlocks.Add(new TextRange(block, text.Length, content));
            text.Append(content);
        }
        documentText = text.ToString();
    }

    private void ClearDocumentSelection()
    {
        StopSelecting();
        keyboardLink = null;
        SetSelection(0, 0);
    }

    private void SelectRange(SelectableTextBlock block, int start, int length)
    {
        var range = selectionBlocks.FirstOrDefault(b => b.Block == block);
        if (range is not null) SetSelection(range.Start + start, range.Start + start + length);
    }

    private void SetSelection(int anchor, int end)
    {
        selectionAnchor = Math.Clamp(anchor, 0, documentText.Length);
        selectionEnd = Math.Clamp(end, 0, documentText.Length);
        var first = Math.Min(selectionAnchor, selectionEnd);
        var last = Math.Max(selectionAnchor, selectionEnd);
        foreach (var range in selectionBlocks)
        {
            range.Block.SelectionStart = Math.Clamp(first - range.Start, 0, range.Text.Length);
            range.Block.SelectionEnd = Math.Clamp(last - range.Start, 0, range.Text.Length);
        }
    }

    private (TextRange Range, int Index)? HitText(Point point)
    {
        TextRange? nearest = null;
        var distance = double.MaxValue;
        foreach (var range in selectionBlocks)
        {
            if (range.Block.TranslatePoint(default, this) is not { } origin) continue;
            var bounds = new Rect(origin, range.Block.Bounds.Size);
            var dx = Math.Max(Math.Max(bounds.Left - point.X, point.X - bounds.Right), 0);
            var dy = Math.Max(Math.Max(bounds.Top - point.Y, point.Y - bounds.Bottom), 0);
            var candidate = dx * dx + dy * dy;
            if (candidate >= distance) continue;
            nearest = range;
            distance = candidate;
        }
        if (nearest is null || this.TranslatePoint(point, nearest.Block) is not { } local) return null;
        var index = local.Y < 0 ? 0 : local.Y > nearest.Block.Bounds.Height ? nearest.Text.Length
            : nearest.Block.TextLayout.HitTestPoint(local - new Point(nearest.Block.Padding.Left, nearest.Block.Padding.Top)).TextPosition;
        return (nearest, Math.Clamp(index, 0, nearest.Text.Length));
    }

    private LinkRange? LinkAt(Point point)
    {
        var hit = HitText(point);
        if (hit is not { } text || this.TranslatePoint(point, text.Range.Block) is not { } local) return null;
        // A nearby paragraph's link should not activate when clicking the whitespace around it.
        var layoutPoint = local - new Point(text.Range.Block.Padding.Left, text.Range.Block.Padding.Top);
        return links.FirstOrDefault(link => link.Block == text.Range.Block &&
            link.Block.TextLayout.HitTestTextRange(link.Start, link.Length).Any(rect => rect.Contains(layoutPoint)));
    }

    private void BeginSelection(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse || e.Source is not Visual source ||
            source.GetVisualAncestors().Prepend(source).OfType<ScrollBar>().Any()) return;
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            contextLink = LinkAt(e.GetPosition(this));
            e.Handled = true;
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || HitText(e.GetPosition(this)) is not { } hit) return;
        Focus();
        keyboardLink = null;
        pressPoint = dragPoint = e.GetPosition(this);
        pressedLink = LinkAt(pressPoint);
        hasDragged = false;
        var index = hit.Range.Start + hit.Index;
        if (e.ClickCount >= 3)
            SetSelection(hit.Range.Start, hit.Range.Start + hit.Range.Text.Length);
        else if (e.ClickCount == 2)
        {
            var start = hit.Index;
            var end = hit.Index;
            while (start > 0 && char.IsLetterOrDigit(hit.Range.Text[start - 1])) start--;
            while (end < hit.Range.Text.Length && char.IsLetterOrDigit(hit.Range.Text[end])) end++;
            SetSelection(hit.Range.Start + start, hit.Range.Start + end);
        }
        else SetSelection(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? selectionAnchor : index, index);
        selectionPointer = e.Pointer;
        e.Pointer.Capture(this);
        selectionScrollTimer.Start();
        e.Handled = true;
    }

    private void MoveSelection(object? sender, PointerEventArgs e)
    {
        if (selectionPointer != e.Pointer) return;
        dragPoint = e.GetPosition(this);
        hasDragged |= new Vector(dragPoint.X - pressPoint.X, dragPoint.Y - pressPoint.Y).Length > 3;
        if (hasDragged && HitText(dragPoint) is { } hit) SetSelection(selectionAnchor, hit.Range.Start + hit.Index);
        e.Handled = true;
    }

    private void EndSelection(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            if (e.Pointer.Type != PointerType.Mouse || e.Source is Visual source &&
                source.GetVisualAncestors().Prepend(source).OfType<ScrollBar>().Any()) return;
            e.Handled = true;
            ContextMenu?.Open(this);
            return;
        }
        if (selectionPointer != e.Pointer || e.InitialPressMouseButton != MouseButton.Left) return;
        var link = !hasDragged && selectionAnchor == selectionEnd && LinkAt(e.GetPosition(this)) == pressedLink ? pressedLink : null;
        StopSelecting();
        e.Handled = true;
        if (link is not null) LinkClicked?.Invoke(link.Href);
    }

    private void StopSelecting()
    {
        selectionScrollTimer.Stop();
        var pointer = selectionPointer;
        selectionPointer = null;
        pressedLink = null;
        if (pointer?.Captured == this) pointer.Capture(null);
    }

    private void ScrollSelection()
    {
        if (selectionPointer is null || !hasDragged) return;
        var delta = dragPoint.Y < 20 ? -Math.Clamp(20 - dragPoint.Y, 8, 60)
            : dragPoint.Y > Bounds.Height - 20 ? Math.Clamp(dragPoint.Y - Bounds.Height + 20, 8, 60) : 0;
        if (delta == 0) return;
        scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(scroll.Offset.Y + delta, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        UpdateLayout();
        if (HitText(dragPoint) is { } hit) SetSelection(selectionAnchor, hit.Range.Start + hit.Index);
    }

    private async void SelectionKeyDown(object? sender, KeyEventArgs e)
    {
        var hotkeys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if (hotkeys?.Copy.Any(key => key.Matches(e)) == true)
        {
            e.Handled = true;
            await CopySelectionAsync();
        }
        else if (hotkeys?.SelectAll.Any(key => key.Matches(e)) == true)
        {
            keyboardLink = null;
            SetSelection(0, documentText.Length);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && (e.KeyModifiers & ~KeyModifiers.Shift) == 0 && links.Count > 0)
        {
            // Links remain reachable with Tab/Shift+Tab and Enter after becoming selectable inline text.
            var backwards = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var index = keyboardLink is null ? (backwards ? links.Count - 1 : 0) : links.IndexOf(keyboardLink) + (backwards ? -1 : 1);
            if (index < 0 || index >= links.Count) { keyboardLink = null; return; }
            keyboardLink = links[index];
            SelectRange(keyboardLink.Block, keyboardLink.Start, keyboardLink.Length);
            keyboardLink.Block.BringIntoView();
            AutomationProperties.SetHelpText(this, $"链接：{keyboardLink.Href}；按回车打开");
            e.Handled = true;
        }
        else if ((e.Key is Key.Enter or Key.Space) && keyboardLink is { } link)
        {
            e.Handled = true;
            LinkClicked?.Invoke(link.Href);
        }
    }

    private async Task CopySelectionAsync()
    {
        if (selectionAnchor == selectionEnd) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            {
                CopyStatusChanged?.Invoke("系统剪贴板暂时不可用，请稍后重试。");
                return;
            }
            var start = Math.Min(selectionAnchor, selectionEnd);
            await clipboard.SetTextAsync(documentText[start..Math.Max(selectionAnchor, selectionEnd)]);
            CopyStatusChanged?.Invoke("已复制所选文字。");
        }
        catch (Exception) { CopyStatusChanged?.Invoke("复制失败，所选文字已保留，请重试。"); }
    }
}
