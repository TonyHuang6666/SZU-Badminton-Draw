using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Scheduling;

namespace BadmintonDraw.Desktop.Controls;

public sealed record BoardHoverFeedback(bool CanApply, string Message);

/// <summary>Rendering and pointer translation only: no persistence, scheduler, workflow or undo state.</summary>
public partial class ScheduleBoardControl : UserControl
{
    public static readonly StyledProperty<WorkspaceScheduleBoard?> BoardProperty = AvaloniaProperty.Register<ScheduleBoardControl, WorkspaceScheduleBoard?>(nameof(Board));
    public static readonly StyledProperty<string?> SelectedDayProperty = AvaloniaProperty.Register<ScheduleBoardControl, string?>(nameof(SelectedDay), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<ScheduleBoardControl, double>(nameof(Zoom), 1, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> CanEditProperty = AvaloniaProperty.Register<ScheduleBoardControl, bool>(nameof(CanEdit));
    public WorkspaceScheduleBoard? Board { get => GetValue(BoardProperty); set => SetValue(BoardProperty, value); }
    public string? SelectedDay { get => GetValue(SelectedDayProperty); set => SetValue(SelectedDayProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public bool CanEdit { get => GetValue(CanEditProperty); set => SetValue(CanEditProperty, value); }
    public event Action<WorkspaceMatchKey>? MatchSelected;
    public event Action<WorkspaceMatchKey>? ManualMoveRequested;
    public event Action<WorkspaceBoardMoveIntent>? MoveRequested;
    public Func<WorkspaceBoardMoveIntent, Task<BoardHoverFeedback>>? PreviewHoverAsync { get; set; }
    private readonly Dictionary<WorkspaceMatchKey, Border> cards = [];
    private readonly Dictionary<WorkspaceBoardMoveIntent, BoardHoverFeedback> hoverCache = [];
    private WorkspaceBoardMoveIntent? hoverIntent;
    private Border? hoverCell, dragSource;
    private CancellationTokenSource? hoverCancellation;
    private readonly SemaphoreSlim hoverGate = new(1, 1);
    private long epoch;
    private bool ready;
    public ScheduleBoardControl()
    {
        InitializeComponent(); ready = true;
        AttachedToVisualTree += (_, _) => Render();
        DetachedFromVisualTree += (_, _) => { ++epoch; ClearDragState(); cards.Clear(); PreviewHoverAsync = null; };
        ActualThemeVariantChanged += (_, _) => Render();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!ready) return;
        if (change.Property == BoardProperty) { ++epoch; hoverCache.Clear(); ClearDragState(); Render(); }
        else if (change.Property == SelectedDayProperty || change.Property == ZoomProperty || change.Property == CanEditProperty) Render();
    }
    private void ZoomOut(object? sender, RoutedEventArgs e) => SetCurrentValue(ZoomProperty, WorkspaceBoardInteraction.ClampZoom(Zoom - .15));
    private void ZoomIn(object? sender, RoutedEventArgs e) => SetCurrentValue(ZoomProperty, WorkspaceBoardInteraction.ClampZoom(Zoom + .15));
    private void ResetZoom(object? sender, RoutedEventArgs e) => SetCurrentValue(ZoomProperty, 1);
    private IBrush Brush(string resource) => this.TryFindResource(resource, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;
    private double Scale(double value) => value * WorkspaceBoardInteraction.ClampZoom(Zoom);
    private TextBlock Text(string text, bool bold = false) => new() { Text = text, FontSize = Math.Max(10, Scale(12)), FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        TextWrapping = TextWrapping.Wrap, Foreground = Brush(bold ? "AppTitleBrush" : "AppTextBrush") };
    private void Render()
    {
        if (!ready) return;
        ClearHover(); cards.Clear(); DayTabs.Children.Clear(); BoardGrid.Children.Clear(); BoardGrid.ColumnDefinitions.Clear(); BoardGrid.RowDefinitions.Clear();
        ZoomLabel.Content = $"{WorkspaceBoardInteraction.ClampZoom(Zoom):P0}";
        if (Board is not { } board) return;
        var day = board.Days.FirstOrDefault(d => d.DayLabel == SelectedDay) ?? board.Days.FirstOrDefault();
        if (day is null) return;
        foreach (var entry in board.Days)
        {
            var tab = new Button { Content = entry.DayLabel, Tag = entry.DayLabel, FontWeight = day.DayLabel == entry.DayLabel ? FontWeight.Bold : FontWeight.Normal };
            tab.Click += (_, _) => SetCurrentValue(SelectedDayProperty, entry.DayLabel);
            DragDrop.SetAllowDrop(tab, true); DragDrop.AddDragOverHandler(tab, DayDragOver); DragDrop.AddDropHandler(tab, DayDrop); DayTabs.Children.Add(tab);
        }
        BoardGrid.ColumnDefinitions.Add(new() { Width = new GridLength(Scale(98)) });
        foreach (var _ in day.Courts) BoardGrid.ColumnDefinitions.Add(new() { Width = new GridLength(Scale(215)) });
        BoardGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Header("开始时间", 0, 0);
        for (var c = 0; c < day.Courts.Count; c++) Header(day.Courts[c], 0, c + 1);
        var lookup = board.Cards.Where(c => c.Placement.DayLabel == day.DayLabel).ToLookup(c => (c.Placement.Court, c.Placement.StartTime));
        for (var i = 0; i < day.TimeSlots.Count; i++)
        {
            var time = day.TimeSlots[i]; BoardGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Header(time.Second == 0 && time.Millisecond == 0 ? time.ToString("HH:mm") : time.ToString("HH:mm:ss.fff"), i + 1, 0);
            for (var c = 0; c < day.Courts.Count; c++)
            {
                var court = day.Courts[c]; var stack = new StackPanel { Spacing = 4 };
                foreach (var item in lookup[(court, time)]) { var card = Card(item); cards[item.Key] = card; stack.Children.Add(card); }
                var cell = new Border { Child = stack, Tag = new BoardCell(day.DayLabel, time, court), MinHeight = Scale(64), Padding = new Thickness(Scale(5)),
                    Background = Brush("AppSurfaceBrush"), BorderBrush = Brush("AppSoftBorderBrush"), BorderThickness = new Thickness(0, 0, 1, 1) };
                DragDrop.SetAllowDrop(cell, true); DragDrop.AddDragOverHandler(cell, CellDragOver); DragDrop.AddDragLeaveHandler(cell, CellDragLeave); DragDrop.AddDropHandler(cell, CellDrop);
                Grid.SetRow(cell, i + 1); Grid.SetColumn(cell, c + 1); BoardGrid.Children.Add(cell);
            }
        }
    }
    private void Header(string label, int row, int column)
    {
        var cell = new Border { Child = Text(label, true), Padding = new Thickness(Scale(8)), Background = Brush("AppSurfaceMutedBrush"), BorderBrush = Brush("AppSoftBorderBrush"), BorderThickness = new Thickness(0, 0, 1, 1) };
        Grid.SetRow(cell, row); Grid.SetColumn(cell, column); BoardGrid.Children.Add(cell);
    }
    private Border Card(WorkspaceBoardCard item)
    {
        var stack = new StackPanel { Spacing = 4 }; stack.Children.Add(Text(item.Title, true)); stack.Children.Add(Text(item.Phase + (item.IsLocked ? " · 已完成 / 锁定" : "")));
        stack.Children.Add(Text($"{item.Placement.StartTime:HH:mm:ss}–{item.Placement.EndTime:HH:mm:ss}")); stack.Children.Add(Text(item.Sides));
        var card = new Border { Child = stack, Tag = item, Focusable = true, CornerRadius = new CornerRadius(7), Padding = new Thickness(Scale(8)), BorderThickness = new Thickness(1),
            Background = Brush(item.IsLocked ? "AppSurfaceMutedBrush" : "AppInfoCardBackgroundBrush"), BorderBrush = Brush("AppButtonBorderBrush") };
        AutomationProperties.SetName(card, item.Title + " " + item.Position + (item.IsLocked ? " 已完成，不能移动" : " 按回车手动移动"));
        ToolTip.SetTip(card, item.IsLocked ? "已有赛果，位置锁定。" : "拖动到目标格，或按回车 / 右键手动移动。");
        card.PointerPressed += CardPointerPressed;
        card.KeyDown += (_, e) => { if (e.Key == Key.Enter) { MatchSelected?.Invoke(item.Key); if (CanEdit && !item.IsLocked) ManualMoveRequested?.Invoke(item.Key); e.Handled = true; } };
        var menu = new MenuItem { Header = "移动到指定时间 / 场地", IsEnabled = CanEdit && !item.IsLocked };
        menu.Click += (_, _) => { MatchSelected?.Invoke(item.Key); ManualMoveRequested?.Invoke(item.Key); };
        card.ContextMenu = new() { Items = { menu } };
        return card;
    }
    public void FocusMatch(WorkspaceMatchKey key)
    {
        if (Board?.Cards.FirstOrDefault(c => c.Key == key) is not { } item) return;
        SetCurrentValue(SelectedDayProperty, item.Placement.DayLabel); var source = Board; var token = epoch;
        Dispatcher.UIThread.Post(() =>
        {
            if (token != epoch || !ReferenceEquals(source, Board) || !cards.TryGetValue(key, out var card)) return;
            card.BringIntoView(); card.Focus(); card.BorderBrush = Brush("AppAccentBrush"); card.BorderThickness = new Thickness(3);
            DispatcherTimer.RunOnce(() => { if (token == epoch && ReferenceEquals(source, Board) && cards.GetValueOrDefault(key) == card) { card.BorderThickness = new Thickness(1); card.BorderBrush = Brush("AppButtonBorderBrush"); } }, TimeSpan.FromSeconds(1.5));
        }, DispatcherPriority.Loaded);
    }
    private sealed record BoardCell(string DayLabel, TimeOnly Time, string Court);
}
