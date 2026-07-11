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
    private async void ExportCrossEventConflictReport_Click(object? sender, RoutedEventArgs e)
    {
        if (_crossEventScheduleBoard is not null)
        {
            await ExportCurrentCrossEventBoardReport();
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择需要一起检查的赛事存档（至少两个）",
            AllowMultiple = true,
            FileTypeFilter = [ProgressFileType]
        });
        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var outputPath = await PickSavePath(
            "保存多项目排程检查报告",
            CrossEventConflictWorkflow.BuildDefaultReportFileName(),
            WorkflowExportFormat.Excel);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        try
        {
            var minimumRestMinutes = GetCrossEventMinimumRestMinutes();
            var result = _crossEventConflictWorkflow.ExportProgressReport(
                paths,
                outputPath,
                minimumRestMinutes);
            SetStatus(
                $"多项目排程检查报告已导出：{result.OutputPath}。"
                + $"严重 {result.Report.SevereCount} 条，警告 {result.Report.WarningCount} 条，"
                + $"同日/负荷推演提醒 {result.Report.NoticeCount} 条。");
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private async void LoadCrossEventScheduleBoard_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择需要一起编排的赛事存档（至少两个）",
            AllowMultiple = true,
            FileTypeFilter = [ProgressFileType]
        });
        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        try
        {
            _crossEventBaseScheduleBoard = _crossEventConflictWorkflow.LoadScheduleBoard(
                paths,
                GetCrossEventMinimumRestMinutes());
            _crossEventScheduleBoard = _crossEventBaseScheduleBoard;
            _crossEventLastAcceptedBoard = null;
            _crossEventSchedulingOptions = null;
            _crossEventRecommendedCustomOptions = null;
            _crossEventLastAcceptedOptions = null;
            ClearCrossEventScheduleUndoStack();
            PreviewTabs.SelectedItem = CrossEventPreviewTab;
            RunCrossEventScheduling(
                GetCrossEventSchedulingStrategy(),
                CrossEventCustomAnchor.None,
                "多项目赛程已加载并自动编排",
                rollbackOnFailure: false);
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void CrossEventSchedulingStrategyBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady && _crossEventScheduleBoard is null)
        {
            return;
        }

        var strategy = GetCrossEventSchedulingStrategy();
        CrossEventCustomSchedulingPanel.IsVisible = strategy == CrossEventSchedulingStrategy.Custom;
        if (_crossEventScheduleBoard is null)
        {
            return;
        }

        if (strategy == CrossEventSchedulingStrategy.Custom)
        {
            EnsureCrossEventCustomRecommendation();
        }
        else
        {
            _crossEventRecommendedCustomOptions = null;
        }

        RunCrossEventScheduling(
            strategy,
            strategy == CrossEventSchedulingStrategy.Custom
                ? new CrossEventCustomAnchor(CrossEventCustomAnchorKind.Recommended)
                : CrossEventCustomAnchor.None,
            $"{GetCrossEventSchedulingStrategyName(strategy)}策略已重新编排",
            rollbackOnFailure: true);
    }

    private void CrossEventCustomOption_Changed(object? sender, RoutedEventArgs e)
    {
        QueueCrossEventCustomRecalculate(CrossEventCustomAnchor.None);
    }

    private void CrossEventCustomSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingCrossEventCustomControls)
        {
            return;
        }

        UpdateCrossEventCustomLabels();
        var anchor = sender is Slider { Tag: CrossEventCustomSliderTag tag }
            ? new CrossEventCustomAnchor(tag.Kind, tag.DayLabel)
            : CrossEventCustomAnchor.None;
        QueueCrossEventCustomRecalculate(anchor);
    }

    private void CrossEventCustomRecalculateTimer_Tick(object? sender, EventArgs e)
    {
        _crossEventCustomRecalculateTimer?.Stop();
        if (GetCrossEventSchedulingStrategy() == CrossEventSchedulingStrategy.Custom)
        {
            RunCrossEventScheduling(
                CrossEventSchedulingStrategy.Custom,
                _pendingCrossEventCustomAnchor,
                $"自定义参数已按{_pendingCrossEventCustomAnchor.Describe()}重新编排",
                rollbackOnFailure: true);
        }
    }

    private void QueueCrossEventCustomRecalculate(CrossEventCustomAnchor anchor)
    {
        if (_updatingCrossEventCustomControls
            || _crossEventScheduleBoard is null
            || GetCrossEventSchedulingStrategy() != CrossEventSchedulingStrategy.Custom)
        {
            return;
        }

        _pendingCrossEventCustomAnchor = anchor;
        _crossEventCustomRecalculateTimer?.Stop();
        _crossEventCustomRecalculateTimer?.Start();
        SetStatus($"自定义参数已变化，将以{anchor.Describe()}为锚点重新编排…", isWarning: true);
    }

    private void ResetCrossEventCustomDefaults_Click(object? sender, RoutedEventArgs e)
    {
        if (_crossEventScheduleBoard is null)
        {
            SetStatus("请先加载多项目赛程。", isError: true);
            return;
        }

        EnsureCrossEventCustomRecommendation();
        RunCrossEventScheduling(
            CrossEventSchedulingStrategy.Custom,
            new CrossEventCustomAnchor(CrossEventCustomAnchorKind.Recommended),
            "已恢复推荐分布并重新编排",
            rollbackOnFailure: true);
    }

    private void CrossEventRestMinutesBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_crossEventScheduleBoard is null)
        {
            return;
        }

        try
        {
            _ = GetCrossEventMinimumRestMinutes();
        }
        catch (DrawValidationException ex)
        {
            SetStatus(ex.Message, isError: true);
            return;
        }

        RunCrossEventScheduling(
            GetCrossEventSchedulingStrategy(),
            new CrossEventCustomAnchor(CrossEventCustomAnchorKind.MinimumRest),
            "最小休息间隔已变化并重新编排",
            rollbackOnFailure: true);
    }

    private void CrossEventRefereeCountBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_crossEventScheduleBoard is null)
        {
            return;
        }

        try
        {
            _ = GetCrossEventRefereeCount();
        }
        catch (DrawValidationException ex)
        {
            SetStatus(ex.Message, isError: true);
            return;
        }

        RunCrossEventScheduling(
            GetCrossEventSchedulingStrategy(),
            new CrossEventCustomAnchor(CrossEventCustomAnchorKind.RefereeCount),
            "裁判人数已变化并重新编排",
            rollbackOnFailure: true);
    }

    private void SaveCrossEventScheduleBoard_Click(object? sender, RoutedEventArgs e)
    {
        if (_crossEventScheduleBoard is null)
        {
            SetStatus("请先加载多项目赛程。", isError: true);
            return;
        }

        try
        {
            var result = _crossEventConflictWorkflow.SaveScheduleBoard(_crossEventScheduleBoard);
            _crossEventScheduleBoard = _crossEventScheduleBoard with { HasUnsavedChanges = false };
            ClearCrossEventScheduleUndoStack();
            RefreshCrossEventScheduleBoard(GetSelectedCrossEventDayLabel());
            RefreshCrossEventBoardWindow(GetSelectedCrossEventDayLabel());
            SetStatus($"已保存 {result.UpdatedPaths.Count} 个赛事存档，备份 {result.BackupPaths.Count} 个。");
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private async System.Threading.Tasks.Task ExportCurrentCrossEventBoardReport()
    {
        if (_crossEventScheduleBoard is null)
        {
            return;
        }

        var outputPath = await PickSavePath(
            "保存多项目排程检查报告",
            CrossEventConflictWorkflow.BuildDefaultReportFileName(),
            WorkflowExportFormat.Excel);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        try
        {
            var result = _crossEventConflictWorkflow.ExportScheduleBoardReport(_crossEventScheduleBoard, outputPath);
            SetStatus(
                $"当前多项目排程检查报告已导出：{result.OutputPath}。"
                + $"严重 {result.Report.SevereCount} 条，警告 {result.Report.WarningCount} 条，"
                + $"同日/负荷推演提醒 {result.Report.NoticeCount} 条。");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private async void ExportCrossEventMergedMaterials_Click(object? sender, RoutedEventArgs e)
    {
        if (_crossEventScheduleBoard is null)
        {
            SetStatus("请先加载多项目赛程。", isError: true);
            return;
        }

        var outputDirectory = await PickFolderPath("选择合并材料包保存文件夹");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        try
        {
            var result = _crossEventConflictWorkflow.ExportMergedScheduleMaterials(_crossEventScheduleBoard, outputDirectory);
            var dayText = result.DayLabels.Count switch
            {
                0 => "无比赛日",
                1 => result.DayLabels[0],
                _ => $"{result.DayLabels.First()} 至 {result.DayLabels.Last()}"
            };
            SetStatus(
                $"多项目合并材料包已导出到：{result.OutputDirectory}（{dayText}，"
                + $"共 {result.Schedule.Matches.Count} 场、{result.OutputPaths.Count} 个文件，含材料包说明）。"
                + $"排程检查：严重 {_crossEventScheduleBoard.Report.SevereCount}，"
                + $"警告 {_crossEventScheduleBoard.Report.WarningCount}，提醒 {_crossEventScheduleBoard.Report.NoticeCount}。",
                isWarning: _crossEventScheduleBoard.Report.WarningCount > 0);
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void CrossEventDayBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshCrossEventScheduleBoard(GetSelectedCrossEventDayLabel());
    }

    private async void ShowCrossEventPlayerDetails_Click(object? sender, RoutedEventArgs e)
    {
        if (_crossEventScheduleBoard is null)
        {
            SetStatus("请先加载多项目赛程。", isError: true);
            return;
        }

        var rows = BuildCrossEventPlayerSummaryRows(_crossEventScheduleBoard.PlayerDetails, CrossEventPlayerSortMode.Default);
        if (rows.Count == 0)
        {
            SetStatus("当前没有识别到跨项目兼项选手。", isWarning: true);
            return;
        }

        var sortBox = new ComboBox
        {
            Width = 190,
            ItemsSource = new[]
            {
                "默认排序",
                "休息时间从短到长",
                "休息时间从长到短"
            },
            SelectedIndex = 0
        };
        var listBox = new ListBox
        {
            ItemsSource = rows,
            MinWidth = 330
        };
        var detailStack = new StackPanel { Spacing = 10 };
        listBox.SelectionChanged += (_, _) =>
        {
            if (listBox.SelectedItem is CrossEventPlayerSummaryRow row)
            {
                RenderCrossEventPlayerDetailCards(detailStack, row.Entry);
            }
        };
        void RefreshPlayerRows()
        {
            var sortMode = sortBox.SelectedIndex switch
            {
                1 => CrossEventPlayerSortMode.RestAscending,
                2 => CrossEventPlayerSortMode.RestDescending,
                _ => CrossEventPlayerSortMode.Default
            };
            listBox.ItemsSource = BuildCrossEventPlayerSummaryRows(_crossEventScheduleBoard.PlayerDetails, sortMode);
            listBox.SelectedIndex = 0;
        }

        sortBox.SelectionChanged += (_, _) => RefreshPlayerRows();

        var root = new Grid { Margin = new Avalonia.Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = $"兼项选手 {rows.Count} 人；明细会随多项目赛程调整实时重新计算。",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(43, 20, 95)),
            Margin = new Avalonia.Thickness(0, 0, 0, 10)
        });
        var sortPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top
        };
        sortPanel.Children.Add(new TextBlock
        {
            Text = "排序",
            Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(100, 116, 139)),
            VerticalAlignment = VerticalAlignment.Center
        });
        sortPanel.Children.Add(sortBox);
        Grid.SetColumn(sortPanel, 1);
        header.Children.Add(sortPanel);
        root.Children.Add(header);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("350,14,*") };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var playerPanel = CreateCrossEventDialogPanel("选手兼项汇总", listBox);
        var detailPanel = CreateCrossEventDialogPanel(
            "该选手赛程明细",
            new ScrollViewer
            {
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = detailStack
            });
        Grid.SetColumn(playerPanel, 0);
        Grid.SetColumn(detailPanel, 2);
        body.Children.Add(playerPanel);
        body.Children.Add(detailPanel);

        var dialog = new Window
        {
            Title = "兼项明细",
            Width = 1240,
            Height = 780,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        RefreshPlayerRows();
        await dialog.ShowDialog(this);
    }

    private Border CreateCrossEventDialogPanel(string title, Control content)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrush("AppButtonTextBrush", Color.FromRgb(39, 59, 99)),
            Margin = new Avalonia.Thickness(0, 0, 0, 8)
        });
        Grid.SetRow(content, 1);
        grid.Children.Add(content);
        return new Border
        {
            Background = ThemeBrush("AppSurfaceMutedBrush", Color.FromRgb(248, 250, 252)),
            BorderBrush = ThemeBrush("AppSoftBorderBrush", Color.FromRgb(226, 232, 240)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10),
            Padding = new Avalonia.Thickness(10),
            Child = grid
        };
    }

    private void RenderCrossEventPlayerDetailCards(
        StackPanel detailStack,
        CrossEventPlayerMultiEntry entry)
    {
        detailStack.Children.Clear();
        detailStack.Children.Add(new TextBlock
        {
            Text = $"{entry.PlayerName}：{string.Join("、", entry.EventNames)}",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(40, 16, 78)),
            TextWrapping = TextWrapping.Wrap
        });
        detailStack.Children.Add(new TextBlock
        {
            Text = $"共 {entry.MatchCount} 场，未完成 {entry.PendingMatchCount} 场；严重 {entry.SevereIssueCount} 条，警告 {entry.WarningIssueCount} 条；最短休息 {FormatRestMinutes(entry.ShortestRestMinutes)}。",
            Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(100, 116, 139)),
            TextWrapping = TextWrapping.Wrap
        });

        foreach (var appearance in entry.Appearances)
        {
            var hasConflict = appearance.ConflictSeverity is CrossEventConflictSeverity.Severe or CrossEventConflictSeverity.Warning;
            var card = new Border
            {
                Background = hasConflict
                    ? ThemeBrush("AppErrorCardBackgroundBrush", Color.FromRgb(254, 242, 242))
                    : ThemeBrush("AppSurfaceBrush", Color.FromRgb(255, 255, 255)),
                BorderBrush = hasConflict
                    ? ThemeBrush("AppErrorCardBorderBrush", Color.FromRgb(220, 38, 38))
                    : ThemeBrush("AppButtonBorderBrush", Color.FromRgb(199, 210, 228)),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Avalonia.Thickness(10),
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = appearance
            };
            ToolTip.SetTip(card, "点击定位到多项目赛程窗口中的这场比赛。");
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock
            {
                Text = $"{appearance.DayLabel} {appearance.TimeRange} · {appearance.Court} · {appearance.EventName}",
                FontWeight = FontWeight.Bold,
                Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(43, 20, 95)),
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{appearance.Phase} {appearance.MatchName} · {appearance.Status}",
                Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(100, 116, 139)),
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"本方：{appearance.SideText}    对方：{appearance.OpponentText}",
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(appearance.ConflictSummary))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = appearance.ConflictSummary,
                    Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28)),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            card.Child = stack;
            card.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                _ = FocusCrossEventPlayerAppearanceAsync(appearance);
            };
            detailStack.Children.Add(card);
        }
    }


    private int GetCrossEventMinimumRestMinutes()
    {
        if (!int.TryParse(CrossEventRestMinutesBox.Text?.Trim(), out var minutes) || minutes < 0)
        {
            throw new DrawValidationException("跨项目最小休息间隔必须是大于或等于 0 的整数。");
        }

        return minutes;
    }

    private int? GetCrossEventRefereeCount()
    {
        return ParseOptionalPositiveInt(CrossEventRefereeCountBox.Text, "裁判人数");
    }

    private CrossEventSchedulingOptions ApplyCrossEventRefereeCount(CrossEventSchedulingOptions options)
    {
        return options with
        {
            RefereeCount = GetCrossEventRefereeCount()
        };
    }

    private CrossEventSchedulingStrategy GetCrossEventSchedulingStrategy()
    {
        if (CrossEventSchedulingStrategyBox.SelectedItem is ComboBoxItem item
            && item.Tag is not null
            && Enum.TryParse<CrossEventSchedulingStrategy>(item.Tag.ToString(), out var strategy))
        {
            return strategy;
        }

        return CrossEventSchedulingStrategy.BalancedRelaxed;
    }

    private static string GetCrossEventSchedulingStrategyName(CrossEventSchedulingStrategy strategy)
    {
        return strategy switch
        {
            CrossEventSchedulingStrategy.Compact => "紧凑完成",
            CrossEventSchedulingStrategy.FinalsDayFriendly => "决赛日友好",
            CrossEventSchedulingStrategy.Custom => "自定义",
            _ => "均衡宽松"
        };
    }

    private bool RunCrossEventScheduling(
        CrossEventSchedulingStrategy strategy,
        CrossEventCustomAnchor anchor,
        string successPrefix,
        bool rollbackOnFailure)
    {
        if (_runningCrossEventScheduling)
        {
            return false;
        }

        if (_crossEventBaseScheduleBoard is null)
        {
            SetStatus("请先加载多项目赛程。", isError: true);
            return false;
        }

        var selectedDay = GetSelectedCrossEventDayLabel();
        var version = ++_crossEventSchedulingVersion;
        _runningCrossEventScheduling = true;
        try
        {
            var baseBoard = RebuildCrossEventBaseBoardForCurrentRest();
            var options = strategy == CrossEventSchedulingStrategy.Custom
                ? BuildAnchoredCustomSchedulingOptions(baseBoard, anchor)
                : _crossEventConflictWorkflow.CreateSchedulingOptions(baseBoard, strategy);
            options = ApplyCrossEventRefereeCount(options);
            var result = _crossEventConflictWorkflow.AutoAdjustScheduleBoard(baseBoard, options);
            if (version != _crossEventSchedulingVersion)
            {
                return false;
            }

            if (result.RemainingBlockingConflictItemCount > 0)
            {
                RestoreLastAcceptedCrossEventSchedule(rollbackOnFailure);
                var failureMessage = BuildCrossEventSchedulingFailureMessage(result, anchor);
                SetStatus(
                    $"当前负载目标不可行，已回滚到上一可行排程；仍有 {result.RemainingBlockingConflictItemCount} 张硬冲突卡片。",
                    isError: true);
                _ = ShowCrossEventSchedulingFailureAsync(failureMessage);
                return false;
            }

            _crossEventScheduleBoard = result.Board;
            _crossEventSchedulingOptions = _crossEventScheduleBoard.SchedulingOptions ?? options;
            _crossEventLastAcceptedBoard = _crossEventScheduleBoard;
            _crossEventLastAcceptedOptions = _crossEventSchedulingOptions;
            ClearCrossEventScheduleUndoStack();
            if (strategy == CrossEventSchedulingStrategy.Custom && _crossEventRecommendedCustomOptions is null)
            {
                _crossEventRecommendedCustomOptions = _crossEventSchedulingOptions with
                {
                    Strategy = CrossEventSchedulingStrategy.Custom
                };
            }

            RebuildCrossEventCustomSchedulingControls();
            RefreshCrossEventScheduleBoard(selectedDay);
            RefreshCrossEventBoardWindow(selectedDay);
            PreviewTabs.SelectedItem = CrossEventPreviewTab;
            var message =
                $"{BuildCrossEventStatus(successPrefix, _crossEventScheduleBoard)} 策略：{GetCrossEventSchedulingStrategyName(strategy)}，移动 {result.MovedCount} 场，硬冲突 0。";
            if (anchor.Kind != CrossEventCustomAnchorKind.None)
            {
                message += $" 锚点：{anchor.Describe()}。";
            }

            if (result.Messages.Count > 0)
            {
                message += $" {string.Join("；", result.Messages.Take(3))}";
            }

            SetStatus(message, isWarning: _crossEventScheduleBoard.Report.NoticeCount > 0);
            return true;
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
        {
            RestoreLastAcceptedCrossEventSchedule(rollbackOnFailure);
            SetStatus($"{anchor.Describe()}无法生成可行赛程：{ex.Message}", isError: true);
            return false;
        }
        finally
        {
            _pendingCrossEventCustomAnchor = CrossEventCustomAnchor.None;
            _runningCrossEventScheduling = false;
        }
    }

    private static string BuildCrossEventSchedulingFailureMessage(
        CrossEventScheduleAutoAdjustResult result,
        CrossEventCustomAnchor anchor)
    {
        var lines = new List<string>
        {
            "当前负载目标不可行，系统已回滚到上一可行排程。",
            $"触发项：{anchor.Describe()}。",
            $"仍有 {result.RemainingBlockingConflictItemCount} 张硬冲突卡片。"
        };

        var blockers = result.Board.Items
            .Where(item => item.IsBlockingConflict)
            .OrderBy(item => item.DayLabel, StringComparer.Ordinal)
            .ThenBy(item => item.StartTime)
            .ThenBy(item => item.Court, StringComparer.Ordinal)
            .ThenBy(item => item.EventName, StringComparer.Ordinal)
            .Take(5)
            .ToList();

        lines.Add("");
        lines.Add("前几条阻塞原因：");
        if (blockers.Count > 0)
        {
            for (var index = 0; index < blockers.Count; index++)
            {
                var item = blockers[index];
                lines.Add(
                    $"{index + 1}. {item.EventName} · {item.MatchName}：{item.DayLabel} {item.TimeRange} {item.Court}，{item.ConflictSummary}");
            }
        }
        else if (result.Messages.Count > 0)
        {
            for (var index = 0; index < Math.Min(5, result.Messages.Count); index++)
            {
                lines.Add($"{index + 1}. {result.Messages[index]}");
            }
        }
        else
        {
            lines.Add("1. 当前场地、日期或阶段目标组合无法满足全部硬约束。");
        }

        lines.Add("");
        lines.Add("建议：把当前滑杆调回推荐可行区间，或增加比赛日/场地、放宽休息间隔后再试。");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task ShowCrossEventSchedulingFailureAsync(string message)
    {
        if (_showingCrossEventSchedulingFailureDialog)
        {
            return;
        }

        _showingCrossEventSchedulingFailureDialog = true;
        try
        {
            await ShowInfoAsync("当前负载目标不可行", message);
        }
        finally
        {
            _showingCrossEventSchedulingFailureDialog = false;
        }
    }

    private CrossEventScheduleBoard RebuildCrossEventBaseBoardForCurrentRest()
    {
        var minimumRestMinutes = GetCrossEventMinimumRestMinutes();
        if (_crossEventBaseScheduleBoard is null)
        {
            throw new DrawValidationException("请先加载多项目赛程。");
        }

        if (_crossEventBaseScheduleBoard.MinimumRestMinutes == minimumRestMinutes)
        {
            return _crossEventBaseScheduleBoard;
        }

        _crossEventBaseScheduleBoard = _crossEventConflictWorkflow.RebuildScheduleBoard(
            _crossEventBaseScheduleBoard,
            minimumRestMinutes,
            _crossEventSchedulingOptions);
        _crossEventRecommendedCustomOptions = null;
        return _crossEventBaseScheduleBoard;
    }

    private void RestoreLastAcceptedCrossEventSchedule(bool rollbackOnFailure)
    {
        if (!rollbackOnFailure || _crossEventLastAcceptedBoard is null)
        {
            return;
        }

        _crossEventScheduleBoard = _crossEventLastAcceptedBoard;
        _crossEventSchedulingOptions = _crossEventLastAcceptedOptions ?? _crossEventLastAcceptedBoard.SchedulingOptions;
        ClearCrossEventScheduleUndoStack();
        RebuildCrossEventCustomSchedulingControls();
        RefreshCrossEventScheduleBoard(GetSelectedCrossEventDayLabel());
        RefreshCrossEventBoardWindow(GetSelectedCrossEventDayLabel());
    }

    private void EnsureCrossEventCustomRecommendation(bool force = false)
    {
        if (!force && _crossEventRecommendedCustomOptions is not null)
        {
            return;
        }

        var baseBoard = _crossEventBaseScheduleBoard ?? _crossEventScheduleBoard;
        if (baseBoard is null)
        {
            return;
        }

        var current = _crossEventLastAcceptedOptions ?? _crossEventSchedulingOptions;
        _crossEventRecommendedCustomOptions = current is not null
            ? current with { Strategy = CrossEventSchedulingStrategy.Custom }
            : _crossEventConflictWorkflow.CreateSchedulingOptions(baseBoard, CrossEventSchedulingStrategy.BalancedRelaxed) with
            {
                Strategy = CrossEventSchedulingStrategy.Custom
            };
    }

    private CrossEventSchedulingOptions BuildAnchoredCustomSchedulingOptions(
        CrossEventScheduleBoard baseBoard,
        CrossEventCustomAnchor anchor)
    {
        EnsureCrossEventCustomRecommendation();
        var recommendation = _crossEventRecommendedCustomOptions
            ?? _crossEventConflictWorkflow.CreateSchedulingOptions(baseBoard, CrossEventSchedulingStrategy.BalancedRelaxed) with
            {
                Strategy = CrossEventSchedulingStrategy.Custom
            };
        var current = (_crossEventLastAcceptedOptions?.Strategy == CrossEventSchedulingStrategy.Custom
                ? _crossEventLastAcceptedOptions
                : _crossEventSchedulingOptions?.Strategy == CrossEventSchedulingStrategy.Custom
                    ? _crossEventSchedulingOptions
                    : recommendation)
            ?? recommendation;

        return anchor.Kind switch
        {
            CrossEventCustomAnchorKind.Recommended => recommendation,
            CrossEventCustomAnchorKind.DayLoad when !string.IsNullOrWhiteSpace(anchor.DayLabel) => current with
            {
                Strategy = CrossEventSchedulingStrategy.Custom,
                DayLoadTargets = BuildAnchoredDayLoadTargets(baseBoard, recommendation, anchor.DayLabel)
            },
            CrossEventCustomAnchorKind.StageWave when !string.IsNullOrWhiteSpace(anchor.DayLabel) => current with
            {
                Strategy = CrossEventSchedulingStrategy.Custom,
                SynchronizeStageWaves = CrossEventStageWaveBox.IsChecked == true,
                StageWaveTargets = BuildAnchoredStageWaveTargets(baseBoard, recommendation, anchor.DayLabel)
            },
            CrossEventCustomAnchorKind.StageWaveEnabled => current with
            {
                Strategy = CrossEventSchedulingStrategy.Custom,
                SynchronizeStageWaves = CrossEventStageWaveBox.IsChecked == true
            },
            _ => current with { Strategy = CrossEventSchedulingStrategy.Custom }
        };
    }

    private IReadOnlyList<CrossEventDayLoadTarget> BuildAnchoredDayLoadTargets(
        CrossEventScheduleBoard board,
        CrossEventSchedulingOptions recommendation,
        string anchorDayLabel)
    {
        var orderedDays = board.Days.OrderBy(day => day.DayLabel, StringComparer.Ordinal).ToList();
        if (!_crossEventDayLoadSliders.TryGetValue(anchorDayLabel, out var anchorSlider))
        {
            return recommendation.DayLoadTargets;
        }

        var capacityByDay = orderedDays.ToDictionary(
            day => day.DayLabel,
            day => ScheduleResourceCalculator.CalculateDayCapacityMinutes(
                day,
                GetCrossEventRefereeCount(),
                slotMinutes: Math.Max(1, day.SlotMinutes)),
            StringComparer.Ordinal);
        var recommendedMinutes = orderedDays.Sum(day =>
            capacityByDay[day.DayLabel] * (recommendation.DayLoadTargets.FirstOrDefault(target => target.DayLabel == day.DayLabel)?.TargetUtilization ?? 0.6));
        var anchorCapacity = capacityByDay[anchorDayLabel];
        var anchorTarget = Math.Clamp(anchorSlider.Value / 100d, 0.05, 1.0);
        var anchorMinutes = anchorCapacity * anchorTarget;
        var remainingMinutes = Math.Max(0, recommendedMinutes - anchorMinutes);
        var remainingDays = orderedDays
            .Where(day => !string.Equals(day.DayLabel, anchorDayLabel, StringComparison.Ordinal))
            .ToList();
        var weights = remainingDays.ToDictionary(
            day => day.DayLabel,
            day => Math.Max(
                1d,
                capacityByDay[day.DayLabel]
                * (recommendation.DayLoadTargets.FirstOrDefault(target => target.DayLabel == day.DayLabel)?.TargetUtilization ?? 0.6)),
            StringComparer.Ordinal);
        var assigned = AllocateTargetMinutes(remainingDays, capacityByDay, weights, remainingMinutes);

        var result = new List<CrossEventDayLoadTarget>();
        foreach (var day in orderedDays)
        {
            var target = string.Equals(day.DayLabel, anchorDayLabel, StringComparison.Ordinal)
                ? anchorTarget
                : assigned.TryGetValue(day.DayLabel, out var minutes)
                    ? minutes / capacityByDay[day.DayLabel]
                    : recommendation.DayLoadTargets.FirstOrDefault(item => item.DayLabel == day.DayLabel)?.TargetUtilization ?? 0.6;
            result.Add(new CrossEventDayLoadTarget(day.DayLabel, target, Math.Min(1.0, target + 0.15)));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, double> AllocateTargetMinutes(
        IReadOnlyList<CrossEventScheduleBoardDay> days,
        IReadOnlyDictionary<string, int> capacityByDay,
        IReadOnlyDictionary<string, double> weights,
        double totalMinutes)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var pending = days.ToList();
        var remaining = Math.Max(0, totalMinutes);
        while (pending.Count > 0)
        {
            var totalWeight = pending.Sum(day => weights.TryGetValue(day.DayLabel, out var weight) ? Math.Max(1d, weight) : 1d);
            var lockedAny = false;
            foreach (var day in pending.ToList())
            {
                var capacity = Math.Max(1, capacityByDay[day.DayLabel]);
                var min = capacity * 0.05;
                var max = capacity;
                var weight = weights.TryGetValue(day.DayLabel, out var dayWeight) ? Math.Max(1d, dayWeight) : 1d;
                var proposed = totalWeight <= 0 ? remaining / pending.Count : remaining * weight / totalWeight;
                if (proposed < min || proposed > max)
                {
                    var clamped = Math.Clamp(proposed, min, max);
                    result[day.DayLabel] = clamped;
                    remaining -= clamped;
                    pending.Remove(day);
                    lockedAny = true;
                }
            }

            if (!lockedAny)
            {
                foreach (var day in pending)
                {
                    var weight = weights.TryGetValue(day.DayLabel, out var dayWeight) ? Math.Max(1d, dayWeight) : 1d;
                    result[day.DayLabel] = totalWeight <= 0 ? remaining / pending.Count : remaining * weight / totalWeight;
                }

                break;
            }

            remaining = Math.Max(0, remaining);
        }

        return result;
    }

    private IReadOnlyList<CrossEventStageWaveTarget> BuildAnchoredStageWaveTargets(
        CrossEventScheduleBoard board,
        CrossEventSchedulingOptions recommendation,
        string anchorDayLabel)
    {
        var orderedDays = board.Days.OrderBy(day => day.DayLabel, StringComparer.Ordinal).ToList();
        if (!_crossEventStageWaveSliders.TryGetValue(anchorDayLabel, out var anchorSlider))
        {
            return recommendation.StageWaveTargets;
        }

        var baseTargets = BuildStageWaveTargetLookup(orderedDays, recommendation);
        var anchorIndex = orderedDays.FindIndex(day => string.Equals(day.DayLabel, anchorDayLabel, StringComparison.Ordinal));
        if (anchorIndex < 0)
        {
            return recommendation.StageWaveTargets;
        }

        var anchorBase = baseTargets[anchorDayLabel];
        var anchorTarget = Math.Clamp(anchorSlider.Value / 100d, 0.05, 0.95);
        var result = new List<CrossEventStageWaveTarget>();
        for (var index = 0; index < orderedDays.Count; index++)
        {
            var day = orderedDays[index];
            double value;
            if (index == orderedDays.Count - 1)
            {
                value = 1.0;
            }
            else if (index == anchorIndex)
            {
                value = anchorTarget;
            }
            else if (index < anchorIndex)
            {
                value = anchorBase <= 0.05
                    ? anchorTarget * (index + 1d) / (anchorIndex + 1d)
                    : anchorTarget * (baseTargets[day.DayLabel] / anchorBase);
            }
            else
            {
                var denominator = Math.Max(0.05, 1.0 - anchorBase);
                value = anchorTarget + ((baseTargets[day.DayLabel] - anchorBase) / denominator * (1.0 - anchorTarget));
            }

            result.Add(new CrossEventStageWaveTarget(day.DayLabel, value));
        }

        return NormalizeStageWaveTargets(result);
    }

    private static Dictionary<string, double> BuildStageWaveTargetLookup(
        IReadOnlyList<CrossEventScheduleBoardDay> orderedDays,
        CrossEventSchedulingOptions options)
    {
        return orderedDays.Select((day, index) => (day, index)).ToDictionary(
            item => item.day.DayLabel,
            item => options.StageWaveTargets.FirstOrDefault(target => target.DayLabel == item.day.DayLabel)?.CumulativeProgress
                ?? ((item.index + 1d) / orderedDays.Count),
            StringComparer.Ordinal);
    }

    private static List<CrossEventStageWaveTarget> NormalizeStageWaveTargets(
        IReadOnlyList<CrossEventStageWaveTarget> targets)
    {
        var result = new List<CrossEventStageWaveTarget>();
        var previous = 0.05;
        for (var index = 0; index < targets.Count; index++)
        {
            var isLast = index == targets.Count - 1;
            var remaining = targets.Count - index - 1;
            var maxValue = Math.Min(0.95, 1.0 - (remaining * 0.05));
            var value = isLast
                ? 1.0
                : Math.Clamp(targets[index].CumulativeProgress, previous + 0.05, maxValue);
            result.Add(targets[index] with { CumulativeProgress = value });
            previous = value;
        }

        return result;
    }

    private void RebuildCrossEventCustomSchedulingControls()
    {
        if (_crossEventScheduleBoard is null)
        {
            CrossEventCustomDayLoadPanel.Children.Clear();
            CrossEventStageWavePanel.Children.Clear();
            return;
        }

        _updatingCrossEventCustomControls = true;
        try
        {
            var strategy = GetCrossEventSchedulingStrategy();
            CrossEventCustomSchedulingPanel.IsVisible = strategy == CrossEventSchedulingStrategy.Custom;
            var options = _crossEventSchedulingOptions
                ?? _crossEventConflictWorkflow.CreateSchedulingOptions(_crossEventScheduleBoard, CrossEventSchedulingStrategy.BalancedRelaxed);
            CrossEventCustomHintText.Text =
                "这些参数来自系统按“均衡宽松”推导的当前最优值；微调后会全局重排，可能跨天溢出或回填。";
            RebuildCrossEventDayLoadControls(options);
            RebuildCrossEventStageWaveControls(options);
            UpdateCrossEventCustomLabels();
        }
        finally
        {
            _updatingCrossEventCustomControls = false;
        }
    }

    private void RebuildCrossEventDayLoadControls(CrossEventSchedulingOptions options)
    {
        CrossEventCustomDayLoadPanel.Children.Clear();
        _crossEventDayLoadSliders.Clear();
        _crossEventDayLoadLabels.Clear();
        _crossEventDayLoadRecommendedRanges.Clear();
        EnsureCrossEventCustomRecommendation();
        var recommendedTargets = (_crossEventRecommendedCustomOptions ?? options)
            .DayLoadTargets
            .ToDictionary(target => target.DayLabel, target => target.TargetUtilization, StringComparer.Ordinal);
        foreach (var day in _crossEventScheduleBoard!.Days.OrderBy(day => day.DayLabel, StringComparer.Ordinal))
        {
            var target = options.DayLoadTargets.FirstOrDefault(item => item.DayLabel == day.DayLabel)?.TargetUtilization ?? 0.6;
            var recommended = recommendedTargets.TryGetValue(day.DayLabel, out var recommendedTarget)
                ? recommendedTarget
                : target;
            var recommendedPercent = Math.Round(recommended * 100);
            var rangeMin = Math.Clamp(recommendedPercent - 15, 10, 100);
            var rangeMax = Math.Clamp(recommendedPercent + 15, 10, 100);
            var label = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap
            };
            var slider = new Slider
            {
                Minimum = 10,
                Maximum = 100,
                TickFrequency = 5,
                IsSnapToTickEnabled = true,
                Value = Math.Round(target * 20) * 5,
                Tag = new CrossEventCustomSliderTag(CrossEventCustomAnchorKind.DayLoad, day.DayLabel)
            };
            ToolTip.SetTip(slider, $"{day.DayLabel} 推荐可行区间：{rangeMin:0}%-{rangeMax:0}%；系统建议 {recommendedPercent:0}%。");
            slider.ValueChanged += CrossEventCustomSlider_ValueChanged;
            _crossEventDayLoadLabels[day.DayLabel] = label;
            _crossEventDayLoadSliders[day.DayLabel] = slider;
            _crossEventDayLoadRecommendedRanges[day.DayLabel] = (rangeMin, rangeMax, recommendedPercent);
            CrossEventCustomDayLoadPanel.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    label,
                    slider
                }
            });
        }
    }

    private void RebuildCrossEventStageWaveControls(CrossEventSchedulingOptions options)
    {
        CrossEventStageWavePanel.Children.Clear();
        _crossEventStageWaveSliders.Clear();
        _crossEventStageWaveLabels.Clear();
        CrossEventStageWaveBox.IsChecked = options.SynchronizeStageWaves;
        var orderedDays = _crossEventScheduleBoard!.Days.OrderBy(day => day.DayLabel, StringComparer.Ordinal).ToList();
        for (var index = 0; index < orderedDays.Count - 1; index++)
        {
            var day = orderedDays[index];
            var target = options.StageWaveTargets.FirstOrDefault(item => item.DayLabel == day.DayLabel)?.CumulativeProgress
                ?? ((index + 1d) / orderedDays.Count);
            var label = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap
            };
            var slider = new Slider
            {
                Minimum = 10,
                Maximum = 95,
                TickFrequency = 5,
                IsSnapToTickEnabled = true,
                Value = Math.Round(target * 20) * 5,
                Tag = new CrossEventCustomSliderTag(CrossEventCustomAnchorKind.StageWave, day.DayLabel)
            };
            slider.ValueChanged += CrossEventCustomSlider_ValueChanged;
            _crossEventStageWaveLabels[day.DayLabel] = label;
            _crossEventStageWaveSliders[day.DayLabel] = slider;
            CrossEventStageWavePanel.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    label,
                    slider
                }
            });
        }
    }

    private void UpdateCrossEventCustomLabels()
    {
        foreach (var pair in _crossEventDayLoadSliders)
        {
            if (_crossEventDayLoadLabels.TryGetValue(pair.Key, out var label))
            {
                label.Text = BuildCrossEventDayLoadLabel(pair.Key, pair.Value.Value);
            }
        }

        foreach (var pair in _crossEventStageWaveSliders)
        {
            if (_crossEventStageWaveLabels.TryGetValue(pair.Key, out var label))
            {
                label.Text = $"{pair.Key} 结束前：预计完成全局阶段进度 {Math.Round(pair.Value.Value)}%";
            }
        }
    }

    private string BuildCrossEventDayLoadLabel(string dayLabel, double value)
    {
        var rounded = Math.Round(value);
        if (!_crossEventDayLoadRecommendedRanges.TryGetValue(dayLabel, out var range))
        {
            return $"{dayLabel}：目标负载率 {rounded}%";
        }

        var text = $"{dayLabel}：目标负载率 {rounded}%（推荐可行区间 {range.Min:0}%-{range.Max:0}%，系统建议 {range.Recommended:0}%）";
        if (rounded < range.Min || rounded > range.Max)
        {
            text += "，已超出推荐区间，可能不可行";
        }

        return text;
    }

    private void ApplyScheduleCourtPreset()
    {
        if (ScheduleVenueBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var tag = item.Tag?.ToString();
        if (tag == "Custom")
        {
            return;
        }

        var courts = tag switch
        {
            "YuehaiEast" => BuildYuehaiEastCourts(),
            "Zhikuai" => BuildZhikuaiCourts(),
            "Zhichang" => BuildZhichangCourts(),
            _ => BuildYuehaiEastCourts().Concat(BuildZhikuaiCourts()).Concat(BuildZhichangCourts()).ToList()
        };
        ScheduleCourtsBox.Text = string.Join("，", courts);
    }

    private static IReadOnlyList<string> BuildYuehaiEastCourts()
    {
        return "BC"
            .SelectMany(prefix => Enumerable.Range(1, 8).Select(index => $"{prefix}{index}"))
            .ToList();
    }

    private static IReadOnlyList<string> BuildZhikuaiCourts()
    {
        return Enumerable.Range(1, 12)
            .Select(index => $"至快{index}")
            .ToList();
    }

    private static IReadOnlyList<string> BuildZhichangCourts()
    {
        return Enumerable.Range(1, 10)
            .Select(index => $"至畅{index}")
            .ToList();
    }


    private void RefreshCrossEventScheduleBoard(string? preferredDayLabel = null)
    {
        if (_crossEventScheduleBoard is null)
        {
            CrossEventBoardSummaryText.Text = "尚未加载多项目赛程。请先在左侧选择至少两个赛事存档。";
            UpdateCrossEventReminderButton();
            CrossEventDayBox.ItemsSource = null;
            CrossEventScheduleBoardGrid.Children.Clear();
            CrossEventScheduleBoardGrid.RowDefinitions.Clear();
            CrossEventScheduleBoardGrid.ColumnDefinitions.Clear();
            return;
        }

        var dayLabels = _crossEventScheduleBoard.Days.Select(day => day.DayLabel).ToList();
        CrossEventDayBox.SelectionChanged -= CrossEventDayBox_SelectionChanged;
        CrossEventDayBox.ItemsSource = dayLabels;
        var selectedDay = !string.IsNullOrWhiteSpace(preferredDayLabel) && dayLabels.Contains(preferredDayLabel)
            ? preferredDayLabel
            : GetSelectedCrossEventDayLabel();
        if (string.IsNullOrWhiteSpace(selectedDay) || !dayLabels.Contains(selectedDay))
        {
            selectedDay = dayLabels.FirstOrDefault();
        }

        CrossEventDayBox.SelectedItem = selectedDay;
        CrossEventDayBox.SelectionChanged += CrossEventDayBox_SelectionChanged;
        UpdateCrossEventSummary();
        RenderCrossEventScheduleBoard(selectedDay);
    }

    private void UpdateCrossEventSummary()
    {
        if (_crossEventScheduleBoard is null)
        {
            return;
        }

        var changedText = _crossEventScheduleBoard.HasUnsavedChanges ? " · 有未保存调整" : "";
        CrossEventBoardSummaryText.Text = BuildCrossEventBoardSummary(_crossEventScheduleBoard, _crossEventBoardZoom, changedText, includeZoom: false);
        UpdateCrossEventReminderButton();
    }

    private void UpdateCrossEventReminderButton()
    {
        if (_crossEventScheduleBoard is null)
        {
            CrossEventReminderButton.Content = "查看提醒";
            CrossEventReminderButton.IsEnabled = false;
            return;
        }

        var count = _crossEventScheduleBoard.Report.Issues.Count;
        CrossEventReminderButton.IsEnabled = true;
        CrossEventReminderButton.Content = count > 0 ? $"提醒 {count}" : "查看提醒";
    }

    private static string BuildCrossEventBoardSummary(
        CrossEventScheduleBoard board,
        double zoom,
        string changedText = "",
        bool includeZoom = true)
    {
        var zoomText = includeZoom ? $"；缩放 {Math.Round(zoom * 100)}%" : "";
        var refereeCount = board.SchedulingOptions?.RefereeCount;
        var refereeText = refereeCount is > 0
            ? $"，裁判 {refereeCount.Value} 人"
            : "";
        var qualityText = BuildScheduleQualityInline(board.QualityReport);
        var qualitySuffix = string.IsNullOrWhiteSpace(qualityText)
            ? ""
            : $"，质量 {qualityText}";
        return $"项目 {board.Sources.Count}，场次 {board.Items.Count}，兼项 {board.MultiEventPlayerCount}；"
               + $"严重 {board.Report.SevereCount}，警告 {board.Report.WarningCount}，提醒/推演 {board.Report.NoticeCount}，"
               + $"冲突卡 {board.BlockingConflictItemCount}{refereeText}{qualitySuffix}{zoomText}{changedText}";
    }

    private void RenderCrossEventScheduleBoard(string? dayLabel)
    {
        RenderCrossEventScheduleBoard(CrossEventScheduleBoardGrid, dayLabel, _crossEventBoardZoom);
    }

    private void RenderCrossEventScheduleBoard(Grid targetGrid, string? dayLabel, double zoom)
    {
        RenderScheduleBoardView(targetGrid, BuildCrossEventScheduleBoardView(), dayLabel, zoom);
    }

    private void AddCrossEventEmptyText(Grid targetGrid, string text, double zoom)
    {
        targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        targetGrid.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(100, 116, 139)),
            FontSize = ScaleCrossEventFont(13, zoom),
            Margin = new Avalonia.Thickness(12)
        });
    }

    private void AddCrossEventHeaderCell(Grid targetGrid, string text, int row, int column, double zoom)
    {
        var border = new Border
        {
            Background = ThemeBrush("AppTableHeaderBackgroundBrush", Color.FromRgb(237, 225, 252)),
            BorderBrush = ThemeBrush("AppTableHeaderBorderBrush", Color.FromRgb(216, 199, 244)),
            BorderThickness = new Avalonia.Thickness(0, 0, 1, 1),
            Padding = new Avalonia.Thickness(ScaleCrossEvent(10, zoom), ScaleCrossEvent(8, zoom)),
            Child = new TextBlock
            {
                Text = text,
                FontSize = ScaleCrossEventFont(13, zoom),
                FontWeight = FontWeight.Bold,
                Foreground = ThemeBrush("AppTableHeaderTextBrush", Color.FromRgb(50, 17, 109)),
                TextAlignment = TextAlignment.Center
            }
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        targetGrid.Children.Add(border);
    }

    private void AddCrossEventTimeCell(Grid targetGrid, TimeOnly slot, int row, double zoom)
    {
        var border = new Border
        {
            Background = ThemeBrush("AppSurfaceMutedBrush", Color.FromRgb(248, 250, 252)),
            BorderBrush = ThemeBrush("AppSoftBorderBrush", Color.FromRgb(226, 232, 240)),
            BorderThickness = new Avalonia.Thickness(0, 0, 1, 1),
            Padding = new Avalonia.Thickness(ScaleCrossEvent(10, zoom), ScaleCrossEvent(14, zoom)),
            Child = new TextBlock
            {
                Text = slot.ToString("HH:mm"),
                FontSize = ScaleCrossEventFont(13, zoom),
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeBrush("AppTextBrush", Color.FromRgb(17, 24, 39)),
                TextAlignment = TextAlignment.Center
            }
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, 0);
        targetGrid.Children.Add(border);
    }

    private string? GetSelectedCrossEventDayLabel()
    {
        return CrossEventDayBox.SelectedItem?.ToString();
    }

    private static string BuildCrossEventStatus(string prefix, CrossEventScheduleBoard board)
    {
        var refereeCount = board.SchedulingOptions?.RefereeCount;
        var refereeText = refereeCount is > 0
            ? $"裁判 {refereeCount.Value} 人，"
            : "";
        var qualityText = BuildScheduleQualityInline(board.QualityReport);
        var qualitySuffix = string.IsNullOrWhiteSpace(qualityText)
            ? ""
            : $" 质量：{qualityText}。";
        return $"{prefix}：兼项选手 {board.MultiEventPlayerCount} 人，严重 {board.Report.SevereCount} 条，警告 {board.Report.WarningCount} 条，"
               + $"同日/负荷推演提醒 {board.Report.NoticeCount} 条，{refereeText}冲突卡片 {board.BlockingConflictItemCount} 张。{qualitySuffix}";
    }
}
