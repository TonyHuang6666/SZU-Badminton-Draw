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
    private void GenerateSchedule_Click(object? sender, RoutedEventArgs e)
    {
        TryGenerateSchedule();
    }

    private async void ScheduleAutoStrategyBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        if (_latestResult is null)
        {
            ScheduleCustomSchedulingPanel.IsVisible = GetScheduleAutoSchedulingStrategy() == ScheduleAutoSchedulingStrategy.Custom;
            return;
        }

        var strategy = GetScheduleAutoSchedulingStrategy();
        ScheduleCustomSchedulingPanel.IsVisible = strategy == ScheduleAutoSchedulingStrategy.Custom;
        if (strategy == ScheduleAutoSchedulingStrategy.Custom)
        {
            EnsureScheduleCustomRecommendation();
        }
        else
        {
            _singleScheduleRecommendedCustomSettings = null;
        }

        RebuildScheduleCustomSchedulingControls();
        await RebuildSingleScheduleByStrategyAsync(
            forceConfirmation: _singleScheduleUndoStack.Count > 0 || _progressState is not null,
            restoreStrategyOnCancel: true);
    }

    private async Task RebuildSingleScheduleByStrategyAsync(
        bool forceConfirmation,
        bool restoreStrategyOnCancel)
    {
        var previousStrategy = _latestSchedule?.Settings.AutoSchedulingStrategy;
        if (_progressState is { Results.Count: > 0 })
        {
            if (restoreStrategyOnCancel && previousStrategy is not null)
            {
                SelectScheduleAutoStrategyWithoutEvent(previousStrategy.Value);
            }

            SetStatus(
                "当前赛事存档已有赛果，不能整体重排单项目赛程；请在赛程安排窗口拖动未完成场次。",
                isError: true);
            return;
        }

        var strategy = GetScheduleAutoSchedulingStrategy();
        var strategyText = ScheduleWorkflow.BuildScheduleStrategyText(strategy);
        if (forceConfirmation && _latestSchedule is not null)
        {
            var confirmed = await ConfirmAsync(
                "按策略重新编排",
                $"将按“{strategyText}”重新生成单项目赛程，已有手动拖动调整会被覆盖。是否继续？");
            if (!confirmed)
            {
                if (restoreStrategyOnCancel && previousStrategy is not null)
                {
                    SelectScheduleAutoStrategyWithoutEvent(previousStrategy.Value);
                    RebuildScheduleCustomSchedulingControls();
                }

                SetStatus("已取消按策略重新编排。", isWarning: true);
                return;
            }
        }

        var selectedDay = _scheduleBoardWindowDayBox?.SelectedItem?.ToString();
        if (!TryGenerateSchedule())
        {
            if (restoreStrategyOnCancel && previousStrategy is not null)
            {
                SelectScheduleAutoStrategyWithoutEvent(previousStrategy.Value);
                RebuildScheduleCustomSchedulingControls();
            }

            return;
        }

        RefreshScheduleBoardWindow(selectedDay);
        var hasConstraintWarnings = _latestScheduleConstraintReport is { SevereCount: > 0 } or { WarningCount: > 0 };
        SetStatus(
            $"已按“{strategyText}”重新编排单项目赛程；手动调整历史已清空。",
            isWarning: hasConstraintWarnings);
    }

    private void ScheduleCustomSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingScheduleCustomControls)
        {
            return;
        }

        UpdateScheduleCustomLabels();
        var anchor = sender is Slider { Tag: ScheduleCustomSliderTag tag }
            ? new ScheduleCustomAnchor(tag.Kind, tag.DayLabel)
            : ScheduleCustomAnchor.None;
        QueueScheduleCustomRecalculate(anchor);
    }

    private void ScheduleCustomRecalculateTimer_Tick(object? sender, EventArgs e)
    {
        _scheduleCustomRecalculateTimer?.Stop();
        if (GetScheduleAutoSchedulingStrategy() == ScheduleAutoSchedulingStrategy.Custom)
        {
            RunSingleScheduleCustomRecalculate(_pendingScheduleCustomAnchor);
        }
    }

    private void QueueScheduleCustomRecalculate(ScheduleCustomAnchor anchor)
    {
        if (_updatingScheduleCustomControls
            || _latestResult is null
            || GetScheduleAutoSchedulingStrategy() != ScheduleAutoSchedulingStrategy.Custom)
        {
            return;
        }

        _pendingScheduleCustomAnchor = anchor;
        _scheduleCustomRecalculateTimer?.Stop();
        _scheduleCustomRecalculateTimer?.Start();
        SetStatus($"单项目自定义参数已变化，将以{anchor.Describe()}为锚点重新编排…", isWarning: true);
    }

    private void ResetScheduleCustomDefaults_Click(object? sender, RoutedEventArgs e)
    {
        if (_latestResult is null)
        {
            SetStatus("请先预览抽签。", isError: true);
            return;
        }

        EnsureScheduleCustomRecommendation(force: true);
        RunSingleScheduleCustomRecalculate(new ScheduleCustomAnchor(ScheduleCustomAnchorKind.Recommended));
    }

    private void RunSingleScheduleCustomRecalculate(ScheduleCustomAnchor anchor)
    {
        if (_runningSingleScheduleCustom)
        {
            return;
        }

        if (_progressState is { Results.Count: > 0 })
        {
            SetStatus("当前赛事存档已有赛果，不能整体重排单项目赛程；请在赛程安排窗口拖动未完成场次。", isError: true);
            return;
        }

        if (_latestResult is null)
        {
            SetStatus("请先预览抽签。", isError: true);
            return;
        }

        _runningSingleScheduleCustom = true;
        try
        {
            var settings = BuildAnchoredCustomScheduleSettings(anchor);
            if (TryGenerateSchedule(settings, rollbackCustomOnFailure: true, anchor))
            {
                SetStatus($"单项目自定义参数已按{anchor.Describe()}重新编排。", isWarning: _latestScheduleConstraintReport is { HasIssues: true });
            }
        }
        finally
        {
            _pendingScheduleCustomAnchor = ScheduleCustomAnchor.None;
            _runningSingleScheduleCustom = false;
        }
    }

    private void EnsureScheduleCustomRecommendation(bool force = false)
    {
        if (!force && _singleScheduleRecommendedCustomSettings is not null)
        {
            return;
        }

        if (_latestResult is null)
        {
            return;
        }

        var baseSettings = BuildScheduleSettings(ScheduleAutoSchedulingStrategy.BalancedRelaxed, includeCustomTargets: false);
        var options = _scheduleWorkflow.CreateSchedulingOptions(
            _latestResult,
            baseSettings,
            ScheduleAutoSchedulingStrategy.BalancedRelaxed);
        _singleScheduleRecommendedCustomSettings = baseSettings with
        {
            AutoSchedulingStrategy = ScheduleAutoSchedulingStrategy.Custom,
            DayLoadTargets = options.DayLoadTargets,
            SynchronizeStageWaves = options.SynchronizeStageWaves,
            StageWaveTargets = options.StageWaveTargets
        };
    }

    private ScheduleSettings BuildAnchoredCustomScheduleSettings(ScheduleCustomAnchor anchor)
    {
        var baseSettings = BuildScheduleSettings(ScheduleAutoSchedulingStrategy.Custom, includeCustomTargets: false);
        EnsureScheduleCustomRecommendation();
        var recommendation = _singleScheduleRecommendedCustomSettings ?? baseSettings;
        var current = (_singleScheduleLastAcceptedSettings?.AutoSchedulingStrategy == ScheduleAutoSchedulingStrategy.Custom
                ? _singleScheduleLastAcceptedSettings
                : _latestSchedule?.Settings.AutoSchedulingStrategy == ScheduleAutoSchedulingStrategy.Custom
                    ? _latestSchedule.Settings
                    : recommendation)
            ?? recommendation;

        return anchor.Kind switch
        {
            ScheduleCustomAnchorKind.Recommended => baseSettings with
            {
                DayLoadTargets = recommendation.DayLoadTargets,
                SynchronizeStageWaves = recommendation.SynchronizeStageWaves,
                StageWaveTargets = recommendation.StageWaveTargets
            },
            ScheduleCustomAnchorKind.DayLoad when !string.IsNullOrWhiteSpace(anchor.DayLabel) => baseSettings with
            {
                DayLoadTargets = BuildAnchoredScheduleDayLoadTargets(baseSettings, recommendation, anchor.DayLabel),
                SynchronizeStageWaves = ScheduleStageWaveBox.IsChecked == true,
                StageWaveTargets = ReadScheduleStageWaveTargetsFromControls(current)
            },
            ScheduleCustomAnchorKind.StageWave when !string.IsNullOrWhiteSpace(anchor.DayLabel) => baseSettings with
            {
                DayLoadTargets = ReadScheduleDayLoadTargetsFromControls(current),
                SynchronizeStageWaves = ScheduleStageWaveBox.IsChecked == true,
                StageWaveTargets = BuildAnchoredScheduleStageWaveTargets(baseSettings, recommendation, anchor.DayLabel)
            },
            ScheduleCustomAnchorKind.StageWaveEnabled => baseSettings with
            {
                DayLoadTargets = ReadScheduleDayLoadTargetsFromControls(current),
                SynchronizeStageWaves = ScheduleStageWaveBox.IsChecked == true,
                StageWaveTargets = ReadScheduleStageWaveTargetsFromControls(current)
            },
            _ => baseSettings with
            {
                DayLoadTargets = ReadScheduleDayLoadTargetsFromControls(current),
                SynchronizeStageWaves = ScheduleStageWaveBox.IsChecked == true,
                StageWaveTargets = ReadScheduleStageWaveTargetsFromControls(current)
            }
        };
    }

    private IReadOnlyList<ScheduleDayLoadTarget> BuildAnchoredScheduleDayLoadTargets(
        ScheduleSettings baseSettings,
        ScheduleSettings recommendation,
        string anchorDayLabel)
    {
        var orderedDays = baseSettings.Days.OrderBy(day => day.Date).ToList();
        if (!_scheduleDayLoadSliders.TryGetValue(anchorDayLabel, out var anchorSlider))
        {
            return recommendation.DayLoadTargets;
        }

        var capacityByDay = orderedDays.ToDictionary(
            day => day.DayLabel,
            day => (double)ScheduleResourceCalculator.CalculateDayCapacityMinutes(
                day,
                baseSettings.RefereeCount,
                slotMinutes: 1),
            StringComparer.Ordinal);
        var recommendedMinutes = orderedDays.Sum(day =>
            capacityByDay[day.DayLabel] * (recommendation.DayLoadTargets.FirstOrDefault(target => target.DayLabel == day.DayLabel)?.TargetUtilization ?? 0.6));
        var anchorCapacity = Math.Max(1d, capacityByDay[anchorDayLabel]);
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
        var assigned = AllocateScheduleTargetMinutes(remainingDays, capacityByDay, weights, remainingMinutes);

        var result = new List<ScheduleDayLoadTarget>();
        foreach (var day in orderedDays)
        {
            var capacity = Math.Max(1d, capacityByDay[day.DayLabel]);
            var target = string.Equals(day.DayLabel, anchorDayLabel, StringComparison.Ordinal)
                ? anchorTarget
                : assigned.TryGetValue(day.DayLabel, out var minutes)
                    ? minutes / capacity
                    : recommendation.DayLoadTargets.FirstOrDefault(item => item.DayLabel == day.DayLabel)?.TargetUtilization ?? 0.6;
            result.Add(new ScheduleDayLoadTarget(day.DayLabel, target, Math.Min(1.0, target + 0.15)));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, double> AllocateScheduleTargetMinutes(
        IReadOnlyList<ScheduleDaySettings> days,
        IReadOnlyDictionary<string, double> capacityByDay,
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
                var capacity = Math.Max(1d, capacityByDay[day.DayLabel]);
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

    private IReadOnlyList<ScheduleStageWaveTarget> BuildAnchoredScheduleStageWaveTargets(
        ScheduleSettings baseSettings,
        ScheduleSettings recommendation,
        string anchorDayLabel)
    {
        var orderedDays = baseSettings.Days.OrderBy(day => day.Date).ToList();
        if (!_scheduleStageWaveSliders.TryGetValue(anchorDayLabel, out var anchorSlider))
        {
            return recommendation.StageWaveTargets;
        }

        var baseTargets = BuildScheduleStageWaveTargetLookup(orderedDays, recommendation);
        var anchorIndex = orderedDays.FindIndex(day => string.Equals(day.DayLabel, anchorDayLabel, StringComparison.Ordinal));
        if (anchorIndex < 0)
        {
            return recommendation.StageWaveTargets;
        }

        var anchorBase = baseTargets[anchorDayLabel];
        var anchorTarget = Math.Clamp(anchorSlider.Value / 100d, 0.05, 0.95);
        var result = new List<ScheduleStageWaveTarget>();
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

            result.Add(new ScheduleStageWaveTarget(day.DayLabel, value));
        }

        return NormalizeScheduleStageWaveTargets(result);
    }

    private static Dictionary<string, double> BuildScheduleStageWaveTargetLookup(
        IReadOnlyList<ScheduleDaySettings> orderedDays,
        ScheduleSettings settings)
    {
        return orderedDays.Select((day, index) => (day, index)).ToDictionary(
            item => item.day.DayLabel,
            item => settings.StageWaveTargets.FirstOrDefault(target => target.DayLabel == item.day.DayLabel)?.CumulativeProgress
                ?? ((item.index + 1d) / orderedDays.Count),
            StringComparer.Ordinal);
    }

    private static List<ScheduleStageWaveTarget> NormalizeScheduleStageWaveTargets(
        IReadOnlyList<ScheduleStageWaveTarget> targets)
    {
        var result = new List<ScheduleStageWaveTarget>();
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

    private void RebuildScheduleCustomSchedulingControls()
    {
        if (GetScheduleAutoSchedulingStrategy() != ScheduleAutoSchedulingStrategy.Custom)
        {
            ScheduleCustomSchedulingPanel.IsVisible = false;
            return;
        }

        ScheduleCustomSchedulingPanel.IsVisible = true;
        if (_latestResult is null)
        {
            ScheduleCustomDayLoadPanel.Children.Clear();
            ScheduleStageWavePanel.Children.Clear();
            ScheduleCustomHintText.Text = "先预览抽签或生成赛程后，这里会显示单项目自定义参数。";
            return;
        }

        _updatingScheduleCustomControls = true;
        try
        {
            EnsureScheduleCustomRecommendation();
            var settings = _latestSchedule?.Settings.AutoSchedulingStrategy == ScheduleAutoSchedulingStrategy.Custom
                ? _latestSchedule.Settings
                : _singleScheduleLastAcceptedSettings?.AutoSchedulingStrategy == ScheduleAutoSchedulingStrategy.Custom
                    ? _singleScheduleLastAcceptedSettings
                    : _singleScheduleRecommendedCustomSettings
                      ?? BuildScheduleSettings(ScheduleAutoSchedulingStrategy.Custom, includeCustomTargets: false);
            ScheduleCustomHintText.Text =
                "这些参数来自系统按“均衡宽松”推导的单项目推荐值；微调后会重新生成当前项目赛程。";
            RebuildScheduleDayLoadControls(settings);
            RebuildScheduleStageWaveControls(settings);
            UpdateScheduleCustomLabels();
        }
        finally
        {
            _updatingScheduleCustomControls = false;
        }
    }

    private void RebuildScheduleDayLoadControls(ScheduleSettings settings)
    {
        ScheduleCustomDayLoadPanel.Children.Clear();
        _scheduleDayLoadSliders.Clear();
        _scheduleDayLoadLabels.Clear();
        _scheduleDayLoadRecommendedRanges.Clear();
        EnsureScheduleCustomRecommendation();
        var recommendedTargets = (_singleScheduleRecommendedCustomSettings ?? settings)
            .DayLoadTargets
            .ToDictionary(target => target.DayLabel, target => target.TargetUtilization, StringComparer.Ordinal);
        foreach (var day in settings.Days.OrderBy(day => day.Date))
        {
            var target = settings.DayLoadTargets.FirstOrDefault(item => item.DayLabel == day.DayLabel)?.TargetUtilization
                ?? recommendedTargets.GetValueOrDefault(day.DayLabel, 0.6);
            var recommended = recommendedTargets.TryGetValue(day.DayLabel, out var recommendedTarget)
                ? recommendedTarget
                : target;
            var recommendedPercent = Math.Round(recommended * 100);
            var rangeMin = Math.Clamp(recommendedPercent - 15, 10, 100);
            var rangeMax = Math.Clamp(recommendedPercent + 15, 10, 100);
            var label = new TextBlock
            {
                Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap
            };
            var slider = new Slider
            {
                Minimum = 10,
                Maximum = 100,
                TickFrequency = 5,
                IsSnapToTickEnabled = true,
                Value = Math.Round(target * 20) * 5,
                Tag = new ScheduleCustomSliderTag(ScheduleCustomAnchorKind.DayLoad, day.DayLabel)
            };
            ToolTip.SetTip(slider, $"{day.DayLabel} 推荐可行区间：{rangeMin:0}%-{rangeMax:0}%；系统建议 {recommendedPercent:0}%。");
            slider.ValueChanged += ScheduleCustomSlider_ValueChanged;
            _scheduleDayLoadLabels[day.DayLabel] = label;
            _scheduleDayLoadSliders[day.DayLabel] = slider;
            _scheduleDayLoadRecommendedRanges[day.DayLabel] = (rangeMin, rangeMax, recommendedPercent);
            ScheduleCustomDayLoadPanel.Children.Add(new StackPanel
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

    private void RebuildScheduleStageWaveControls(ScheduleSettings settings)
    {
        ScheduleStageWavePanel.Children.Clear();
        _scheduleStageWaveSliders.Clear();
        _scheduleStageWaveLabels.Clear();
        ScheduleStageWaveBox.IsChecked = settings.SynchronizeStageWaves;
        var orderedDays = settings.Days.OrderBy(day => day.Date).ToList();
        for (var index = 0; index < orderedDays.Count - 1; index++)
        {
            var day = orderedDays[index];
            var target = settings.StageWaveTargets.FirstOrDefault(item => item.DayLabel == day.DayLabel)?.CumulativeProgress
                ?? ((index + 1d) / orderedDays.Count);
            var label = new TextBlock
            {
                Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap
            };
            var slider = new Slider
            {
                Minimum = 10,
                Maximum = 95,
                TickFrequency = 5,
                IsSnapToTickEnabled = true,
                Value = Math.Round(target * 20) * 5,
                Tag = new ScheduleCustomSliderTag(ScheduleCustomAnchorKind.StageWave, day.DayLabel)
            };
            slider.ValueChanged += ScheduleCustomSlider_ValueChanged;
            _scheduleStageWaveLabels[day.DayLabel] = label;
            _scheduleStageWaveSliders[day.DayLabel] = slider;
            ScheduleStageWavePanel.Children.Add(new StackPanel
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

    private void UpdateScheduleCustomLabels()
    {
        foreach (var pair in _scheduleDayLoadSliders)
        {
            if (_scheduleDayLoadLabels.TryGetValue(pair.Key, out var label))
            {
                label.Text = BuildScheduleDayLoadLabel(pair.Key, pair.Value.Value);
            }
        }

        foreach (var pair in _scheduleStageWaveSliders)
        {
            if (_scheduleStageWaveLabels.TryGetValue(pair.Key, out var label))
            {
                label.Text = $"{pair.Key} 结束前：预计完成本项目阶段进度 {Math.Round(pair.Value.Value)}%";
            }
        }
    }

    private string BuildScheduleDayLoadLabel(string dayLabel, double value)
    {
        var rounded = Math.Round(value);
        if (!_scheduleDayLoadRecommendedRanges.TryGetValue(dayLabel, out var range))
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

    private IReadOnlyList<ScheduleDayLoadTarget> ReadScheduleDayLoadTargetsFromControls(ScheduleSettings? fallback)
    {
        var days = (fallback?.Days ?? _scheduleDays
                .OrderBy(day => day.Date)
                .Select(day => new ScheduleDaySettings(day.Date, day.Start, day.End, ScheduleWorkflow.ParseCourts(day.CourtsText), null, day.UnavailableCourtWindows))
                .ToList())
            .OrderBy(day => day.Date)
            .ToList();
        var fallbackTargets = (fallback?.DayLoadTargets ?? Array.Empty<ScheduleDayLoadTarget>())
            .ToDictionary(target => target.DayLabel, target => target.TargetUtilization, StringComparer.Ordinal);
        return days
            .Select(day =>
            {
                var target = _scheduleDayLoadSliders.TryGetValue(day.DayLabel, out var slider)
                    ? slider.Value / 100d
                    : fallbackTargets.GetValueOrDefault(day.DayLabel, 0.6);
                return new ScheduleDayLoadTarget(day.DayLabel, target, Math.Min(1.0, target + 0.15));
            })
            .ToList();
    }

    private IReadOnlyList<ScheduleStageWaveTarget> ReadScheduleStageWaveTargetsFromControls(ScheduleSettings? fallback)
    {
        var days = (fallback?.Days ?? _scheduleDays
                .OrderBy(day => day.Date)
                .Select(day => new ScheduleDaySettings(day.Date, day.Start, day.End, ScheduleWorkflow.ParseCourts(day.CourtsText), null, day.UnavailableCourtWindows))
                .ToList())
            .OrderBy(day => day.Date)
            .ToList();
        var fallbackTargets = (fallback?.StageWaveTargets ?? Array.Empty<ScheduleStageWaveTarget>())
            .ToDictionary(target => target.DayLabel, target => target.CumulativeProgress, StringComparer.Ordinal);
        var result = new List<ScheduleStageWaveTarget>();
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var target = index == days.Count - 1
                ? 1.0
                : _scheduleStageWaveSliders.TryGetValue(day.DayLabel, out var slider)
                    ? slider.Value / 100d
                    : fallbackTargets.GetValueOrDefault(day.DayLabel, (index + 1d) / days.Count);
            result.Add(new ScheduleStageWaveTarget(day.DayLabel, target));
        }

        return NormalizeScheduleStageWaveTargets(result);
    }

    private static string BuildSingleScheduleCustomFailureMessage(SchedulePlan failedSchedule, ScheduleCustomAnchor anchor)
    {
        var lines = new List<string>
        {
            "当前自定义参数不可行，系统已回滚到上一可行单项目赛程。",
            $"触发项：{anchor.Describe()}。",
            $"仍有 {failedSchedule.UnscheduledMatches.Count} 场无法安排。"
        };

        lines.Add("");
        lines.Add("前几条阻塞原因：");
        var blockers = failedSchedule.UnscheduledMatches.Take(5).ToList();
        if (blockers.Count == 0)
        {
            lines.Add("1. 当前场地、日期、裁判人数或阶段目标组合无法满足全部场次。");
        }
        else
        {
            for (var index = 0; index < blockers.Count; index++)
            {
                var item = blockers[index];
                lines.Add($"{index + 1}. {item.GroupName} · {item.MatchName}：{item.Reason}");
            }
        }

        lines.Add("");
        lines.Add("建议：把当前滑杆调回推荐可行区间，或增加比赛日/场地、增加裁判、延长时间段后再试。");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task ShowSingleScheduleCustomFailureAsync(string message)
    {
        if (_showingSingleScheduleCustomFailureDialog)
        {
            return;
        }

        _showingSingleScheduleCustomFailureDialog = true;
        try
        {
            await ShowInfoAsync("当前自定义参数不可行", message);
        }
        finally
        {
            _showingSingleScheduleCustomFailureDialog = false;
        }
    }
}
