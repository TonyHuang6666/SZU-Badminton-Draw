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
    private void OpenScheduleBoardWindow_Click(object? sender, RoutedEventArgs e)
    {
        EnsureScheduleBoardWindowOpen();
    }

    private bool EnsureScheduleBoardWindowOpen(string? preferredDayLabel = null)
    {
        if (_latestSchedule is null)
        {
            SetStatus("请先生成或打开赛程。", isError: true);
            return false;
        }

        if (_scheduleBoardWindow is { IsVisible: true })
        {
            RefreshScheduleBoardWindow(preferredDayLabel);
            _scheduleBoardWindow.Activate();
            return true;
        }

        _scheduleBoardWindowZoom = 1.0;
        _scheduleBoardWindowDayBox = new ComboBox { Width = 180 };
        _scheduleBoardWindowDayBox.SelectionChanged += ScheduleBoardWindowDayBox_SelectionChanged;
        _scheduleBoardWindowSummaryText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            TextWrapping = TextWrapping.Wrap
        };
        _scheduleBoardWindowGrid = new Grid
        {
            Margin = new Avalonia.Thickness(10),
            MinWidth = 980,
            MinHeight = 560
        };

        _scheduleBoardWindow = new Window
        {
            Title = "赛程安排窗口",
            Width = 1420,
            Height = 880,
            MinWidth = 980,
            MinHeight = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = BuildScheduleBoardWindowContent()
        };
        _scheduleBoardWindow.Closed += (_, _) =>
        {
            _scheduleBoardWindow = null;
            _scheduleBoardWindowDayBox = null;
            _scheduleBoardWindowSummaryText = null;
            _scheduleBoardWindowGrid = null;
            _scheduleBoardWindowScrollViewer = null;
            _scheduleBoardWindowUndoButton = null;
            _scheduleBoardWindowDayTabs = null;
            _scheduleBoardWindowDayPickerPanel = null;
            _scheduleBoardWindowMatchCards.Clear();
        };
        RefreshScheduleBoardWindow(preferredDayLabel);
        _scheduleBoardWindow.Show(this);
        return true;
    }

    private void ShowScheduleConstraintDetails_Click(object? sender, RoutedEventArgs e)
    {
        if (_latestSchedule is null)
        {
            SetStatus("请先生成或打开赛程。", isError: true);
            return;
        }

        _latestScheduleConstraintReport ??= _scheduleConstraintAnalyzer.Analyze(_latestSchedule);
        UpdateScheduleConstraintButton();
        if (_scheduleConstraintWindow is { IsVisible: true })
        {
            _scheduleConstraintWindow.Close();
        }

        var dialog = new Window
        {
            Title = "高级约束提醒",
            Width = 860,
            Height = 640,
            MinWidth = 680,
            MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = BuildScheduleConstraintDialogContent(_latestScheduleConstraintReport)
        };
        _scheduleConstraintWindow = dialog;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_scheduleConstraintWindow, dialog))
            {
                _scheduleConstraintWindow = null;
            }
        };
        dialog.Show(this);
    }

    private Control BuildScheduleConstraintDialogContent(ScheduleConstraintReport report)
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Avalonia.Thickness(16)
        };
        var headerStack = new StackPanel
        {
            Spacing = 10,
        };
        headerStack.Children.Add(new TextBlock
        {
            Text = $"高级约束提醒（{report.Rules.ProfileName}）",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(40, 16, 78))
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = $"已确定风险 {report.ConfirmedCount}，下一轮接续 {report.DirectDependencyCount}，推演风险 {report.SpeculativeCount}。严重/警告/提醒只作为卡片颜色和排序依据；点击卡片可定位到赛程安排窗口。",
            Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(100, 116, 139)),
            TextWrapping = TextWrapping.Wrap
        });

        var filterStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        headerStack.Children.Add(filterStack);
        var categoryStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        headerStack.Children.Add(categoryStack);
        root.Children.Add(headerStack);

        var issueStack = new StackPanel { Spacing = 10 };
        var issueScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
            Content = issueStack
        };
        Grid.SetRow(issueScroll, 1);
        root.Children.Add(issueScroll);

        var filterButtons = new List<Button>();
        var selectedScope = report.ConfirmedCount > 0
            ? ScheduleConstraintIssueScope.Confirmed
            : report.DirectDependencyCount > 0
                ? ScheduleConstraintIssueScope.DirectDependency
                : ScheduleConstraintIssueScope.Speculative;

        static int CountIssuesByScope(ScheduleConstraintReport report, ScheduleConstraintIssueScope scope)
        {
            return scope switch
            {
                ScheduleConstraintIssueScope.Confirmed => report.ConfirmedCount,
                ScheduleConstraintIssueScope.DirectDependency => report.DirectDependencyCount,
                _ => report.SpeculativeCount
            };
        }

        void RenderIssues(ScheduleConstraintIssueScope scope)
        {
            selectedScope = scope;
            foreach (var button in filterButtons)
            {
                var isSelected = Equals(button.Tag, selectedScope);
                var count = CountIssuesByScope(report, (ScheduleConstraintIssueScope)button.Tag!);
                button.IsEnabled = count > 0;
                button.Opacity = count > 0 || isSelected ? 1 : 0.48;
                button.Background = isSelected
                    ? ThemeBrush("AppInfoCardBackgroundBrush", Color.FromRgb(239, 246, 255))
                    : ThemeBrush("AppButtonBackgroundBrush", Color.FromRgb(255, 255, 255));
                button.BorderBrush = isSelected
                    ? ThemeBrush("AppAccentBrush", Color.FromRgb(15, 95, 159))
                    : ThemeBrush("AppButtonBorderBrush", Color.FromRgb(203, 213, 225));
                button.Foreground = count > 0 || isSelected
                    ? isSelected
                        ? ThemeBrush("AppAccentBrush", Color.FromRgb(15, 95, 159))
                        : ThemeBrush("AppTitleBrush", Color.FromRgb(43, 20, 95))
                    : ThemeBrush("AppDisabledTextBrush", Color.FromRgb(148, 163, 184));
            }

            issueStack.Children.Clear();
            var issues = report.Issues
                .Where(issue => issue.Scope == selectedScope)
                .ToList();
            if (issues.Count == 0)
            {
                issueStack.Children.Add(CreateScheduleConstraintEmptyCard(report.HasIssues
                    ? $"当前没有{FormatScheduleConstraintScope(selectedScope)}。"
                    : "当前赛程暂无高级约束提醒。"));
                return;
            }

            foreach (var issue in issues)
            {
                issueStack.Children.Add(CreateScheduleConstraintIssueCard(issue));
            }
        }

        Button AddFilterButton(string text, ScheduleConstraintIssueScope scope)
        {
            var button = new Button
            {
                Content = text,
                Tag = scope,
                Padding = new Avalonia.Thickness(12, 6),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            button.Click += (_, _) => RenderIssues(scope);
            filterButtons.Add(button);
            filterStack.Children.Add(button);
            return button;
        }

        AddFilterButton($"已确定风险 {report.ConfirmedCount}", ScheduleConstraintIssueScope.Confirmed);
        AddFilterButton($"下一轮接续 {report.DirectDependencyCount}", ScheduleConstraintIssueScope.DirectDependency);
        AddFilterButton($"推演风险 {report.SpeculativeCount}", ScheduleConstraintIssueScope.Speculative);
        RenderIssues(selectedScope);
        return root;
    }

    private Border CreateScheduleConstraintEmptyCard(string text)
    {
        return new Border
        {
            Background = ThemeBrush("AppSuccessCardBackgroundBrush", Color.FromRgb(240, 248, 241)),
            BorderBrush = ThemeBrush("AppSuccessCardBorderBrush", Color.FromRgb(212, 234, 216)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(12),
            Child = new TextBlock
            {
                Text = text,
                Foreground = ThemeBrush("AppSuccessTextBrush", Color.FromRgb(37, 101, 74)),
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private Border CreateScheduleConstraintIssueCard(ScheduleConstraintIssue issue)
    {
        var isBlocking = issue.Severity == ScheduleConstraintSeverity.Severe;
        var isWarning = issue.Severity == ScheduleConstraintSeverity.Warning;
        var borderBrush = isBlocking
            ? ThemeBrush("AppErrorCardBorderBrush", Color.FromRgb(220, 38, 38))
            : isWarning
                ? ThemeBrush("AppWarningCardBorderBrush", Color.FromRgb(217, 119, 6))
                : ThemeBrush("AppWarningCardBorderBrush", Color.FromRgb(242, 216, 137));
        var backgroundBrush = isBlocking
            ? ThemeBrush("AppErrorCardBackgroundBrush", Color.FromRgb(254, 242, 242))
            : ThemeBrush("AppWarningCardBackgroundBrush", isWarning ? Color.FromRgb(255, 250, 235) : Color.FromRgb(255, 248, 230));
        var detailBrush = isBlocking
            ? ThemeBrush("AppErrorTextBrush", Color.FromRgb(185, 28, 28))
            : ThemeBrush("AppWarningTextBrush", Color.FromRgb(120, 83, 0));
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{FormatScheduleConstraintSeverity(issue.Severity)} · {FormatScheduleConstraintScope(issue.Scope)} · {issue.DayLabel} {FormatOptionalTime(issue.StartTime)} · {issue.Court ?? "-"} · {issue.Phase} {issue.MatchName}",
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(43, 20, 95)),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = issue.Message,
            Foreground = detailBrush,
            TextWrapping = TextWrapping.Wrap
        });

        var card = new Border
        {
            Background = backgroundBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Avalonia.Thickness(isBlocking ? 2 : 1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(12),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = issue,
            Child = stack
        };
        card.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _ = FocusScheduleConstraintIssueAsync(issue);
        };
        return card;
    }

    private static string FormatOptionalTime(TimeOnly? time)
    {
        return time.HasValue ? time.Value.ToString("HH:mm") : "--:--";
    }

    private static string FormatScheduleConstraintScope(ScheduleConstraintIssueScope scope)
    {
        return scope switch
        {
            ScheduleConstraintIssueScope.Confirmed => "已确定风险",
            ScheduleConstraintIssueScope.DirectDependency => "下一轮接续",
            _ => "推演风险"
        };
    }

    private async Task FocusScheduleConstraintIssueAsync(ScheduleConstraintIssue issue)
    {
        if (_latestSchedule is null)
        {
            SetStatus("请先生成或打开赛程。", isError: true);
            return;
        }

        var match = _latestSchedule.Matches.FirstOrDefault(item => string.Equals(item.MatchName, issue.MatchName, StringComparison.Ordinal));
        if (match is null)
        {
            SetStatus($"未找到提醒对应的赛程：{issue.MatchName}", isError: true);
            return;
        }

        if (!EnsureScheduleBoardWindowOpen(match.DayLabel))
        {
            return;
        }

        await Task.Delay(60);
        if (!_scheduleBoardWindowMatchCards.TryGetValue(match.MatchName, out var card))
        {
            RefreshScheduleBoardWindow(match.DayLabel);
            await Task.Delay(60);
            _scheduleBoardWindowMatchCards.TryGetValue(match.MatchName, out card);
        }

        if (card is null)
        {
            SetStatus($"已打开赛程安排窗口，但未定位到卡片：{match.MatchName}", isError: true);
            return;
        }

        _scheduleBoardWindow?.Activate();
        card.BringIntoView();
        await FlashScheduleMatchCardAsync(card);
        SetStatus($"已定位到赛程：{match.DayLabel} {match.TimeRange} {match.Court} {match.MatchName}");
    }

    private async Task FlashScheduleMatchCardAsync(Border card)
    {
        var version = ++_scheduleBoardHighlightVersion;
        var originalBackground = card.Background;
        var originalBorderBrush = card.BorderBrush;
        var originalBorderThickness = card.BorderThickness;
        var highlightBackground = new SolidColorBrush(Color.FromRgb(255, 247, 188));
        var highlightBorder = new SolidColorBrush(Color.FromRgb(217, 119, 6));

        try
        {
            for (var index = 0; index < 3; index++)
            {
                if (version != _scheduleBoardHighlightVersion)
                {
                    return;
                }

                card.Background = highlightBackground;
                card.BorderBrush = highlightBorder;
                card.BorderThickness = new Avalonia.Thickness(2);
                await Task.Delay(100);
                if (version != _scheduleBoardHighlightVersion)
                {
                    return;
                }

                card.Background = originalBackground;
                card.BorderBrush = originalBorderBrush;
                card.BorderThickness = originalBorderThickness;
                await Task.Delay(index == 2 ? 0 : 100);
            }
        }
        finally
        {
            if (version == _scheduleBoardHighlightVersion)
            {
                card.Background = originalBackground;
                card.BorderBrush = originalBorderBrush;
                card.BorderThickness = originalBorderThickness;
            }
        }
    }

    private void ShowCrossEventConflictDetails_Click(object? sender, RoutedEventArgs e)
    {
        if (_crossEventScheduleBoard is null)
        {
            SetStatus("请先加载多项目赛程。", isError: true);
            return;
        }

        UpdateCrossEventReminderButton();
        if (_crossEventConflictWindow is { IsVisible: true })
        {
            _crossEventConflictWindow.Close();
        }

        var dialog = new Window
        {
            Title = "多项目提醒",
            Width = 920,
            Height = 660,
            MinWidth = 720,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = BuildCrossEventConflictDialogContent(_crossEventScheduleBoard.Report)
        };
        _crossEventConflictWindow = dialog;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_crossEventConflictWindow, dialog))
            {
                _crossEventConflictWindow = null;
            }
        };
        dialog.Show(this);
    }

    private Control BuildCrossEventConflictDialogContent(CrossEventConflictReport report)
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Avalonia.Thickness(16)
        };
        var headerStack = new StackPanel { Spacing = 10 };
        headerStack.Children.Add(new TextBlock
        {
            Text = "多项目提醒",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(40, 16, 78))
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = $"严重 {report.SevereCount}，警告 {report.WarningCount}，同日/负荷推演提醒 {report.NoticeCount}。点击卡片可定位到多项目赛程窗口。",
            Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(100, 116, 139)),
            TextWrapping = TextWrapping.Wrap
        });

        var filterStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        headerStack.Children.Add(filterStack);
        var categoryStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        headerStack.Children.Add(categoryStack);
        root.Children.Add(headerStack);

        var issueStack = new StackPanel { Spacing = 10 };
        var issueScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
            Content = issueStack
        };
        Grid.SetRow(issueScroll, 1);
        root.Children.Add(issueScroll);

        var filterButtons = new List<Button>();
        var categoryButtons = new List<Button>();
        var selectedSeverity = report.SevereCount > 0
            ? CrossEventConflictSeverity.Severe
            : report.WarningCount > 0
                ? CrossEventConflictSeverity.Warning
                : CrossEventConflictSeverity.Notice;
        var selectedCategory = CrossEventIssueCategory.All;

        static int CountIssuesBySeverity(CrossEventConflictReport report, CrossEventConflictSeverity severity)
        {
            return severity switch
            {
                CrossEventConflictSeverity.Severe => report.SevereCount,
                CrossEventConflictSeverity.Warning => report.WarningCount,
                _ => report.NoticeCount
            };
        }

        int CountIssuesByCategory(CrossEventIssueCategory category)
        {
            return category == CrossEventIssueCategory.All
                ? report.Issues.Count
                : report.Issues.Count(issue => GetCrossEventIssueCategory(issue) == category);
        }

        void RenderIssues()
        {
            foreach (var button in filterButtons)
            {
                var isSelected = Equals(button.Tag, selectedSeverity);
                var count = CountIssuesBySeverity(report, (CrossEventConflictSeverity)button.Tag!);
                button.IsEnabled = count > 0;
                button.Opacity = count > 0 || isSelected ? 1 : 0.48;
                button.Background = isSelected
                    ? ThemeBrush("AppInfoCardBackgroundBrush", Color.FromRgb(239, 246, 255))
                    : ThemeBrush("AppButtonBackgroundBrush", Color.FromRgb(255, 255, 255));
                button.BorderBrush = isSelected
                    ? ThemeBrush("AppAccentBrush", Color.FromRgb(15, 95, 159))
                    : ThemeBrush("AppButtonBorderBrush", Color.FromRgb(203, 213, 225));
                button.Foreground = count > 0 || isSelected
                    ? isSelected
                        ? ThemeBrush("AppAccentBrush", Color.FromRgb(15, 95, 159))
                        : ThemeBrush("AppTitleBrush", Color.FromRgb(43, 20, 95))
                    : ThemeBrush("AppDisabledTextBrush", Color.FromRgb(148, 163, 184));
            }

            foreach (var button in categoryButtons)
            {
                var isSelected = Equals(button.Tag, selectedCategory);
                var count = CountIssuesByCategory((CrossEventIssueCategory)button.Tag!);
                button.IsEnabled = count > 0 || isSelected;
                button.Opacity = count > 0 || isSelected ? 1 : 0.48;
                button.Background = isSelected
                    ? ThemeBrush("AppSuccessCardBackgroundBrush", Color.FromRgb(240, 253, 244))
                    : ThemeBrush("AppButtonBackgroundBrush", Color.FromRgb(255, 255, 255));
                button.BorderBrush = isSelected
                    ? ThemeBrush("AppSuccessCardBorderBrush", Color.FromRgb(22, 101, 52))
                    : ThemeBrush("AppButtonBorderBrush", Color.FromRgb(203, 213, 225));
                button.Foreground = count > 0 || isSelected
                    ? isSelected
                        ? ThemeBrush("AppSuccessTextBrush", Color.FromRgb(22, 101, 52))
                        : ThemeBrush("AppTitleBrush", Color.FromRgb(43, 20, 95))
                    : ThemeBrush("AppDisabledTextBrush", Color.FromRgb(148, 163, 184));
            }

            issueStack.Children.Clear();
            var issues = report.Issues
                .Where(issue => issue.Severity == selectedSeverity)
                .Where(issue => selectedCategory == CrossEventIssueCategory.All
                                || GetCrossEventIssueCategory(issue) == selectedCategory)
                .ToList();
            if (issues.Count == 0)
            {
                issueStack.Children.Add(CreateScheduleConstraintEmptyCard(report.HasIssues
                    ? $"当前没有{FormatCrossEventConflictSeverity(selectedSeverity)} / {FormatCrossEventIssueCategory(selectedCategory)}提醒。"
                    : "当前多项目赛程暂无提醒。"));
                return;
            }

            foreach (var issue in issues)
            {
                issueStack.Children.Add(CreateCrossEventConflictIssueCard(issue));
            }
        }

        Button AddFilterButton(string text, CrossEventConflictSeverity severity)
        {
            var button = new Button
            {
                Content = text,
                Tag = severity,
                Padding = new Avalonia.Thickness(12, 6),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            button.Click += (_, _) =>
            {
                selectedSeverity = severity;
                RenderIssues();
            };
            filterButtons.Add(button);
            filterStack.Children.Add(button);
            return button;
        }

        Button AddCategoryButton(string text, CrossEventIssueCategory category)
        {
            var button = new Button
            {
                Content = text,
                Tag = category,
                Padding = new Avalonia.Thickness(12, 6),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            button.Click += (_, _) =>
            {
                selectedCategory = category;
                RenderIssues();
            };
            categoryButtons.Add(button);
            categoryStack.Children.Add(button);
            return button;
        }

        AddFilterButton($"严重 {report.SevereCount}", CrossEventConflictSeverity.Severe);
        AddFilterButton($"警告 {report.WarningCount}", CrossEventConflictSeverity.Warning);
        AddFilterButton($"提醒/推演 {report.NoticeCount}", CrossEventConflictSeverity.Notice);
        AddCategoryButton($"全部类型 {report.Issues.Count}", CrossEventIssueCategory.All);
        AddCategoryButton($"兼项间隔 {CountIssuesByCategory(CrossEventIssueCategory.MultiEventInterval)}", CrossEventIssueCategory.MultiEventInterval);
        AddCategoryButton($"同日负荷 {CountIssuesByCategory(CrossEventIssueCategory.SameDayLoad)}", CrossEventIssueCategory.SameDayLoad);
        AddCategoryButton($"负荷推演 {CountIssuesByCategory(CrossEventIssueCategory.LoadForecast)}", CrossEventIssueCategory.LoadForecast);
        AddCategoryButton($"赛程冲突 {CountIssuesByCategory(CrossEventIssueCategory.ScheduleConflict)}", CrossEventIssueCategory.ScheduleConflict);
        RenderIssues();
        return root;
    }

    private Border CreateCrossEventConflictIssueCard(CrossEventConflictIssue issue)
    {
        var isSevere = issue.Severity == CrossEventConflictSeverity.Severe;
        var isWarning = issue.Severity == CrossEventConflictSeverity.Warning;
        var borderBrush = isSevere
            ? ThemeBrush("AppErrorCardBorderBrush", Color.FromRgb(220, 38, 38))
            : isWarning
                ? ThemeBrush("AppWarningCardBorderBrush", Color.FromRgb(217, 119, 6))
                : ThemeBrush("AppWarningCardBorderBrush", Color.FromRgb(242, 216, 137));
        var backgroundBrush = isSevere
            ? ThemeBrush("AppErrorCardBackgroundBrush", Color.FromRgb(254, 242, 242))
            : ThemeBrush("AppWarningCardBackgroundBrush", isWarning ? Color.FromRgb(255, 250, 235) : Color.FromRgb(255, 248, 230));
        var detailBrush = isSevere
            ? ThemeBrush("AppErrorTextBrush", Color.FromRgb(185, 28, 28))
            : ThemeBrush("AppWarningTextBrush", Color.FromRgb(120, 83, 0));
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{FormatCrossEventConflictSeverity(issue.Severity)} · {FormatCrossEventIssueCategory(issue)} · {issue.DayLabel} · {issue.FirstMatch.EventName} {issue.FirstMatch.Phase} {issue.FirstMatch.MatchName}",
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(43, 20, 95)),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{issue.FirstMatch.TimeRange} · {issue.FirstMatch.Court} · {issue.PlayerName}",
            Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(71, 85, 105)),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = issue.Detail,
            Foreground = detailBrush,
            TextWrapping = TextWrapping.Wrap
        });

        var card = new Border
        {
            Background = backgroundBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Avalonia.Thickness(isSevere ? 2 : 1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(12),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = issue,
            Child = stack
        };
        card.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _ = FocusCrossEventConflictIssueAsync(issue);
        };
        return card;
    }

    private async Task FocusCrossEventPlayerAppearanceAsync(CrossEventPlayerScheduleAppearance appearance)
    {
        if (_crossEventScheduleBoard is null)
        {
            SetStatus("请先加载多项目赛程。", isError: true);
            return;
        }

        var item = _crossEventScheduleBoard.Items.FirstOrDefault(item =>
                       string.Equals(item.Key, appearance.ItemKey, StringComparison.Ordinal))
                   ?? _crossEventScheduleBoard.Items.FirstOrDefault(item =>
                       string.Equals(item.EventName, appearance.EventName, StringComparison.Ordinal)
                       && string.Equals(item.MatchName, appearance.MatchName, StringComparison.Ordinal)
                       && string.Equals(item.DayLabel, appearance.DayLabel, StringComparison.Ordinal)
                       && item.StartTime == appearance.StartTime
                       && string.Equals(item.Court, appearance.Court, StringComparison.Ordinal));
        if (item is null)
        {
            SetStatus($"未找到兼项明细对应的赛程：{appearance.EventName} {appearance.MatchName}", isError: true);
            return;
        }

        await FocusCrossEventScheduleBoardItemAsync(item, "已定位到兼项选手场次");
    }

    private static string FormatCrossEventConflictSeverity(CrossEventConflictSeverity severity)
    {
        return severity switch
        {
            CrossEventConflictSeverity.Severe => "严重",
            CrossEventConflictSeverity.Warning => "警告",
            _ => "提醒"
        };
    }

    private static string FormatCrossEventIssueCategory(CrossEventConflictIssue issue)
    {
        return FormatCrossEventIssueCategory(GetCrossEventIssueCategory(issue));
    }

    private static string FormatCrossEventIssueCategory(CrossEventIssueCategory category)
    {
        return category switch
        {
            CrossEventIssueCategory.All => "全部类型",
            CrossEventIssueCategory.MultiEventInterval => "兼项间隔",
            CrossEventIssueCategory.SameDayLoad => "同日负荷",
            CrossEventIssueCategory.LoadForecast => "负荷推演",
            _ => "赛程冲突"
        };
    }

    private static CrossEventIssueCategory GetCrossEventIssueCategory(CrossEventConflictIssue issue)
    {
        if (issue.Detail.StartsWith("负荷推演", StringComparison.Ordinal))
        {
            return CrossEventIssueCategory.LoadForecast;
        }

        if (issue.Detail.StartsWith("同日", StringComparison.Ordinal)
            || issue.Detail.Contains("跨项目累计", StringComparison.Ordinal))
        {
            return CrossEventIssueCategory.SameDayLoad;
        }

        return issue.RestMinutes.HasValue
            ? CrossEventIssueCategory.MultiEventInterval
            : CrossEventIssueCategory.ScheduleConflict;
    }

    private async Task FocusCrossEventConflictIssueAsync(CrossEventConflictIssue issue)
    {
        if (_crossEventScheduleBoard is null)
        {
            SetStatus("请先加载多项目赛程。", isError: true);
            return;
        }

        var item = FindCrossEventIssueTarget(issue);
        if (item is null)
        {
            SetStatus($"未找到提醒对应的多项目赛程：{issue.FirstMatch.EventName} {issue.FirstMatch.MatchName}", isError: true);
            return;
        }

        await FocusCrossEventScheduleBoardItemAsync(item, "已定位到多项目赛程");
    }

    private async Task FocusCrossEventScheduleBoardItemAsync(
        CrossEventScheduleBoardItem item,
        string statusPrefix)
    {
        RefreshCrossEventScheduleBoard(item.DayLabel);
        if (!EnsureCrossEventBoardWindowOpen(item.DayLabel))
        {
            return;
        }

        await Task.Delay(60);
        if (!_crossEventBoardWindowMatchCards.TryGetValue(item.Key, out var card))
        {
            RefreshCrossEventBoardWindow(item.DayLabel);
            await Task.Delay(60);
            _crossEventBoardWindowMatchCards.TryGetValue(item.Key, out card);
        }

        if (card is null)
        {
            SetStatus($"已打开多项目赛程窗口，但未定位到卡片：{item.MatchLabel}", isError: true);
            return;
        }

        _crossEventBoardWindow?.Activate();
        card.BringIntoView();
        await FlashScheduleMatchCardAsync(card);
        SetStatus($"{statusPrefix}：{item.DayLabel} {item.TimeRange} {item.Court} {item.MatchLabel}");
    }

    private CrossEventScheduleBoardItem? FindCrossEventIssueTarget(CrossEventConflictIssue issue)
    {
        if (_crossEventScheduleBoard is null)
        {
            return null;
        }

        return FindCrossEventIssueAppearance(issue.FirstMatch)
               ?? FindCrossEventIssueAppearance(issue.SecondMatch);
    }

    private CrossEventScheduleBoardItem? FindCrossEventIssueAppearance(CrossEventPlayerAppearance appearance)
    {
        if (_crossEventScheduleBoard is null)
        {
            return null;
        }

        return _crossEventScheduleBoard.Items.FirstOrDefault(item =>
                   string.Equals(item.SourceId, appearance.SourceId, StringComparison.Ordinal)
                   && string.Equals(item.MatchName, appearance.MatchName, StringComparison.Ordinal)
                   && string.Equals(item.DayLabel, appearance.DayLabel, StringComparison.Ordinal)
                   && item.StartTime == appearance.StartTime)
               ?? _crossEventScheduleBoard.Items.FirstOrDefault(item =>
                   string.Equals(item.SourceId, appearance.SourceId, StringComparison.Ordinal)
                   && string.Equals(item.MatchName, appearance.MatchName, StringComparison.Ordinal));
    }
}
