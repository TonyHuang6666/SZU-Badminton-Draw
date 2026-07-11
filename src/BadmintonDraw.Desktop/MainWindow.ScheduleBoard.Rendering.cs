using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using BadmintonDraw.Workflows;

namespace BadmintonDraw.Desktop;

public partial class MainWindow : Window
{
    private Control BuildScheduleBoardWindowContent()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Avalonia.Thickness(16)
        };
        var header = new Border
        {
            Background = ThemeBrush("AppSurfaceMutedBrush", Color.FromRgb(248, 250, 252)),
            BorderBrush = ThemeBrush("AppSoftBorderBrush", Color.FromRgb(226, 232, 240)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10),
            Padding = new Avalonia.Thickness(12)
        };
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titleStack = new StackPanel { Spacing = 2 };
        titleStack.Children.Add(new TextBlock
        {
            Text = "赛程安排窗口",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(40, 16, 78))
        });
        titleStack.Children.Add(_scheduleBoardWindowSummaryText!);
        _scheduleBoardWindowDayTabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        titleStack.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
            Content = _scheduleBoardWindowDayTabs
        });
        headerGrid.Children.Add(titleStack);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        _scheduleBoardWindowDayPickerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        _scheduleBoardWindowDayPickerPanel.Children.Add(new TextBlock
        {
            Text = "比赛日",
            Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(100, 116, 139)),
            VerticalAlignment = VerticalAlignment.Center
        });
        _scheduleBoardWindowDayPickerPanel.Children.Add(_scheduleBoardWindowDayBox!);
        controls.Children.Add(_scheduleBoardWindowDayPickerPanel);
        _scheduleBoardWindowUndoButton = CreateCrossEventWindowButton("撤销", UndoSingleScheduleMove_Click);
        controls.Children.Add(_scheduleBoardWindowUndoButton);
        controls.Children.Add(CreateCrossEventWindowButton("缩小", (_, _) => SetScheduleBoardWindowZoom(_scheduleBoardWindowZoom - ScheduleBoardLayout.ZoomStep)));
        controls.Children.Add(CreateCrossEventWindowButton("100%", (_, _) => SetScheduleBoardWindowZoom(1.0)));
        controls.Children.Add(CreateCrossEventWindowButton("放大", (_, _) => SetScheduleBoardWindowZoom(_scheduleBoardWindowZoom + ScheduleBoardLayout.ZoomStep)));
        UpdateScheduleUndoButtons();
        Grid.SetColumn(controls, 1);
        headerGrid.Children.Add(controls);
        header.Child = headerGrid;
        root.Children.Add(header);

        _scheduleBoardWindowScrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _scheduleBoardWindowGrid
        };

        var boardHost = new Border
        {
            Background = ThemeBrush("AppSurfaceAltBrush", Color.FromRgb(251, 252, 255)),
            BorderBrush = ThemeBrush("AppPanelBorderBrush", Color.FromRgb(216, 224, 236)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10),
            Margin = new Avalonia.Thickness(0, 10, 0, 0),
            Child = _scheduleBoardWindowScrollViewer
        };
        Grid.SetRow(boardHost, 1);
        root.Children.Add(boardHost);
        return root;
    }

    private void ScheduleBoardWindowDayBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshScheduleBoardWindow(_scheduleBoardWindowDayBox?.SelectedItem?.ToString());
    }

    private void SetScheduleBoardWindowZoom(double value)
    {
        _scheduleBoardWindowZoom = ScheduleBoardLayout.ClampWindowZoom(value);
        RefreshScheduleBoardWindow(_scheduleBoardWindowDayBox?.SelectedItem?.ToString());
    }

    private void RefreshScheduleBoardWindow(string? preferredDayLabel = null)
    {
        if (_scheduleBoardWindowDayBox is null
            || _scheduleBoardWindowSummaryText is null
            || _scheduleBoardWindowGrid is null)
        {
            return;
        }

        if (_latestSchedule is null)
        {
            _scheduleBoardWindowSummaryText.Text = "尚未生成赛程。";
            _scheduleBoardWindowDayTabs?.Children.Clear();
            RenderScheduleBoard(_scheduleBoardWindowGrid, null, _scheduleBoardWindowZoom);
            return;
        }

        var boardView = BuildSingleScheduleBoardView();
        var dayLabels = boardView.DayLabels;
        _scheduleBoardWindowDayBox.SelectionChanged -= ScheduleBoardWindowDayBox_SelectionChanged;
        _scheduleBoardWindowDayBox.ItemsSource = dayLabels;
        var selectedDay = !string.IsNullOrWhiteSpace(preferredDayLabel) && dayLabels.Contains(preferredDayLabel)
            ? preferredDayLabel
            : _scheduleBoardWindowDayBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(selectedDay) || !dayLabels.Contains(selectedDay))
        {
            selectedDay = dayLabels.FirstOrDefault();
        }

        _scheduleBoardWindowDayBox.SelectedItem = selectedDay;
        if (_scheduleBoardWindowDayPickerPanel is not null)
        {
            _scheduleBoardWindowDayPickerPanel.IsVisible = dayLabels.Count > ScheduleBoardDayDropdownThreshold;
        }

        _scheduleBoardWindowDayBox.SelectionChanged += ScheduleBoardWindowDayBox_SelectionChanged;
        _scheduleBoardWindowSummaryText.Text = BuildScheduleBoardSummary(_latestSchedule, _scheduleBoardWindowZoom);
        RenderScheduleBoardDayTabs(_scheduleBoardWindowDayTabs, ScheduleBoardKind.SingleEvent, dayLabels, selectedDay);
        RenderScheduleBoardView(_scheduleBoardWindowGrid, boardView, selectedDay, _scheduleBoardWindowZoom);
    }

    private void RenderScheduleBoardDayTabs(
        StackPanel? targetPanel,
        ScheduleBoardKind kind,
        IReadOnlyList<string> dayLabels,
        string? selectedDayLabel)
    {
        if (targetPanel is null)
        {
            return;
        }

        targetPanel.Children.Clear();
        if (dayLabels.Count == 0)
        {
            return;
        }

        targetPanel.Children.Add(new TextBlock
        {
            Text = "点击切换日期，拖到日期可跨日移动：",
            Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(100, 116, 139)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 2, 0)
        });

        foreach (var dayLabel in dayLabels)
        {
            var isSelected = string.Equals(dayLabel, selectedDayLabel, StringComparison.Ordinal);
            var tab = new Border
            {
                Background = isSelected
                    ? ThemeBrush("AppInfoCardBackgroundBrush", Color.FromRgb(236, 246, 255))
                    : ThemeBrush("AppButtonBackgroundBrush", Color.FromRgb(255, 255, 255)),
                BorderBrush = isSelected
                    ? ThemeBrush("AppAccentBrush", Color.FromRgb(15, 95, 159))
                    : ThemeBrush("AppButtonBorderBrush", Color.FromRgb(203, 213, 225)),
                BorderThickness = new Avalonia.Thickness(isSelected ? 2 : 1),
                CornerRadius = new Avalonia.CornerRadius(999),
                Padding = new Avalonia.Thickness(10, 5),
                Tag = new ScheduleBoardDayTabTarget(kind, dayLabel),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = dayLabel,
                    FontWeight = isSelected ? FontWeight.Bold : FontWeight.SemiBold,
                    Foreground = isSelected
                        ? ThemeBrush("AppAccentBrush", Color.FromRgb(15, 95, 159))
                        : ThemeBrush("AppTitleBrush", Color.FromRgb(43, 20, 95))
                }
            };
            ToolTip.SetTip(tab, $"点击切换到 {dayLabel}；拖动比赛卡片到这里可跨日移动。");
            tab.PointerPressed += ScheduleBoardDayTab_PointerPressed;
            DragDrop.SetAllowDrop(tab, true);
            DragDrop.AddDragOverHandler(tab, ScheduleBoardDayTab_DragOver);
            DragDrop.AddDropHandler(tab, ScheduleBoardDayTab_Drop);
            targetPanel.Children.Add(tab);
        }
    }

    private ScheduleBoardView BuildSingleScheduleBoardView()
    {
        return ScheduleWorkflow.BuildScheduleBoardView(
            _latestSchedule ?? EmptySchedulePlan(),
            _progressState?.Results.Keys.ToHashSet(StringComparer.Ordinal));
    }

    private ScheduleBoardView? BuildCrossEventScheduleBoardView()
    {
        return _crossEventScheduleBoard is null
            ? null
            : CrossEventConflictWorkflow.BuildScheduleBoardView(_crossEventScheduleBoard);
    }

    private static SchedulePlan EmptySchedulePlan()
    {
        return new SchedulePlan(
            Array.Empty<ScheduledMatch>(),
            new ScheduleSettings(
                Array.Empty<ScheduleDaySettings>(),
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 3));
    }

    private static string BuildScheduleBoardSummary(SchedulePlan schedule, double zoom)
    {
        var unscheduledText = schedule.UnscheduledMatches.Count > 0
            ? $"，未安排 {schedule.UnscheduledMatches.Count} 场"
            : "";
        var qualityText = BuildScheduleQualityInline(schedule.QualityReport);
        var qualitySuffix = string.IsNullOrWhiteSpace(qualityText)
            ? ""
            : $"；质量：{qualityText}";
        return $"已安排 {schedule.Matches.Count} 场{unscheduledText}，比赛日 {schedule.DayCount} 个；缩放 {Math.Round(zoom * 100)}%{qualitySuffix}。";
    }

    private static string BuildScheduleQualitySentence(ScheduleQualityReport? report)
    {
        var text = BuildScheduleQualityInline(report);
        return string.IsNullOrWhiteSpace(text)
            ? ""
            : $" 质量：{text}。";
    }

    private static string BuildScheduleQualityInline(ScheduleQualityReport? report)
    {
        if (report is null)
        {
            return "";
        }

        var hardText = report.HardConstraintCount == 0
            ? "硬约束 0"
            : $"硬约束 {report.HardConstraintCount}";
        var softText = report.SoftScore > 0
            ? $"，软评分 {report.SoftScore}"
            : "";
        var strategyText = string.IsNullOrWhiteSpace(report.StrategyName)
            ? ""
            : $"，策略 {report.StrategyName}";
        return $"{hardText}{softText}{strategyText}";
    }

    private void RenderScheduleBoard(Grid targetGrid, string? dayLabel, double zoom)
    {
        RenderScheduleBoardView(targetGrid, _latestSchedule is null ? null : BuildSingleScheduleBoardView(), dayLabel, zoom);
    }

    private void RenderScheduleBoardView(
        Grid targetGrid,
        ScheduleBoardView? board,
        string? dayLabel,
        double zoom)
    {
        targetGrid.Children.Clear();
        targetGrid.RowDefinitions.Clear();
        targetGrid.ColumnDefinitions.Clear();
        if (board?.Kind == ScheduleBoardKind.SingleEvent)
        {
            _scheduleBoardWindowMatchCards.Clear();
        }
        else if (board?.Kind == ScheduleBoardKind.CrossEvent && ReferenceEquals(targetGrid, _crossEventBoardWindowGrid))
        {
            _crossEventBoardWindowMatchCards.Clear();
        }

        if (board is null || string.IsNullOrWhiteSpace(dayLabel))
        {
            AddCrossEventEmptyText(targetGrid, "尚未选择比赛日。", zoom);
            return;
        }

        var day = board.FindDay(dayLabel);
        if (day is null)
        {
            AddCrossEventEmptyText(targetGrid, board.EmptyDayText, zoom);
            return;
        }

        targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ScaleCrossEvent(96, zoom)) });
        foreach (var _ in day.Courts)
        {
            targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ScaleCrossEvent(190, zoom)) });
        }

        targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var _ in day.TimeSlots)
        {
            targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        AddCrossEventHeaderCell(targetGrid, "比赛时间", 0, 0, zoom);
        for (var courtIndex = 0; courtIndex < day.Courts.Count; courtIndex++)
        {
            AddCrossEventHeaderCell(targetGrid, day.Courts[courtIndex], 0, courtIndex + 1, zoom);
        }

        for (var slotIndex = 0; slotIndex < day.TimeSlots.Count; slotIndex++)
        {
            var slot = day.TimeSlots[slotIndex];
            AddCrossEventTimeCell(targetGrid, slot, slotIndex + 1, zoom);
            for (var courtIndex = 0; courtIndex < day.Courts.Count; courtIndex++)
            {
                var court = day.Courts[courtIndex];
                AddScheduleBoardDropCell(
                    targetGrid,
                    board.Kind,
                    day.DayLabel,
                    slot,
                    court,
                    slotIndex + 1,
                    courtIndex + 1,
                    board.GetItems(day.DayLabel, court, slot),
                    zoom);
            }
        }
    }

    private void AddScheduleBoardDropCell(
        Grid targetGrid,
        ScheduleBoardKind boardKind,
        string dayLabel,
        TimeOnly slot,
        string court,
        int row,
        int column,
        IReadOnlyList<ScheduleBoardItem> items,
        double zoom)
    {
        var stack = new StackPanel();
        foreach (var item in items)
        {
            var card = CreateScheduleBoardMatchCard(item, boardKind, zoom);
            if (boardKind == ScheduleBoardKind.SingleEvent)
            {
                _scheduleBoardWindowMatchCards[item.FocusKey] = card;
            }
            else if (boardKind == ScheduleBoardKind.CrossEvent && ReferenceEquals(targetGrid, _crossEventBoardWindowGrid))
            {
                _crossEventBoardWindowMatchCards[item.FocusKey] = card;
            }

            stack.Children.Add(card);
        }

        var border = new Border
        {
            Background = ThemeBrush("AppSurfaceBrush", Color.FromRgb(255, 255, 255)),
            BorderBrush = ThemeBrush("AppSoftBorderBrush", Color.FromRgb(226, 232, 240)),
            BorderThickness = new Avalonia.Thickness(0, 0, 1, 1),
            MinHeight = ScaleCrossEvent(72, zoom),
            Padding = new Avalonia.Thickness(ScaleCrossEvent(6, zoom)),
            Tag = new ScheduleBoardDropTarget(boardKind, dayLabel, slot, court),
            Child = stack
        };
        DragDrop.SetAllowDrop(border, true);
        DragDrop.AddDragOverHandler(border, ScheduleBoardCell_DragOver);
        DragDrop.AddDragLeaveHandler(border, ScheduleBoardCell_DragLeave);
        DragDrop.AddDropHandler(border, ScheduleBoardCell_Drop);
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        targetGrid.Children.Add(border);
    }

    private Border CreateScheduleBoardMatchCard(ScheduleBoardItem item, ScheduleBoardKind boardKind, double zoom)
    {
        var borderColor = item.IsBlocking
            ? Color.FromRgb(220, 38, 38)
            : Color.FromRgb(199, 210, 228);
        var backgroundColor = item.IsBlocking
            ? Color.FromRgb(254, 242, 242)
            : item.IsLocked
                ? Color.FromRgb(241, 245, 249)
                : Color.FromRgb(248, 251, 255);
        var card = new Border
        {
            Background = item.IsBlocking
                ? new SolidColorBrush(backgroundColor)
                : item.IsLocked
                    ? ThemeBrush("AppSurfaceMutedBrush", backgroundColor)
                    : ThemeBrush("AppInfoCardBackgroundBrush", backgroundColor),
            BorderBrush = item.IsBlocking
                ? new SolidColorBrush(borderColor)
                : ThemeBrush("AppButtonBorderBrush", borderColor),
            BorderThickness = new Avalonia.Thickness(item.IsBlocking ? 2 : 1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(ScaleCrossEvent(8, zoom)),
            Margin = new Avalonia.Thickness(0, 0, 0, ScaleCrossEvent(6, zoom)),
            Tag = item,
            Cursor = item.IsLocked ? Cursor.Default : new Cursor(StandardCursorType.Hand)
        };
        if (!string.IsNullOrWhiteSpace(item.Tooltip))
        {
            ToolTip.SetTip(card, item.Tooltip);
        }
        else if (!item.IsLocked)
        {
            ToolTip.SetTip(card, "拖拽可调整到空位；拖到窗口顶部日期标签可跨日切换，右键可精确指定比赛日。");
        }

        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = ScaleCrossEventFont(13, zoom),
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(43, 20, 95)),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = item.Subtitle,
            Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(100, 116, 139)),
            FontSize = ScaleCrossEventFont(12, zoom)
        });
        stack.Children.Add(new TextBlock
        {
            Text = item.SideText,
            TextWrapping = TextWrapping.Wrap,
            FontSize = ScaleCrossEventFont(12, zoom)
        });
        if (item.IsBlocking && !string.IsNullOrWhiteSpace(item.DetailText))
        {
            stack.Children.Add(new TextBlock
            {
                Text = item.DetailText,
                Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = ScaleCrossEventFont(12, zoom)
            });
        }

        card.Child = stack;
        if (!item.IsLocked)
        {
            var moveItem = new MenuItem
            {
                Header = "移动到指定比赛日..."
            };
            moveItem.Click += (_, _) => _ = ShowScheduleBoardMoveDialogAsync(boardKind, item);
            card.ContextMenu = new ContextMenu
            {
                Items = { moveItem }
            };
        }

        card.PointerPressed += ScheduleBoardMatchCard_PointerPressed;
        return card;
    }


    private void ZoomOutCrossEventBoard_Click(object? sender, RoutedEventArgs e)
    {
        SetCrossEventBoardZoom(_crossEventBoardZoom - ScheduleBoardLayout.ZoomStep);
    }

    private void ResetCrossEventBoardZoom_Click(object? sender, RoutedEventArgs e)
    {
        SetCrossEventBoardZoom(1.0);
    }

    private void ZoomInCrossEventBoard_Click(object? sender, RoutedEventArgs e)
    {
        SetCrossEventBoardZoom(_crossEventBoardZoom + ScheduleBoardLayout.ZoomStep);
    }

    private void OpenCrossEventBoardWindow_Click(object? sender, RoutedEventArgs e)
    {
        EnsureCrossEventBoardWindowOpen(GetSelectedCrossEventDayLabel());
    }

    private bool EnsureCrossEventBoardWindowOpen(string? preferredDayLabel = null)
    {
        if (_crossEventScheduleBoard is null)
        {
            SetStatus("请先加载多项目赛程。", isError: true);
            return false;
        }

        if (_crossEventBoardWindow is { IsVisible: true })
        {
            RefreshCrossEventBoardWindow(preferredDayLabel);
            _crossEventBoardWindow.Activate();
            return true;
        }

        _crossEventBoardWindowZoom = Math.Max(_crossEventBoardZoom, 0.85);
        _crossEventBoardWindowDayBox = new ComboBox { Width = 180 };
        _crossEventBoardWindowDayBox.SelectionChanged += CrossEventBoardWindowDayBox_SelectionChanged;
        _crossEventBoardWindowSummaryText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            TextWrapping = TextWrapping.Wrap
        };
        _crossEventBoardWindowGrid = new Grid
        {
            Margin = new Avalonia.Thickness(10),
            MinWidth = 980,
            MinHeight = 560
        };

        var root = BuildCrossEventBoardWindowContent();
        _crossEventBoardWindow = new Window
        {
            Title = "多项目赛程窗口",
            Width = 1420,
            Height = 880,
            MinWidth = 980,
            MinHeight = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        _crossEventBoardWindow.Closed += (_, _) =>
        {
            _crossEventBoardWindow = null;
            _crossEventBoardWindowDayBox = null;
            _crossEventBoardWindowSummaryText = null;
            _crossEventBoardWindowGrid = null;
            _crossEventBoardWindowScrollViewer = null;
            _crossEventBoardWindowUndoButton = null;
            _crossEventBoardWindowDayTabs = null;
            _crossEventBoardWindowDayPickerPanel = null;
            _crossEventBoardWindowMatchCards.Clear();
        };
        RefreshCrossEventBoardWindow(preferredDayLabel ?? GetSelectedCrossEventDayLabel());
        _crossEventBoardWindow.Show(this);
        return true;
    }

    private Control BuildCrossEventBoardWindowContent()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Avalonia.Thickness(16)
        };
        var header = new Border
        {
            Background = ThemeBrush("AppSurfaceMutedBrush", Color.FromRgb(248, 250, 252)),
            BorderBrush = ThemeBrush("AppSoftBorderBrush", Color.FromRgb(226, 232, 240)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10),
            Padding = new Avalonia.Thickness(12)
        };
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titleStack = new StackPanel { Spacing = 2 };
        titleStack.Children.Add(new TextBlock
        {
            Text = "多项目赛程窗口",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(40, 16, 78))
        });
        titleStack.Children.Add(_crossEventBoardWindowSummaryText!);
        _crossEventBoardWindowDayTabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        titleStack.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
            Content = _crossEventBoardWindowDayTabs
        });
        headerGrid.Children.Add(titleStack);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        _crossEventBoardWindowDayPickerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        _crossEventBoardWindowDayPickerPanel.Children.Add(new TextBlock
        {
            Text = "比赛日",
            Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(100, 116, 139)),
            VerticalAlignment = VerticalAlignment.Center
        });
        _crossEventBoardWindowDayPickerPanel.Children.Add(_crossEventBoardWindowDayBox!);
        controls.Children.Add(_crossEventBoardWindowDayPickerPanel);
        _crossEventBoardWindowUndoButton = CreateCrossEventWindowButton("撤销", UndoCrossEventScheduleMove_Click);
        controls.Children.Add(_crossEventBoardWindowUndoButton);
        controls.Children.Add(CreateCrossEventWindowButton("缩小", (_, _) => SetCrossEventBoardWindowZoom(_crossEventBoardWindowZoom - ScheduleBoardLayout.ZoomStep)));
        controls.Children.Add(CreateCrossEventWindowButton("100%", (_, _) => SetCrossEventBoardWindowZoom(1.0)));
        controls.Children.Add(CreateCrossEventWindowButton("放大", (_, _) => SetCrossEventBoardWindowZoom(_crossEventBoardWindowZoom + ScheduleBoardLayout.ZoomStep)));
        UpdateCrossEventUndoButtons();
        Grid.SetColumn(controls, 1);
        headerGrid.Children.Add(controls);
        header.Child = headerGrid;
        root.Children.Add(header);

        _crossEventBoardWindowScrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _crossEventBoardWindowGrid
        };

        var boardHost = new Border
        {
            Background = ThemeBrush("AppSurfaceAltBrush", Color.FromRgb(251, 252, 255)),
            BorderBrush = ThemeBrush("AppPanelBorderBrush", Color.FromRgb(216, 224, 236)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10),
            Margin = new Avalonia.Thickness(0, 10, 0, 0),
            Child = _crossEventBoardWindowScrollViewer
        };
        Grid.SetRow(boardHost, 1);
        root.Children.Add(boardHost);
        return root;
    }

    private Button CreateCrossEventWindowButton(string text, EventHandler<RoutedEventArgs> handler)
    {
        var button = new Button
        {
            Content = text,
            Width = 64,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Click += handler;
        return button;
    }

    private void CrossEventBoardWindowDayBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshCrossEventBoardWindow(_crossEventBoardWindowDayBox?.SelectedItem?.ToString());
    }

    private void SetCrossEventBoardWindowZoom(double value)
    {
        _crossEventBoardWindowZoom = ScheduleBoardLayout.ClampWindowZoom(value);
        RefreshCrossEventBoardWindow(_crossEventBoardWindowDayBox?.SelectedItem?.ToString());
    }

    private void RefreshCrossEventBoardWindow(string? preferredDayLabel = null)
    {
        if (_crossEventScheduleBoard is null
            || _crossEventBoardWindowDayBox is null
            || _crossEventBoardWindowSummaryText is null
            || _crossEventBoardWindowGrid is null)
        {
            _crossEventBoardWindowDayTabs?.Children.Clear();
            return;
        }

        var dayLabels = _crossEventScheduleBoard.Days.Select(day => day.DayLabel).ToList();
        _crossEventBoardWindowDayBox.SelectionChanged -= CrossEventBoardWindowDayBox_SelectionChanged;
        _crossEventBoardWindowDayBox.ItemsSource = dayLabels;
        var selectedDay = !string.IsNullOrWhiteSpace(preferredDayLabel) && dayLabels.Contains(preferredDayLabel)
            ? preferredDayLabel
            : _crossEventBoardWindowDayBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(selectedDay) || !dayLabels.Contains(selectedDay))
        {
            selectedDay = dayLabels.FirstOrDefault();
        }

        _crossEventBoardWindowDayBox.SelectedItem = selectedDay;
        if (_crossEventBoardWindowDayPickerPanel is not null)
        {
            _crossEventBoardWindowDayPickerPanel.IsVisible = dayLabels.Count > ScheduleBoardDayDropdownThreshold;
        }

        _crossEventBoardWindowDayBox.SelectionChanged += CrossEventBoardWindowDayBox_SelectionChanged;
        _crossEventBoardWindowSummaryText.Text = BuildCrossEventBoardSummary(_crossEventScheduleBoard, _crossEventBoardWindowZoom);
        RenderScheduleBoardDayTabs(_crossEventBoardWindowDayTabs, ScheduleBoardKind.CrossEvent, dayLabels, selectedDay);
        RenderCrossEventScheduleBoard(_crossEventBoardWindowGrid, selectedDay, _crossEventBoardWindowZoom);
    }

    private void SetCrossEventBoardZoom(double value)
    {
        var next = ScheduleBoardLayout.ClampMainZoom(value);
        if (Math.Abs(next - _crossEventBoardZoom) < 0.001)
        {
            return;
        }

        _crossEventBoardZoom = next;
        RefreshCrossEventScheduleBoard(GetSelectedCrossEventDayLabel());
    }

    private static IReadOnlyList<CrossEventPlayerSummaryRow> BuildCrossEventPlayerSummaryRows(
        IReadOnlyList<CrossEventPlayerMultiEntry> entries,
        CrossEventPlayerSortMode sortMode)
    {
        var orderedEntries = SortCrossEventPlayerEntries(entries, sortMode);
        return orderedEntries
            .Select((entry, index) => new CrossEventPlayerSummaryRow(
                entry,
                index + 1,
                $"{index + 1}. {entry.PlayerName} · {entry.EventCount} 项 · {entry.MatchCount} 场",
                $"{string.Join("、", entry.EventNames)}\n未完成 {entry.PendingMatchCount} 场；严重 {entry.SevereIssueCount} 条，警告 {entry.WarningIssueCount} 条；最短休息 {FormatRestMinutes(entry.ShortestRestMinutes)}"))
            .ToList();
    }

    private static IEnumerable<CrossEventPlayerMultiEntry> SortCrossEventPlayerEntries(
        IEnumerable<CrossEventPlayerMultiEntry> entries,
        CrossEventPlayerSortMode sortMode)
    {
        return sortMode switch
        {
            CrossEventPlayerSortMode.RestAscending => entries
                .OrderBy(entry => entry.ShortestRestMinutes.HasValue ? 0 : 1)
                .ThenBy(entry => entry.ShortestRestMinutes ?? int.MaxValue)
                .ThenBy(entry => entry.PlayerName, StringComparer.Ordinal),
            CrossEventPlayerSortMode.RestDescending => entries
                .OrderBy(entry => entry.ShortestRestMinutes.HasValue ? 0 : 1)
                .ThenByDescending(entry => entry.ShortestRestMinutes ?? int.MinValue)
                .ThenBy(entry => entry.PlayerName, StringComparer.Ordinal),
            _ => entries
        };
    }

    private static string FormatRestMinutes(int? minutes)
    {
        return minutes.HasValue ? $"{minutes.Value} 分钟" : "-";
    }

    private double ScaleCrossEvent(double value)
    {
        return ScaleCrossEvent(value, _crossEventBoardZoom);
    }

    private double ScaleCrossEventFont(double value)
    {
        return ScaleCrossEventFont(value, _crossEventBoardZoom);
    }

    private static double ScaleCrossEvent(double value, double zoom)
    {
        return Math.Round(ScheduleBoardLayout.Scale(value, zoom));
    }

    private static double ScaleCrossEventFont(double value, double zoom)
    {
        return Math.Round(ScheduleBoardLayout.ScaleFont(value, zoom), 1);
    }
}
