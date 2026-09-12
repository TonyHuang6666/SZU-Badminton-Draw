using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Scheduling;

namespace BadmintonDraw.Desktop.Controls;

public partial class ScheduleBoardControl
{
    private async void CardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: WorkspaceBoardCard item } card) return;
        MatchSelected?.Invoke(item.Key); card.Focus();
        if (!CanEdit || item.IsLocked || Board is not { } source || !e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
        hoverCache.Clear(); ClearHover(); dragSource = card; card.Opacity = .45;
        var data = new DataTransfer(); data.Add(DataTransferItem.CreateText(WorkspaceBoardDrag.Encode(source, item.Key)));
        try { await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move); }
        finally { ClearDragState(); }
    }
    private bool TryKey(DragEventArgs e, out WorkspaceMatchKey key)
    {
        key = default; return CanEdit && Board is { } board && WorkspaceBoardDrag.TryParse(e.DataTransfer.TryGetText(), board, out key);
    }
    private void DayDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Button { Tag: string day } || !TryKey(e, out _)) { e.DragEffects = DragDropEffects.None; e.Handled = true; return; }
        if (SelectedDay != day)
        {
            var token = epoch; var source = Board;
            Dispatcher.UIThread.Post(() => { if (token == epoch && ReferenceEquals(source, Board)) SetCurrentValue(SelectedDayProperty, day); }, DispatcherPriority.Input);
        }
        Feedback.Text = $"切换到 {day}；继续拖到具体时间和场地后松开。"; e.DragEffects = DragDropEffects.Move; e.Handled = true;
    }
    private void DayDrop(object? sender, DragEventArgs e)
    {
        DayDragOver(sender, e);
        if (TryKey(e, out _)) Feedback.Text = "尚未移动比赛。请再次拖到具体时间 / 场地格，或使用手动移动。";
    }
    private void CellDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Border { Tag: BoardCell target } cell || !TryKey(e, out var key)) { e.DragEffects = DragDropEffects.None; e.Handled = true; return; }
        var intent = new WorkspaceBoardMoveIntent(key, target.DayLabel, target.Time, target.Court);
        if (hoverIntent != intent || !ReferenceEquals(hoverCell, cell))
        {
            ClearHover(); hoverIntent = intent; hoverCell = cell;
            if (hoverCache.TryGetValue(intent, out var feedback)) ApplyFeedback(cell, feedback);
            else { Feedback.Text = "正在检查目标位置…松开后仍需预览确认。"; QueueHover(intent, cell); }
        }
        // Drop is only an intent. A blocked target opens read-only reasons/cascade preview; it never saves directly.
        e.DragEffects = DragDropEffects.Move;
        var point = e.GetPosition(BoardScroll);
        BoardScroll.Offset = new Vector(Math.Clamp(BoardScroll.Offset.X + WorkspaceBoardInteraction.AutoScrollDelta(point.X, BoardScroll.Bounds.Width), 0, Math.Max(0, BoardScroll.Extent.Width - BoardScroll.Viewport.Width)),
            Math.Clamp(BoardScroll.Offset.Y + WorkspaceBoardInteraction.AutoScrollDelta(point.Y, BoardScroll.Bounds.Height), 0, Math.Max(0, BoardScroll.Extent.Height - BoardScroll.Viewport.Height)));
        e.Handled = true;
    }
    private async void QueueHover(WorkspaceBoardMoveIntent intent, Border cell)
    {
        hoverCancellation = new(); var cancellation = hoverCancellation; var cancellationToken = cancellation.Token; var token = epoch; var source = Board;
        try
        {
            await Task.Delay(150, cancellationToken);
            if (PreviewHoverAsync is not { } preview || cancellation.IsCancellationRequested) return;
            await hoverGate.WaitAsync(cancellationToken);
            BoardHoverFeedback result;
            try { cancellationToken.ThrowIfCancellationRequested(); result = await preview(intent); }
            finally { hoverGate.Release(); }
            if (cancellation.IsCancellationRequested || token != epoch || !ReferenceEquals(source, Board)) return;
            hoverCache[intent] = result;
            if (hoverIntent == intent && ReferenceEquals(hoverCell, cell)) ApplyFeedback(cell, result);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested && token == epoch && ReferenceEquals(source, Board)) ApplyFeedback(cell, new(false, "检查失败：" + exception.Message));
        }
    }
    private void ApplyFeedback(Border cell, BoardHoverFeedback result)
    {
        cell.Background = Brush(result.CanApply ? "AppSuccessCardBackgroundBrush" : "AppErrorCardBackgroundBrush");
        cell.BorderBrush = Brush(result.CanApply ? "AppSuccessCardBorderBrush" : "AppErrorCardBorderBrush"); cell.BorderThickness = new Thickness(3);
        ToolTip.SetTip(cell, result.Message); Feedback.Text = result.Message;
    }
    private void CellDragLeave(object? sender, DragEventArgs e) { if (ReferenceEquals(sender, hoverCell)) ClearHover(); }
    private void CellDrop(object? sender, DragEventArgs e)
    {
        var valid = TryKey(e, out var key); ClearHover();
        if (valid && sender is Border { Tag: BoardCell target }) MoveRequested?.Invoke(new(key, target.DayLabel, target.Time, target.Court));
        e.Handled = true;
    }
    private void ClearHover()
    {
        hoverCancellation?.Cancel(); hoverCancellation?.Dispose(); hoverCancellation = null;
        if (hoverCell is { } cell) { cell.Background = Brush("AppSurfaceBrush"); cell.BorderBrush = Brush("AppSoftBorderBrush"); cell.BorderThickness = new Thickness(0, 0, 1, 1); ToolTip.SetTip(cell, null); }
        hoverCell = null; hoverIntent = null;
    }
    private void ClearDragState()
    {
        ClearHover(); hoverCache.Clear();
        if (dragSource is { } card) card.Opacity = 1;
        dragSource = null;
    }
}
