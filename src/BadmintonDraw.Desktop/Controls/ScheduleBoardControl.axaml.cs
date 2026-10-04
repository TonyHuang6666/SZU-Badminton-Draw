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
    private const string DragInstructions = "拖到日期标签切换比赛日，再到具体时间 / 场地格松开。合法普通拖动再次校验后自动保存；手动和连锁移动需查看调整方案并确认。";
    public static readonly StyledProperty<WorkspaceScheduleBoard?> BoardProperty = AvaloniaProperty.Register<ScheduleBoardControl, WorkspaceScheduleBoard?>(nameof(Board));
    public static readonly StyledProperty<string?> SelectedDayProperty = AvaloniaProperty.Register<ScheduleBoardControl, string?>(nameof(SelectedDay), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<ScheduleBoardControl, double>(nameof(Zoom), 1, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> CanEditProperty = AvaloniaProperty.Register<ScheduleBoardControl, bool>(nameof(CanEdit));
    public static readonly StyledProperty<bool> IsCompactProperty = AvaloniaProperty.Register<ScheduleBoardControl, bool>(nameof(IsCompact), true, defaultBindingMode: BindingMode.TwoWay);
    public WorkspaceScheduleBoard? Board { get => GetValue(BoardProperty); set => SetValue(BoardProperty, value); }
    public string? SelectedDay { get => GetValue(SelectedDayProperty); set => SetValue(SelectedDayProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public bool CanEdit { get => GetValue(CanEditProperty); set => SetValue(CanEditProperty, value); }
    public bool IsCompact { get => GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    public event Action<WorkspaceMatchKey>? MatchSelected;
    public event Action<WorkspaceMatchKey>? ManualMoveRequested;
    public event Action<WorkspaceBoardMoveIntent>? MoveRequested;
    public Func<WorkspaceBoardMoveIntent, Task<BoardHoverFeedback>>? PreviewHoverAsync { get; set; }
    private readonly Dictionary<WorkspaceMatchKey, Border> cards = [];
    private readonly Dictionary<WorkspaceMatchKey, CardAppearance> cardAppearances = [];
    private WorkspaceMatchKey? locatedMatch;
    private Guid? locatedWorkspace;
    private readonly Dictionary<WorkspaceBoardMoveIntent, BoardHoverFeedback> hoverCache = [];
    private WorkspaceBoardMoveIntent? hoverIntent;
    private Border? hoverCell, dragSource;
    private CancellationTokenSource? hoverCancellation;
    private readonly SemaphoreSlim hoverGate = new(1, 1);
    private long epoch;
    private bool ready;
    public ScheduleBoardControl()
    {
        InitializeComponent(); ready = true; Feedback.Text = DragInstructions;
        BoardScroll.PropertyChanged += (_, change) =>
        {
            if (change.Property == ScrollViewer.OffsetProperty || change.Property == ScrollViewer.ViewportProperty) SyncFrozenPanes();
        };
        AttachedToVisualTree += (_, _) => Render();
        DetachedFromVisualTree += (_, _) => { ++epoch; ClearDragState(); cards.Clear(); cardAppearances.Clear(); PreviewHoverAsync = null; };
        ActualThemeVariantChanged += (_, _) => Render();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!ready) return;
        if (change.Property == BoardProperty)
        {
            ++epoch; hoverCache.Clear(); ClearDragState();
            if (Board is not { } board || board.WorkspaceId != locatedWorkspace || !board.Cards.Any(c => c.Key == locatedMatch))
                locatedMatch = null;
            Render();
        }
        else if (change.Property == SelectedDayProperty || change.Property == ZoomProperty || change.Property == CanEditProperty || change.Property == IsCompactProperty) Render();
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
        ClearHover(); cards.Clear(); cardAppearances.Clear(); DayTabs.Children.Clear();
        foreach (var grid in new[] { BoardGrid, CourtHeaderGrid, TimeAxisGrid }) { grid.Children.Clear(); grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear(); }
        ZoomLabel.Content = $"{WorkspaceBoardInteraction.ClampZoom(Zoom):P0}";
        CompactToggle.Content = IsCompact ? "紧凑" : "详细";
        UpdateLocationSummary();
        if (Board is not { } board) return;
        var day = board.Days.FirstOrDefault(d => d.DayLabel == SelectedDay) ?? board.Days.FirstOrDefault();
        if (day is null) return;
        foreach (var entry in board.Days)
        {
            var tab = new Button { Content = entry.DayLabel, Tag = entry.DayLabel, Padding = new Thickness(10, 5), FontWeight = day.DayLabel == entry.DayLabel ? FontWeight.Bold : FontWeight.Normal };
            if (day.DayLabel == entry.DayLabel) { tab.Background = Brush("AppSurfaceMutedBrush"); tab.BorderBrush = Brush("AppAccentBrush"); }
            tab.Click += (_, _) => SetCurrentValue(SelectedDayProperty, entry.DayLabel);
            DragDrop.SetAllowDrop(tab, true); DragDrop.AddDragOverHandler(tab, DayDragOver); DragDrop.AddDropHandler(tab, DayDrop); DayTabs.Children.Add(tab);
        }
        var timeWidth = Scale(day.TimeSlots.Any(t => t.Ticks % TimeSpan.TicksPerSecond != 0) ? 110 : day.TimeSlots.Any(t => t.Second != 0) ? 80 : 62);
        TimeAxisCorner.Width = TimeAxisGrid.Width = timeWidth;
        var courtWidth = new GridLength(Scale(IsCompact ? 204 : 250));
        foreach (var _ in day.Courts)
        {
            BoardGrid.ColumnDefinitions.Add(new() { Width = courtWidth });
            CourtHeaderGrid.ColumnDefinitions.Add(new() { Width = courtWidth });
        }
        for (var c = 0; c < day.Courts.Count; c++) Header(CourtHeaderGrid, day.Courts[c], 0, c);
        var lookup = board.Cards.Where(c => c.Placement.DayLabel == day.DayLabel).ToLookup(c => (c.Placement.Court, c.Placement.StartTime));
        var projects = board.Cards.GroupBy(c => c.Key.ProjectId).OrderBy(g => g.First().ProjectName, StringComparer.Ordinal).ThenBy(g => g.Key)
            .Select((group, index) => (group.Key, Palette: new[] { "Info", "Success", "Warning", "Purple" }[index % 4])).ToDictionary(p => p.Key, p => p.Palette);
        for (var i = 0; i < day.TimeSlots.Count; i++)
        {
            var time = day.TimeSlots[i];
            BoardGrid.RowDefinitions.Add(new() { Height = GridLength.Auto, SharedSizeGroup = "TimeSlot" + i });
            TimeAxisGrid.RowDefinitions.Add(new() { Height = GridLength.Auto, SharedSizeGroup = "TimeSlot" + i });
            Header(TimeAxisGrid, WorkspaceBoardTime.Format(time), i, 0);
            for (var c = 0; c < day.Courts.Count; c++)
            {
                var court = day.Courts[c]; var stack = new StackPanel { Spacing = Scale(3) };
                foreach (var item in lookup[(court, time)]) { var card = Card(item, projects[item.Key.ProjectId]); cards[item.Key] = card; stack.Children.Add(card); }
                var cell = new Border { Child = stack, Tag = new BoardCell(day.DayLabel, time, court), MinHeight = Scale(IsCompact ? 32 : 48), Padding = new Thickness(Scale(IsCompact ? 3 : 5)),
                    Background = Brush("AppSurfaceBrush"), BorderBrush = Brush("AppSoftBorderBrush"), BorderThickness = new Thickness(0, 0, 1, 1) };
                DragDrop.SetAllowDrop(cell, true); DragDrop.AddDragOverHandler(cell, CellDragOver); DragDrop.AddDragLeaveHandler(cell, CellDragLeave); DragDrop.AddDropHandler(cell, CellDrop);
                Grid.SetRow(cell, i); Grid.SetColumn(cell, c); BoardGrid.Children.Add(cell);
            }
        }
    }
    private void Header(Grid grid, string label, int row, int column)
    {
        var text = Text(label, true); text.VerticalAlignment = VerticalAlignment.Center;
        var cell = new Border { Child = text, Padding = new Thickness(Scale(6)), Background = Brush("AppSurfaceMutedBrush"), BorderBrush = Brush("AppSoftBorderBrush"), BorderThickness = new Thickness(0, 0, 1, 1) };
        Grid.SetRow(cell, row); Grid.SetColumn(cell, column); grid.Children.Add(cell);
    }
    private Border Card(WorkspaceBoardCard item, string palette)
    {
        var stack = new StackPanel { Spacing = Scale(IsCompact ? 2 : 4) };
        var title = Text(item.Title, true); title.Foreground = Brush($"App{palette}TextBrush"); stack.Children.Add(title);
        if (!IsCompact && !string.IsNullOrWhiteSpace(item.Phase) && !item.MatchName.Contains(item.Phase, StringComparison.Ordinal)) stack.Children.Add(Text(item.Phase));
        var timeRow = new Grid { MinHeight = Scale(18) };
        timeRow.ColumnDefinitions.Add(new() { Width = GridLength.Star });
        timeRow.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Max(50, Scale(60))) });
        timeRow.Children.Add(Text($"{WorkspaceBoardTime.Format(item.Placement.StartTime)}–{WorkspaceBoardTime.Format(item.Placement.EndTime)}" + (item.IsLocked ? " · 已锁定" : "")));
        var badgeText = new TextBlock { Text = "当前定位", FontSize = Math.Max(10, Scale(10)), FontWeight = FontWeight.SemiBold, Foreground = Brush("AppAccentTextBrush"), IsVisible = false };
        var badge = new Border { Child = badgeText, Background = Brush("AppAccentBrush"), CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsVisible = false };
        Grid.SetColumn(badge, 1); timeRow.Children.Add(badge); stack.Children.Add(timeRow);
        stack.Children.Add(Text(IsCompact ? item.Sides.Replace("\nVS\n", "  vs  ", StringComparison.Ordinal) : item.Sides));
        var card = new Border { Child = stack, Tag = item, Focusable = true, CornerRadius = new CornerRadius(5), Padding = new Thickness(Scale(IsCompact ? 6 : 8)), BorderThickness = new Thickness(3, 1, 1, 1),
            Background = Brush($"App{palette}CardBackgroundBrush"), BorderBrush = Brush($"App{palette}CardBorderBrush") };
        cardAppearances[item.Key] = new(card.Background, card.BorderBrush, badge);
        ApplyLocationAppearance(item.Key, card);
        AutomationProperties.SetName(card, item.Title + " " + item.Position + (item.IsLocked ? " 已完成，不能移动" : " 按回车手动移动"));
        ToolTip.SetTip(card, $"{item.Title}\n{item.Phase}\n{item.Position}\n{item.Sides}\n" + (item.IsLocked ? "已有赛果，位置锁定。" : "拖动到目标格，或按回车 / 右键手动移动。"));
        card.PointerPressed += CardPointerPressed;
        card.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SelectMatch(item.Key); if (CanEdit && !item.IsLocked) ManualMoveRequested?.Invoke(item.Key); e.Handled = true; } };
        var menu = new MenuItem { Header = "移动到指定时间 / 场地", IsEnabled = CanEdit && !item.IsLocked };
        menu.Click += (_, _) => { SelectMatch(item.Key); ManualMoveRequested?.Invoke(item.Key); };
        card.ContextMenu = new() { Items = { menu } };
        return card;
    }
    public void FocusMatch(WorkspaceMatchKey key)
    {
        if (Board?.Cards.FirstOrDefault(c => c.Key == key) is not { } item) return;
        SetLocatedMatch(key);
        SetCurrentValue(SelectedDayProperty, item.Placement.DayLabel); var source = Board; var token = epoch;
        Dispatcher.UIThread.Post(() =>
        {
            if (token != epoch || !ReferenceEquals(source, Board) || locatedMatch != key || !cards.TryGetValue(key, out var card)) return;
            // Focus already requests scrolling; doing both before arrange applies the same offset twice.
            if (card.IsFocused || !card.Focus()) card.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
    public void ClearMatchLocation()
    {
        locatedMatch = null; locatedWorkspace = null;
        foreach (var (key, card) in cards) ApplyLocationAppearance(key, card);
        UpdateLocationSummary();
    }
    private void CancelMatchLocation(object? sender, RoutedEventArgs e) => ClearMatchLocation();
    private void SelectMatch(WorkspaceMatchKey key)
    {
        SetLocatedMatch(key); MatchSelected?.Invoke(key);
    }
    private void SetLocatedMatch(WorkspaceMatchKey key)
    {
        if (Board is not { } board || !board.Cards.Any(c => c.Key == key)) return;
        locatedMatch = key; locatedWorkspace = board.WorkspaceId;
        foreach (var (cardKey, card) in cards) ApplyLocationAppearance(cardKey, card);
        UpdateLocationSummary();
    }
    private void UpdateLocationSummary()
    {
        var item = Board?.Cards.FirstOrDefault(c => c.Key == locatedMatch);
        LocationSummary.IsVisible = item is not null;
        LocationInfo.Text = item is null ? "" : $"当前定位 · {item.Title} · {item.Position}";
        ToolTip.SetTip(LocationInfo, LocationInfo.Text);
        Grid.SetRow(FeedbackScroll, item is null ? 0 : 1);
        Grid.SetRowSpan(FeedbackScroll, item is null ? 2 : 1);
    }
    private void ApplyLocationAppearance(WorkspaceMatchKey key, Border card)
    {
        var appearance = cardAppearances[key]; var located = key == locatedMatch;
        appearance.Badge.IsVisible = located;
        ((TextBlock)appearance.Badge.Child!).IsVisible = located;
        card.BorderBrush = located ? Brush("AppAccentBrush") : appearance.Border;
        card.Background = located ? LocationBackground() : appearance.Background;
        // An inset shadow strengthens the outline without changing measure or scroll geometry.
        card.BoxShadow = located && Brush("AppAccentBrush") is ISolidColorBrush accent
            ? new BoxShadows(new BoxShadow { Color = accent.Color, Spread = 2, IsInset = true }) : default;
    }
    private IBrush LocationBackground()
    {
        if (Brush("AppAccentBrush") is not ISolidColorBrush accent || Brush("AppSurfaceBrush") is not ISolidColorBrush surface)
            return Brush("AppSurfaceMutedBrush");
        static byte Blend(byte accent, byte surface) => (byte)Math.Round(accent * .18 + surface * .82);
        return new SolidColorBrush(Color.FromRgb(Blend(accent.Color.R, surface.Color.R), Blend(accent.Color.G, surface.Color.G), Blend(accent.Color.B, surface.Color.B)));
    }
    private sealed record CardAppearance(IBrush? Background, IBrush? Border, Border Badge);
    private sealed record BoardCell(string DayLabel, TimeOnly Time, string Court);
}
