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
    private async void ScheduleBoardMatchCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: ScheduleBoardItem item } || item.IsLocked)
        {
            return;
        }

        if (!e.GetCurrentPoint((Control)sender).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _scheduleBoardMoveValidationCache.Clear();
        _scheduleBoardDragSwitchDayLabel = null;
        ClearScheduleBoardDragFeedback();
        var sourceCard = (Border)sender;
        _scheduleBoardDragSourceCard = sourceCard;
        _scheduleBoardDragSourceOriginalOpacity = sourceCard.Opacity;
        sourceCard.Opacity = ScheduleBoardDragSourceOpacity;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(item.DragPayload));
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            _scheduleBoardDragSwitchDayLabel = null;
            _scheduleBoardMoveValidationCache.Clear();
            ClearScheduleBoardDragFeedback();
            if (_scheduleBoardDragSourceCard is not null)
            {
                _scheduleBoardDragSourceCard.Opacity = _scheduleBoardDragSourceOriginalOpacity;
                _scheduleBoardDragSourceCard = null;
                _scheduleBoardDragSourceOriginalOpacity = 1.0;
            }
        }
    }

    private async Task ShowScheduleBoardMoveDialogAsync(ScheduleBoardKind boardKind, ScheduleBoardItem item)
    {
        var board = boardKind == ScheduleBoardKind.SingleEvent
            ? BuildSingleScheduleBoardView()
            : BuildCrossEventScheduleBoardView();
        if (board is null)
        {
            SetStatus("当前没有可调整的赛程看板。", isError: true);
            return;
        }

        var dialog = new Window
        {
            Title = "移动比赛",
            Width = 520,
            Height = 360,
            MinWidth = 460,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var owner = boardKind == ScheduleBoardKind.SingleEvent
            ? _scheduleBoardWindow ?? this
            : _crossEventBoardWindow ?? this;

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Avalonia.Thickness(18)
        };
        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(40, 16, 78)),
            TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(new TextBlock
        {
            Text = $"当前：{item.DayLabel} {item.TimeRange} {item.Court}",
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            TextWrapping = TextWrapping.Wrap
        });

        var dayBox = new ComboBox { MinWidth = 220, ItemsSource = board.DayLabels };
        var timeBox = new ComboBox { MinWidth = 160 };
        var courtBox = new ComboBox { MinWidth = 160 };
        var errorText = new TextBlock
        {
            Foreground = ErrorStatusBrush,
            TextWrapping = TextWrapping.Wrap
        };

        void UpdateTargetControls(string? preferredTime = null, string? preferredCourt = null)
        {
            var day = board.FindDay(dayBox.SelectedItem?.ToString());
            if (day is null)
            {
                timeBox.ItemsSource = Array.Empty<string>();
                courtBox.ItemsSource = Array.Empty<string>();
                return;
            }

            var timeTexts = day.TimeSlots.Select(time => time.ToString("HH:mm")).ToList();
            timeBox.ItemsSource = timeTexts;
            var selectedTime = !string.IsNullOrWhiteSpace(preferredTime) && timeTexts.Contains(preferredTime)
                ? preferredTime
                : timeTexts.Contains(item.StartTime.ToString("HH:mm"))
                    ? item.StartTime.ToString("HH:mm")
                    : timeTexts.FirstOrDefault();
            timeBox.SelectedItem = selectedTime;

            courtBox.ItemsSource = day.Courts;
            var selectedCourt = !string.IsNullOrWhiteSpace(preferredCourt) && day.Courts.Contains(preferredCourt, StringComparer.Ordinal)
                ? preferredCourt
                : day.Courts.Contains(item.Court, StringComparer.Ordinal)
                    ? item.Court
                    : day.Courts.FirstOrDefault();
            courtBox.SelectedItem = selectedCourt;
        }

        dayBox.SelectionChanged += (_, _) => UpdateTargetControls();
        dayBox.SelectedItem = board.DayLabels.Contains(item.DayLabel) ? item.DayLabel : board.DayLabels.FirstOrDefault();
        UpdateTargetControls(item.StartTime.ToString("HH:mm"), item.Court);

        form.Children.Add(CreateScheduleMoveField("目标比赛日", dayBox));
        form.Children.Add(CreateScheduleMoveField("目标时间", timeBox));
        form.Children.Add(CreateScheduleMoveField("目标场地", courtBox));
        form.Children.Add(errorText);
        root.Children.Add(form);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(0, 16, 0, 0)
        };
        var cancelButton = new Button
        {
            Content = "取消",
            Width = 90,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        cancelButton.Click += (_, _) => dialog.Close(false);
        var moveButton = new Button
        {
            Content = "移动",
            Width = 90,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Classes = { "primary" }
        };
        moveButton.Click += async (_, _) =>
        {
            errorText.Text = "";
            if (string.IsNullOrWhiteSpace(dayBox.SelectedItem?.ToString())
                || string.IsNullOrWhiteSpace(timeBox.SelectedItem?.ToString())
                || string.IsNullOrWhiteSpace(courtBox.SelectedItem?.ToString())
                || !TimeOnly.TryParse(timeBox.SelectedItem!.ToString(), out var startTime))
            {
                errorText.Text = "请选择完整的目标比赛日、时间和场地。";
                return;
            }

            var target = new ScheduleBoardDropTarget(
                boardKind,
                dayBox.SelectedItem!.ToString()!,
                startTime,
                courtBox.SelectedItem!.ToString()!);
            try
            {
                var preview = BuildScheduleBoardCascadeMovePreview(target, item.DragPayload);
                var action = preview is { HasPreviewItems: true }
                    ? await ShowScheduleBoardCascadeMovePreviewDialogAsync(owner, boardKind, preview)
                    : ScheduleBoardCascadeMoveAction.MoveCurrentOnly;
                if (action == ScheduleBoardCascadeMoveAction.Cancel)
                {
                    return;
                }

                if (boardKind == ScheduleBoardKind.SingleEvent)
                {
                    if (action == ScheduleBoardCascadeMoveAction.CascadeMove)
                    {
                        CascadeMoveSingleScheduleBoardItem(item.DragPayload, target);
                    }
                    else
                    {
                        MoveSingleScheduleBoardItem(item.DragPayload, target);
                    }
                }
                else
                {
                    if (action == ScheduleBoardCascadeMoveAction.CascadeMove)
                    {
                        CascadeMoveCrossEventScheduleBoardItem(item.DragPayload, target);
                    }
                    else
                    {
                        MoveCrossEventScheduleBoardItem(item.DragPayload, target);
                    }
                }

                dialog.Close(true);
            }
            catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
            {
                errorText.Text = ex.Message;
            }
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(moveButton);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);

        dialog.Content = root;
        await dialog.ShowDialog<bool>(owner);
    }

    private static Grid CreateScheduleMoveField(string label, Control editor)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("110,*"),
            ColumnSpacing = 10
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private void ScheduleBoardDayTab_DragOver(object? sender, DragEventArgs e)
    {
        var payload = e.DataTransfer.TryGetText();
        if (sender is not Border { Tag: ScheduleBoardDayTabTarget target }
            || !IsScheduleBoardDragAllowed(target.Kind, payload))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        SwitchScheduleBoardDragDay(target);
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ScheduleBoardDayTab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: ScheduleBoardDayTabTarget target }
            || !e.GetCurrentPoint((Control)sender).Properties.IsLeftButtonPressed)
        {
            return;
        }

        SwitchScheduleBoardSelectedDay(target);
        e.Handled = true;
    }

    private void ScheduleBoardDayTab_Drop(object? sender, DragEventArgs e)
    {
        var payload = e.DataTransfer.TryGetText();
        if (sender is Border { Tag: ScheduleBoardDayTabTarget target }
            && IsScheduleBoardDragAllowed(target.Kind, payload))
        {
            SwitchScheduleBoardDragDay(target);
            SetStatus($"已切换到 {target.DayLabel}；请把比赛卡片拖到具体时间和场地格子后松开。", isWarning: true);
        }

        e.Handled = true;
    }

    private void SwitchScheduleBoardSelectedDay(ScheduleBoardDayTabTarget target)
    {
        var selectedDayLabel = target.Kind == ScheduleBoardKind.SingleEvent
            ? _scheduleBoardWindowDayBox?.SelectedItem?.ToString()
            : _crossEventBoardWindowDayBox?.SelectedItem?.ToString();
        if (string.Equals(selectedDayLabel, target.DayLabel, StringComparison.Ordinal))
        {
            return;
        }

        _scheduleBoardMoveValidationCache.Clear();
        ClearScheduleBoardDragFeedback();
        if (target.Kind == ScheduleBoardKind.SingleEvent)
        {
            RefreshScheduleBoardWindow(target.DayLabel);
        }
        else
        {
            RefreshCrossEventBoardWindow(target.DayLabel);
        }

        SetStatus($"已切换到 {target.DayLabel} 赛程。");
    }

    private void SwitchScheduleBoardDragDay(ScheduleBoardDayTabTarget target)
    {
        var selectedDayLabel = target.Kind == ScheduleBoardKind.SingleEvent
            ? _scheduleBoardWindowDayBox?.SelectedItem?.ToString()
            : _crossEventBoardWindowDayBox?.SelectedItem?.ToString();
        var isAlreadySelected = string.Equals(selectedDayLabel, target.DayLabel, StringComparison.Ordinal);
        if (isAlreadySelected
            && string.Equals(_scheduleBoardDragSwitchDayLabel, target.DayLabel, StringComparison.Ordinal))
        {
            return;
        }

        _scheduleBoardDragSwitchDayLabel = target.DayLabel;
        _scheduleBoardMoveValidationCache.Clear();
        ClearScheduleBoardDragFeedback();
        if (!isAlreadySelected)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (target.Kind == ScheduleBoardKind.SingleEvent)
                {
                    RefreshScheduleBoardWindow(target.DayLabel);
                }
                else
                {
                    RefreshCrossEventBoardWindow(target.DayLabel);
                }
            }, DispatcherPriority.Input);
        }

        SetStatus($"已切换到 {target.DayLabel}；继续拖到目标时间和场地后松开。");
    }

    private void ScheduleBoardCell_DragOver(object? sender, DragEventArgs e)
    {
        var text = e.DataTransfer.TryGetText();
        if (sender is not Border cell || cell.Tag is not ScheduleBoardDropTarget target)
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var result = ValidateScheduleBoardDrop(target, text);
        ApplyScheduleBoardDragFeedback(cell, target, result);
        AutoScrollScheduleBoardWindow(target.Kind, e);
        e.DragEffects = result.CanDrop ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void ScheduleBoardCell_DragLeave(object? sender, DragEventArgs e)
    {
        if (ReferenceEquals(sender, _scheduleBoardDragHoverCell))
        {
            ClearScheduleBoardDragFeedback();
        }
    }

    private async void ScheduleBoardCell_Drop(object? sender, DragEventArgs e)
    {
        ClearScheduleBoardDragFeedback();
        if (sender is not Border { Tag: ScheduleBoardDropTarget target })
        {
            return;
        }

        var payload = e.DataTransfer.TryGetText();
        var validation = ValidateScheduleBoardDrop(target, payload);
        if (!validation.CanDrop)
        {
            SetStatus(validation.Message, isError: true);
            return;
        }

        try
        {
            var action = await ConfirmScheduleBoardCascadeMovePreviewAsync(target, payload!);
            if (action == ScheduleBoardCascadeMoveAction.Cancel)
            {
                SetStatus("已取消移动，赛程保持不变。", isWarning: true);
                return;
            }

            if (target.Kind == ScheduleBoardKind.SingleEvent)
            {
                if (action == ScheduleBoardCascadeMoveAction.CascadeMove)
                {
                    CascadeMoveSingleScheduleBoardItem(payload!, target);
                }
                else
                {
                    MoveSingleScheduleBoardItem(payload!, target);
                }
            }
            else
            {
                if (action == ScheduleBoardCascadeMoveAction.CascadeMove)
                {
                    CascadeMoveCrossEventScheduleBoardItem(payload!, target);
                }
                else
                {
                    MoveCrossEventScheduleBoardItem(payload!, target);
                }
            }
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private async Task<ScheduleBoardCascadeMoveAction> ConfirmScheduleBoardCascadeMovePreviewAsync(
        ScheduleBoardDropTarget target,
        string payload)
    {
        var preview = BuildScheduleBoardCascadeMovePreview(target, payload);
        if (preview is null || !preview.HasPreviewItems)
        {
            return ScheduleBoardCascadeMoveAction.MoveCurrentOnly;
        }

        var owner = target.Kind == ScheduleBoardKind.SingleEvent
            ? _scheduleBoardWindow ?? this
            : _crossEventBoardWindow ?? this;
        return await ShowScheduleBoardCascadeMovePreviewDialogAsync(owner, target.Kind, preview);
    }

    private ScheduleBoardCascadeMovePreview? BuildScheduleBoardCascadeMovePreview(
        ScheduleBoardDropTarget target,
        string payload)
    {
        if (target.Kind == ScheduleBoardKind.SingleEvent)
        {
            if (_latestSchedule is null
                || !ScheduleBoardDrag.TryParseSingleEventPayload(payload, out var matchName))
            {
                return null;
            }

            return ScheduleWorkflow.BuildScheduledMatchCascadeMovePreview(
                _latestSchedule,
                matchName,
                target.DayLabel,
                target.StartTime,
                target.Court,
                _progressState?.Results.Keys.ToHashSet(StringComparer.Ordinal));
        }

        if (_crossEventScheduleBoard is null)
        {
            return null;
        }

        return _crossEventConflictWorkflow.BuildScheduleItemCascadeMovePreview(
            _crossEventScheduleBoard,
            payload,
            target.DayLabel,
            target.StartTime,
            target.Court);
    }

    private async Task<ScheduleBoardCascadeMoveAction> ShowScheduleBoardCascadeMovePreviewDialogAsync(
        Window owner,
        ScheduleBoardKind boardKind,
        ScheduleBoardCascadeMovePreview preview)
    {
        var dialog = new Window
        {
            Title = "连锁移动预览",
            Width = 680,
            Height = 520,
            MinWidth = 560,
            MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Avalonia.Thickness(18)
        };
        var header = new StackPanel { Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = "移动前请确认赛程影响",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(40, 16, 78)),
            TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(new TextBlock
        {
            Text = $"将“{preview.MatchName}”移动到 {preview.TargetText}；本项目后续依赖 {preview.AffectedMatches.Count} 场，兼项影响 {preview.CrossEventImpacts.Count} 条。你可以只移动当前场次，也可以让程序连锁后移后续场次。",
            Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
            TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(new TextBlock
        {
            Text = boardKind == ScheduleBoardKind.SingleEvent
                ? "单项目依赖来自淘汰树：胜者/负者进入后续轮次。"
                : "本项目后续依赖按项目内部淘汰树计算；兼项硬约束只检查已确定选手，负荷推演会在多项目提醒中按概率汇总未决晋级路径。",
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(header);

        var list = new StackPanel { Spacing = 10 };
        if (preview.AffectedMatches.Count > 0)
        {
            list.Children.Add(CreateScheduleBoardCascadeSectionTitle("本项目后续依赖"));
        }

        foreach (var item in preview.AffectedMatches.Take(30))
        {
            list.Children.Add(CreateScheduleBoardCascadePreviewCard(item));
        }

        if (preview.AffectedMatches.Count > 30)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"另有 {preview.AffectedMatches.Count - 30} 场后续比赛未显示，可后续通过赛程安排窗口继续检查。",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (preview.HasCrossEventImpact)
        {
            list.Children.Add(CreateScheduleBoardCascadeSectionTitle("兼项选手跨项目影响"));
            if (!string.IsNullOrWhiteSpace(preview.CrossEventImpactNote))
            {
                list.Children.Add(CreateScheduleBoardCascadeNoteCard(preview.CrossEventImpactNote));
            }

            foreach (var item in preview.CrossEventImpacts.Take(30))
            {
                list.Children.Add(CreateScheduleBoardCrossEventImpactCard(item));
            }

            if (preview.CrossEventImpacts.Count > 30)
            {
                list.Children.Add(new TextBlock
                {
                    Text = $"另有 {preview.CrossEventImpacts.Count - 30} 条兼项影响未显示，可在兼项明细中继续查看。",
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        var scroll = new ScrollViewer
        {
            Content = list,
            Margin = new Avalonia.Thickness(0, 14, 0, 0),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Avalonia.Thickness(0, 16, 0, 0)
        };
        var cancelButton = new Button
        {
            Content = "取消",
            MinWidth = 90,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        cancelButton.Click += (_, _) => dialog.Close(ScheduleBoardCascadeMoveAction.Cancel);
        var continueButton = new Button
        {
            Content = "只移动当前场次",
            MinWidth = 150,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        continueButton.Click += (_, _) => dialog.Close(ScheduleBoardCascadeMoveAction.MoveCurrentOnly);
        var cascadeButton = new Button
        {
            Content = "连锁移动后续场次",
            MinWidth = 170,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = preview.AffectedMatches.Count > 0,
            Classes = { "primary" }
        };
        cascadeButton.Click += (_, _) => dialog.Close(ScheduleBoardCascadeMoveAction.CascadeMove);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(continueButton);
        buttons.Children.Add(cascadeButton);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        dialog.Content = root;
        return await dialog.ShowDialog<ScheduleBoardCascadeMoveAction>(owner);
    }

    private static TextBlock CreateScheduleBoardCascadeSectionTitle(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(40, 16, 78)),
            Margin = new Avalonia.Thickness(0, 4, 0, 0)
        };
    }

    private static Border CreateScheduleBoardCascadeNoteCard(string text)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(239, 246, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(147, 197, 253)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(12),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175)),
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private Border CreateScheduleBoardCascadePreviewCard(ScheduleBoardCascadeMovePreviewItem item)
    {
        var isInvalid = item.RestMinutes < 0;
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = $"第 {item.Depth} 层后续 · {item.DayLabel} {item.TimeRange} · {item.Court} · {item.Phase} {item.DisplayMatchName}",
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(43, 20, 95)),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{item.DependencyText}；与前序间隔 {item.RestMinutes} 分钟{(item.IsCompleted ? "；该场已有赛果" : "")}",
            Foreground = isInvalid
                ? ThemeBrush("AppErrorTextBrush", Color.FromRgb(185, 28, 28))
                : ThemeBrush("AppWarningTextBrush", Color.FromRgb(120, 83, 0)),
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Background = item.IsCompleted
                ? ThemeBrush("AppSurfaceMutedBrush", Color.FromRgb(241, 245, 249))
                : isInvalid
                    ? ThemeBrush("AppErrorCardBackgroundBrush", Color.FromRgb(254, 242, 242))
                    : ThemeBrush("AppWarningCardBackgroundBrush", Color.FromRgb(255, 251, 235)),
            BorderBrush = isInvalid
                ? ThemeBrush("AppErrorCardBorderBrush", Color.FromRgb(220, 38, 38))
                : ThemeBrush("AppWarningCardBorderBrush", Color.FromRgb(245, 158, 11)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(12),
            Child = stack
        };
    }

    private Border CreateScheduleBoardCrossEventImpactCard(ScheduleBoardCrossEventImpactPreviewItem item)
    {
        var isSevere = item.Severity == CrossEventConflictSeverity.Severe;
        var isWarning = item.Severity == CrossEventConflictSeverity.Warning;
        var borderBrush = isSevere
            ? ThemeBrush("AppErrorCardBorderBrush", Color.FromRgb(220, 38, 38))
            : isWarning
                ? ThemeBrush("AppWarningCardBorderBrush", Color.FromRgb(245, 158, 11))
                : ThemeBrush("AppWarningCardBorderBrush", Color.FromRgb(242, 216, 137));
        var backgroundBrush = isSevere
            ? ThemeBrush("AppErrorCardBackgroundBrush", Color.FromRgb(254, 242, 242))
            : ThemeBrush("AppWarningCardBackgroundBrush", isWarning ? Color.FromRgb(255, 251, 235) : Color.FromRgb(255, 248, 230));
        var detailBrush = isSevere
            ? ThemeBrush("AppErrorTextBrush", Color.FromRgb(185, 28, 28))
            : ThemeBrush("AppWarningTextBrush", Color.FromRgb(120, 83, 0));
        var label = item.Severity switch
        {
            CrossEventConflictSeverity.Severe => "严重",
            CrossEventConflictSeverity.Warning => "警告",
            _ => "提醒"
        };
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{label} · {item.PlayerName} · {item.DayLabel} {item.TimeRange} · {item.Court} · {item.EventName} {item.Phase} {item.MatchName}",
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(43, 20, 95)),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{item.Detail}{(item.IsCompleted ? "；该场已有赛果" : "")}",
            Foreground = detailBrush,
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Background = backgroundBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Avalonia.Thickness(isSevere ? 2 : 1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(12),
            Child = stack
        };
    }

    private static bool IsScheduleBoardDragAllowed(ScheduleBoardKind kind, string? payload)
    {
        return kind == ScheduleBoardKind.SingleEvent
            ? ScheduleBoardDrag.TryParseSingleEventPayload(payload, out _)
            : !string.IsNullOrWhiteSpace(payload)
              && !ScheduleBoardDrag.TryParseSingleEventPayload(payload, out _);
    }

    private ScheduleBoardMoveValidationResult ValidateScheduleBoardDrop(
        ScheduleBoardDropTarget target,
        string? payload)
    {
        var cacheKey = $"{target.Kind}\u001F{payload}\u001F{target.DayLabel}\u001F{target.StartTime:HH:mm}\u001F{target.Court}";
        if (_scheduleBoardMoveValidationCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var result = ValidateScheduleBoardDropCore(target, payload);
        _scheduleBoardMoveValidationCache[cacheKey] = result;
        return result;
    }

    private ScheduleBoardMoveValidationResult ValidateScheduleBoardDropCore(
        ScheduleBoardDropTarget target,
        string? payload)
    {
        if (!IsScheduleBoardDragAllowed(target.Kind, payload))
        {
            return ScheduleBoardMoveValidationResult.Blocked("不能把这张比赛卡片放到当前赛程面板。");
        }

        if (target.Kind == ScheduleBoardKind.SingleEvent)
        {
            if (_latestSchedule is null
                || !ScheduleBoardDrag.TryParseSingleEventPayload(payload, out var matchName))
            {
                return ScheduleBoardMoveValidationResult.Blocked("当前没有可调整的单项目赛程。");
            }

            return ScheduleWorkflow.ValidateScheduledMatchMove(
                _latestSchedule,
                matchName,
                target.DayLabel,
                target.StartTime,
                target.Court,
                _progressState?.Results.Keys.ToHashSet(StringComparer.Ordinal));
        }

        if (_crossEventScheduleBoard is null || string.IsNullOrWhiteSpace(payload))
        {
            return ScheduleBoardMoveValidationResult.Blocked("当前没有可调整的多项目赛程。");
        }

        return _crossEventConflictWorkflow.ValidateScheduleItemMove(
            _crossEventScheduleBoard,
            payload,
            target.DayLabel,
            target.StartTime,
            target.Court);
    }

    private void ApplyScheduleBoardDragFeedback(
        Border cell,
        ScheduleBoardDropTarget target,
        ScheduleBoardMoveValidationResult result)
    {
        if (!ReferenceEquals(_scheduleBoardDragHoverCell, cell))
        {
            ClearScheduleBoardDragFeedback();
            _scheduleBoardDragHoverCell = cell;
        }
        else
        {
            RemoveScheduleBoardDragFeedbackCard();
        }

        var (background, border) = result.Severity switch
        {
            ScheduleBoardMoveValidationSeverity.Blocked => (
                ThemeBrush("AppErrorCardBackgroundBrush", Color.FromRgb(254, 242, 242)),
                ThemeBrush("AppErrorCardBorderBrush", Color.FromRgb(220, 38, 38))),
            ScheduleBoardMoveValidationSeverity.Warning => (
                ThemeBrush("AppWarningCardBackgroundBrush", Color.FromRgb(255, 251, 235)),
                ThemeBrush("AppWarningCardBorderBrush", Color.FromRgb(245, 158, 11))),
            _ => (
                ThemeBrush("AppSuccessCardBackgroundBrush", Color.FromRgb(236, 253, 245)),
                ThemeBrush("AppSuccessCardBorderBrush", Color.FromRgb(34, 197, 94)))
        };
        cell.Background = background;
        cell.BorderBrush = border;
        cell.BorderThickness = new Avalonia.Thickness(3);
        ToolTip.SetTip(cell, result.Message);
        if (cell.Child is StackPanel stack)
        {
            _scheduleBoardDragFeedbackCard = CreateScheduleBoardDropFeedbackCard(target, result);
            stack.Children.Insert(0, _scheduleBoardDragFeedbackCard);
        }

        if (!string.Equals(_lastScheduleBoardDragFeedbackMessage, result.Message, StringComparison.Ordinal))
        {
            _lastScheduleBoardDragFeedbackMessage = result.Message;
            SetStatus(
                result.Message,
                isError: result.Severity == ScheduleBoardMoveValidationSeverity.Blocked,
                isWarning: result.Severity == ScheduleBoardMoveValidationSeverity.Warning);
        }
    }

    private void ClearScheduleBoardDragFeedback()
    {
        if (_scheduleBoardDragHoverCell is not null)
        {
            RemoveScheduleBoardDragFeedbackCard();
            _scheduleBoardDragHoverCell.Background = ThemeBrush("AppSurfaceBrush", Color.FromRgb(255, 255, 255));
            _scheduleBoardDragHoverCell.BorderBrush = ThemeBrush("AppSoftBorderBrush", Color.FromRgb(226, 232, 240));
            _scheduleBoardDragHoverCell.BorderThickness = new Avalonia.Thickness(0, 0, 1, 1);
            ToolTip.SetTip(_scheduleBoardDragHoverCell, null);
        }

        _scheduleBoardDragHoverCell = null;
        _lastScheduleBoardDragFeedbackMessage = null;
    }

    private void RemoveScheduleBoardDragFeedbackCard()
    {
        if (_scheduleBoardDragHoverCell?.Child is StackPanel stack && _scheduleBoardDragFeedbackCard is not null)
        {
            stack.Children.Remove(_scheduleBoardDragFeedbackCard);
        }

        _scheduleBoardDragFeedbackCard = null;
    }

    private Border CreateScheduleBoardDropFeedbackCard(
        ScheduleBoardDropTarget target,
        ScheduleBoardMoveValidationResult result)
    {
        var (label, background, border, foreground) = result.Severity switch
        {
            ScheduleBoardMoveValidationSeverity.Blocked => (
                "硬冲突不可放置",
                ThemeBrush("AppErrorCardBackgroundBrush", Color.FromRgb(254, 242, 242)),
                ThemeBrush("AppErrorCardBorderBrush", Color.FromRgb(220, 38, 38)),
                ThemeBrush("AppErrorTextBrush", Color.FromRgb(185, 28, 28))),
            ScheduleBoardMoveValidationSeverity.Warning => (
                "软冲突可放置",
                ThemeBrush("AppWarningCardBackgroundBrush", Color.FromRgb(255, 251, 235)),
                ThemeBrush("AppWarningCardBorderBrush", Color.FromRgb(245, 158, 11)),
                ThemeBrush("AppWarningTextBrush", Color.FromRgb(146, 64, 14))),
            _ => (
                "可放置",
                ThemeBrush("AppSuccessCardBackgroundBrush", Color.FromRgb(236, 253, 245)),
                ThemeBrush("AppSuccessCardBorderBrush", Color.FromRgb(34, 197, 94)),
                ThemeBrush("AppSuccessTextBrush", Color.FromRgb(22, 101, 52)))
        };

        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{label} · {target.DayLabel} {target.StartTime:HH:mm} · {target.Court}",
            FontWeight = FontWeight.Bold,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = result.Message,
            FontSize = 12,
            Foreground = ThemeBrush("AppBodyTextBrush", Color.FromRgb(30, 41, 59)),
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Background = background,
            BorderBrush = border,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(8),
            Margin = new Avalonia.Thickness(0, 0, 0, 6),
            Child = stack,
            IsHitTestVisible = false
        };
    }

    private void AutoScrollScheduleBoardWindow(ScheduleBoardKind kind, DragEventArgs e)
    {
        var scrollViewer = kind == ScheduleBoardKind.SingleEvent
            ? _scheduleBoardWindowScrollViewer
            : _crossEventBoardWindowScrollViewer;
        if (scrollViewer is null || !scrollViewer.IsVisible)
        {
            return;
        }

        var point = e.GetPosition(scrollViewer);
        var deltaX = GetScheduleBoardAutoScrollDelta(point.X, scrollViewer.Bounds.Width);
        var deltaY = GetScheduleBoardAutoScrollDelta(point.Y, scrollViewer.Bounds.Height);
        if (Math.Abs(deltaX) < 0.001 && Math.Abs(deltaY) < 0.001)
        {
            return;
        }

        var maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
        var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        scrollViewer.Offset = new Vector(
            Math.Clamp(scrollViewer.Offset.X + deltaX, 0, maxX),
            Math.Clamp(scrollViewer.Offset.Y + deltaY, 0, maxY));
    }

    private static double GetScheduleBoardAutoScrollDelta(double position, double length)
    {
        if (position < ScheduleBoardAutoScrollEdgeThreshold)
        {
            return -ScheduleBoardAutoScrollStep;
        }

        if (position > length - ScheduleBoardAutoScrollEdgeThreshold)
        {
            return ScheduleBoardAutoScrollStep;
        }

        return 0;
    }

    private void MoveSingleScheduleBoardItem(string payload, ScheduleBoardDropTarget target)
    {
        if (_latestSchedule is null
            || !ScheduleBoardDrag.TryParseSingleEventPayload(payload, out var matchName))
        {
            return;
        }

        var validation = ScheduleWorkflow.ValidateScheduledMatchMove(
            _latestSchedule,
            matchName,
            target.DayLabel,
            target.StartTime,
            target.Court,
            _progressState?.Results.Keys.ToHashSet(StringComparer.Ordinal));
        if (!validation.CanDrop)
        {
            throw new DrawValidationException(validation.Message);
        }

        var previousSchedule = _latestSchedule;
        var previousProgressState = _progressState;
        var previousDayLabel = previousSchedule.Matches
            .FirstOrDefault(match => string.Equals(match.MatchName, matchName, StringComparison.Ordinal))
            ?.DayLabel
            ?? target.DayLabel;
        var movedSchedule = ScheduleWorkflow.MoveScheduledMatch(
            _latestSchedule,
            matchName,
            target.DayLabel,
            target.StartTime,
            target.Court,
            _progressState?.Results.Keys.ToHashSet(StringComparer.Ordinal));
        _singleScheduleUndoStack.Push(new SingleScheduleUndoSnapshot(
            previousSchedule,
            previousProgressState,
            _progressFilePath,
            previousDayLabel));
        _latestSchedule = movedSchedule;
        if (_progressState is not null)
        {
            _progressState = ReplaceProgressSchedule(_progressState, _latestSchedule);
            UpdateProgressDisplay();
        }

        ScheduleList.ItemsSource = FormatScheduleRows(_latestSchedule);
        UpdateScheduleConstraintReport(_latestSchedule);
        ScheduleSummaryText.Text =
            $"已调整 {_latestSchedule.Matches.Count} 场赛程，预计 {_latestSchedule.DayCount} 个比赛日。"
            + BuildScheduleQualitySentence(_latestSchedule.QualityReport);
        RefreshScheduleBoardWindow(target.DayLabel);
        UpdateScheduleUndoButtons();
        SetStatus(
            "赛程安排已调整；后续导出会使用调整后的时间和场地。",
            isWarning: _latestScheduleConstraintReport is { SevereCount: > 0 } or { WarningCount: > 0 });
    }

    private void CascadeMoveSingleScheduleBoardItem(string payload, ScheduleBoardDropTarget target)
    {
        if (_latestSchedule is null
            || !ScheduleBoardDrag.TryParseSingleEventPayload(payload, out var matchName))
        {
            return;
        }

        var previousSchedule = _latestSchedule;
        var previousProgressState = _progressState;
        var previousDayLabel = previousSchedule.Matches
            .FirstOrDefault(match => string.Equals(match.MatchName, matchName, StringComparison.Ordinal))
            ?.DayLabel
            ?? target.DayLabel;
        var result = ScheduleWorkflow.CascadeMoveScheduledMatch(
            _latestSchedule,
            matchName,
            target.DayLabel,
            target.StartTime,
            target.Court,
            _progressState?.Results.Keys.ToHashSet(StringComparer.Ordinal));
        _singleScheduleUndoStack.Push(new SingleScheduleUndoSnapshot(
            previousSchedule,
            previousProgressState,
            _progressFilePath,
            previousDayLabel));
        _latestSchedule = result.Schedule;
        if (_progressState is not null)
        {
            _progressState = ReplaceProgressSchedule(_progressState, _latestSchedule);
            UpdateProgressDisplay();
        }

        ScheduleList.ItemsSource = FormatScheduleRows(_latestSchedule);
        UpdateScheduleConstraintReport(_latestSchedule);
        ScheduleSummaryText.Text =
            $"已连锁调整 {_latestSchedule.Matches.Count} 场赛程，预计 {_latestSchedule.DayCount} 个比赛日。"
            + BuildScheduleQualitySentence(_latestSchedule.QualityReport);
        RefreshScheduleBoardWindow(target.DayLabel);
        UpdateScheduleUndoButtons();
        var movedCount = result.MovedMatches.Count;
        SetStatus(
            movedCount > 0
                ? $"已连锁移动 {movedCount} 场赛程；后续导出会使用调整后的时间和场地。"
                : "当前赛程已经满足后续依赖，无需移动后续场次。",
            isWarning: _latestScheduleConstraintReport is { SevereCount: > 0 } or { WarningCount: > 0 });
    }

    private void MoveCrossEventScheduleBoardItem(string payload, ScheduleBoardDropTarget target)
    {
        if (_crossEventScheduleBoard is null)
        {
            return;
        }

        var validation = _crossEventConflictWorkflow.ValidateScheduleItemMove(
            _crossEventScheduleBoard,
            payload,
            target.DayLabel,
            target.StartTime,
            target.Court);
        if (!validation.CanDrop)
        {
            throw new DrawValidationException(validation.Message);
        }

        var previousBoard = _crossEventScheduleBoard;
        var previousDayLabel = previousBoard.Items
            .FirstOrDefault(item => string.Equals(item.Key, payload, StringComparison.Ordinal))
            ?.DayLabel
            ?? target.DayLabel;
        var movedBoard = _crossEventConflictWorkflow.MoveScheduleItem(
            _crossEventScheduleBoard,
            payload,
            target.DayLabel,
            target.StartTime,
            target.Court);
        _crossEventScheduleUndoStack.Push(new CrossEventScheduleUndoSnapshot(
            previousBoard,
            previousDayLabel));
        _crossEventScheduleBoard = movedBoard;
        RefreshCrossEventScheduleBoard(target.DayLabel);
        RefreshCrossEventBoardWindow(target.DayLabel);
        UpdateCrossEventUndoButtons();
        PreviewTabs.SelectedItem = CrossEventPreviewTab;
        SetStatus(BuildCrossEventStatus("已调整多项目赛程", _crossEventScheduleBoard));
    }

    private void CascadeMoveCrossEventScheduleBoardItem(string payload, ScheduleBoardDropTarget target)
    {
        if (_crossEventScheduleBoard is null)
        {
            return;
        }

        var previousBoard = _crossEventScheduleBoard;
        var previousDayLabel = previousBoard.Items
            .FirstOrDefault(item => string.Equals(item.Key, payload, StringComparison.Ordinal))
            ?.DayLabel
            ?? target.DayLabel;
        var result = _crossEventConflictWorkflow.CascadeMoveScheduleItem(
            _crossEventScheduleBoard,
            payload,
            target.DayLabel,
            target.StartTime,
            target.Court);
        _crossEventScheduleUndoStack.Push(new CrossEventScheduleUndoSnapshot(
            previousBoard,
            previousDayLabel));
        _crossEventScheduleBoard = result.Schedule;
        _crossEventSchedulingOptions = _crossEventScheduleBoard.SchedulingOptions ?? _crossEventSchedulingOptions;
        RefreshCrossEventScheduleBoard(target.DayLabel);
        RefreshCrossEventBoardWindow(target.DayLabel);
        UpdateCrossEventUndoButtons();
        PreviewTabs.SelectedItem = CrossEventPreviewTab;
        var movedCount = result.MovedMatches.Count;
        SetStatus(BuildCrossEventStatus(
            movedCount > 0
                ? $"已连锁移动 {movedCount} 场多项目赛程"
                : "当前多项目赛程已经满足后续依赖，无需移动后续场次",
            _crossEventScheduleBoard));
    }

    private void UndoSingleScheduleMove_Click(object? sender, RoutedEventArgs e)
    {
        if (!_singleScheduleUndoStack.TryPop(out var snapshot))
        {
            SetStatus("当前没有可撤销的赛程调整。", isWarning: true);
            UpdateScheduleUndoButtons();
            return;
        }

        _latestSchedule = snapshot.Schedule;
        _progressState = snapshot.ProgressState;
        _progressFilePath = snapshot.ProgressFilePath;

        UpdateProgressDisplay();
        ScheduleList.ItemsSource = FormatScheduleRows(_latestSchedule);
        UpdateScheduleConstraintReport(_latestSchedule);
        ScheduleSummaryText.Text =
            $"已撤销上一步调整；当前 {_latestSchedule.Matches.Count} 场赛程，预计 {_latestSchedule.DayCount} 个比赛日。"
            + BuildScheduleQualitySentence(_latestSchedule.QualityReport);
        RefreshScheduleBoardWindow(snapshot.DayLabel);
        UpdateScheduleUndoButtons();
        SetStatus(
            "已撤销上一步赛程调整；后续导出会使用撤销后的时间和场地。",
            isWarning: _latestScheduleConstraintReport is { SevereCount: > 0 } or { WarningCount: > 0 });
    }

    private void UndoCrossEventScheduleMove_Click(object? sender, RoutedEventArgs e)
    {
        if (!_crossEventScheduleUndoStack.TryPop(out var snapshot))
        {
            SetStatus("当前没有可撤销的多项目赛程调整。", isWarning: true);
            UpdateCrossEventUndoButtons();
            return;
        }

        _crossEventScheduleBoard = snapshot.Board;
        _crossEventSchedulingOptions = snapshot.Board.SchedulingOptions ?? _crossEventSchedulingOptions;
        RefreshCrossEventScheduleBoard(snapshot.DayLabel);
        RefreshCrossEventBoardWindow(snapshot.DayLabel);
        UpdateCrossEventUndoButtons();
        PreviewTabs.SelectedItem = CrossEventPreviewTab;
        SetStatus(BuildCrossEventStatus("已撤销上一步多项目赛程调整", _crossEventScheduleBoard));
    }

    private void ClearSingleScheduleUndoStack()
    {
        _singleScheduleUndoStack.Clear();
        UpdateScheduleUndoButtons();
    }

    private void ClearCrossEventScheduleUndoStack()
    {
        _crossEventScheduleUndoStack.Clear();
        UpdateCrossEventUndoButtons();
    }

    private void UpdateScheduleUndoButtons()
    {
        var canUndo = _singleScheduleUndoStack.Count > 0;
        if (ScheduleUndoButton is not null)
        {
            ScheduleUndoButton.IsEnabled = canUndo;
        }

        if (_scheduleBoardWindowUndoButton is not null)
        {
            _scheduleBoardWindowUndoButton.IsEnabled = canUndo;
        }
    }

    private void UpdateCrossEventUndoButtons()
    {
        var canUndo = _crossEventScheduleUndoStack.Count > 0;
        if (CrossEventUndoButton is not null)
        {
            CrossEventUndoButton.IsEnabled = canUndo;
        }

        if (_crossEventBoardWindowUndoButton is not null)
        {
            _crossEventBoardWindowUndoButton.IsEnabled = canUndo;
        }
    }

    private static TournamentProgressState ReplaceProgressSchedule(TournamentProgressState state, SchedulePlan schedule)
    {
        return state with
        {
            Snapshot = state.Snapshot with
            {
                Schedule = schedule,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };
    }
}
