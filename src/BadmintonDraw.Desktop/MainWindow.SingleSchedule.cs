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
    private void ScheduleTimingBoundaryBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateScheduleTimingSplitVisibility();
    }

    private void UseTomorrowScheduleDate_Click(object? sender, RoutedEventArgs e)
    {
        SetScheduleDate(DateOnly.FromDateTime(DateTime.Today.AddDays(1)));
    }

    private void UseThisSaturdayScheduleDate_Click(object? sender, RoutedEventArgs e)
    {
        SetScheduleDate(GetNextOrSameWeekday(DateOnly.FromDateTime(DateTime.Today), DayOfWeek.Saturday));
    }

    private void UseThisSundayScheduleDate_Click(object? sender, RoutedEventArgs e)
    {
        SetScheduleDate(GetNextOrSameWeekday(DateOnly.FromDateTime(DateTime.Today), DayOfWeek.Sunday));
    }

    private void SetScheduleDate(DateOnly date)
    {
        ScheduleDatePicker.SelectedDate = date.ToDateTime(TimeOnly.MinValue);
        SetStatus($"已选择比赛日期：{date:yyyy-MM-dd}。");
    }

    private static DateOnly GetNextOrSameWeekday(DateOnly startDate, DayOfWeek targetDay)
    {
        var offset = ((int)targetDay - (int)startDate.DayOfWeek + 7) % 7;
        return startDate.AddDays(offset);
    }

    private void AddScheduleDay_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            AddCurrentScheduleDay();
        }
        catch (Exception ex) when (ex is DrawValidationException or InvalidOperationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void RemoveScheduleDay_Click(object? sender, RoutedEventArgs e)
    {
        if (ScheduleDaysList.SelectedItem is ScheduleDayWorkflowRequest selectedDay)
        {
            _scheduleDays.Remove(selectedDay);
            RefreshScheduleResourcePanel();
            ClearSchedulePreview();
            SetStatus("已删除选中的赛程日。");
        }
    }

    private void ScheduleDaysList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshScheduleResourcePanel();
    }

    private void AddScheduleCourtBlock_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var selectedDay = GetSelectedScheduleDayForResources();
            var start = ParseScheduleResourceTime(ScheduleResourceStartBox.Text, "资源时段开始时间");
            var end = ParseScheduleResourceTime(ScheduleResourceEndBox.Text, "资源时段结束时间");
            ValidateScheduleResourceTimeRange(start, end);
            var courts = string.IsNullOrWhiteSpace(ScheduleUnavailableCourtsBox.Text)
                ? Array.Empty<string>()
                : ScheduleWorkflow.ParseCourts(ScheduleUnavailableCourtsBox.Text).ToArray();

            var blocks = (selectedDay.UnavailableCourtWindows ?? [])
                .ToList();
            blocks.Add(new ScheduleCourtAvailabilityBlock(start, end, courts));

            UpdateSelectedScheduleDayResources(selectedDay with
            {
                UnavailableCourtWindows = blocks
                    .OrderBy(block => block.StartTime)
                    .ThenBy(block => block.EndTime)
                    .ToList()
            });
            SetStatus($"{selectedDay.Date:yyyy-MM-dd} 已添加不可用场地时段，请重新生成赛程预览。", isWarning: true);
        }
        catch (Exception ex) when (ex is DrawValidationException or InvalidOperationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void RemoveScheduleResourceItem_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var selectedDay = GetSelectedScheduleDayForResources();
            if (ScheduleResourceItemsList.SelectedItem is not ScheduleResourceListItem selectedResource)
            {
                throw new DrawValidationException("请选择要删除的资源项。");
            }

            var blocks = (selectedDay.UnavailableCourtWindows ?? []).ToList();
            if (selectedResource.Index < 0 || selectedResource.Index >= blocks.Count)
            {
                throw new DrawValidationException("所选不可用场地时段已经变化，请重新选择。");
            }

            blocks.RemoveAt(selectedResource.Index);
            UpdateSelectedScheduleDayResources(selectedDay with { UnavailableCourtWindows = blocks });

            SetStatus($"{selectedDay.Date:yyyy-MM-dd} 已删除资源项，请重新生成赛程预览。", isWarning: true);
        }
        catch (Exception ex) when (ex is DrawValidationException or InvalidOperationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void CompetitionModeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdateEventKindForMode();
        UpdateKnockoutGoalVisibility();
        UpdateDrawPdfOptionsVisibility();
        ClearSchedulePreview();
    }

    private void KnockoutGoalBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdatePlacementPlayoffVisibility();
        UpdateScheduleTimingSplitVisibility();
        UpdateDrawPdfOptionsVisibility();
        ClearSchedulePreview();
    }

    private void GroupCountBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdateKnockoutGoalVisibility();
        UpdateDrawPdfOptionsVisibility();
        ClearSchedulePreview();
    }

    private void DrawExportFormatBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_uiReady)
        {
            UpdateDrawPdfOptionsVisibility();
        }
    }

    private void ScheduleVenueBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_uiReady)
        {
            ApplyScheduleCourtPreset();
        }
    }

    private async void ExportSchedule_Click(object? sender, RoutedEventArgs e)
    {
        if ((_latestSchedule is null && !TryGenerateSchedule())
            || _latestSchedule is null
            || _latestResult is null)
        {
            return;
        }

        if (!_latestSchedule.IsComplete)
        {
            SetStatus(
                $"当前赛程资源不足，仍有 {_latestSchedule.UnscheduledMatches.Count} 场无法安排；不支持导出不完整赛程。",
                isWarning: true);
            return;
        }

        var exportFormat = GetExportFormat(ScheduleExportFormatBox);
        var suggestedName = ScheduleWorkflow.BuildDefaultScheduleFileName(_latestResult, _loadedInputPath ?? InputPathBox.Text, exportFormat);
        var path = await PickSavePath("保存赛程表", suggestedName, exportFormat);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var schedulePaths = _scheduleWorkflow.ExportFiles(path, exportFormat, _latestSchedule);
            SetStatus($"完整赛程表已导出：{FormatOutputPaths(schedulePaths)}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private async void ExportFirstDayPackage_Click(object? sender, RoutedEventArgs e)
    {
        if ((_latestSchedule is null && !TryGenerateSchedule())
            || _latestSchedule is null
            || _latestWorkflowResult is null)
        {
            return;
        }

        if (!_latestSchedule.IsComplete)
        {
            SetStatus(
                $"当前赛程资源不足，仍有 {_latestSchedule.UnscheduledMatches.Count} 场无法安排；不支持导出不完整赛程。",
                isWarning: true);
            return;
        }

        var outputDirectory = await PickFolderPath("选择首日材料包保存文件夹");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        try
        {
            var package = _progressState is not null
                ? _progressWorkflow.ExportFirstDayPackage(
                    _progressState,
                    outputDirectory,
                    includePrintablePdf: true,
                    GetDrawVisualOptions(WorkflowExportFormat.A4Pdf))
                : _progressWorkflow.ExportFirstDayPackage(
                    outputDirectory,
                    _loadedInputPath ?? InputPathBox.Text,
                    _latestWorkflowResult,
                    _latestSchedule,
                    includePrintablePdf: true,
                    GetDrawVisualOptions(WorkflowExportFormat.A4Pdf));
            SetStatus($"{package.DayLabel} 首日材料包已导出到：{package.OutputDirectory}（共 {package.OutputPaths.Count} 个文件）。");
        }
        catch (Exception ex) when (IsHandledWorkflowException(ex))
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private async void ImportMatchRecordAndExportNext_Click(object? sender, RoutedEventArgs e)
    {
        if ((_latestSchedule is null && !TryGenerateSchedule()) || _latestSchedule is null)
        {
            return;
        }

        if (!_latestSchedule.IsComplete)
        {
            SetStatus(
                $"当前赛程资源不足，仍有 {_latestSchedule.UnscheduledMatches.Count} 场无法安排；不支持导出不完整赛程。",
                isWarning: true);
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择已填写的赛程记录表",
            AllowMultiple = true,
            FileTypeFilter = [ExcelFileType]
        });
        var paths = files.Select(file => file.TryGetLocalPath()).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_progressFilePath))
        {
            await ImportMatchRecordsIntoProgress(paths!);
            return;
        }

        try
        {
            var importResult = _scheduleWorkflow.ImportMatchRecords(paths!);
            if (importResult.ExpectedMatchCount == 0)
            {
                SetStatus("所选记录表中没有识别到可处理的比赛场次，请确认是本工具导出的赛程记录表。", isError: true);
                return;
            }

            var nextDayLabel = ScheduleWorkflow.GetNextMatchRecordDayLabel(_latestSchedule, importResult);
            if (string.IsNullOrWhiteSpace(nextDayLabel))
            {
                SetStatus("已读取比赛结果，但当前赛程没有下一比赛日可导出。", isWarning: true);
                return;
            }

            if (importResult.HasWarnings)
            {
                var confirmed = await ConfirmAsync(
                    "赛程记录表提醒",
                    ScheduleWorkflow.BuildMatchRecordImportWarning(importResult, nextDayLabel));
                if (!confirmed)
                {
                    SetStatus("已取消导出，请修正记录表后重新导入。", isWarning: true);
                    return;
                }
            }

            if (_latestWorkflowResult is null)
            {
                SetStatus("请先生成或打开完整赛事，再导出下一比赛日材料包。", isError: true);
                return;
            }

            var outputDirectory = await PickFolderPath("选择下一比赛日材料包保存文件夹");
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return;
            }

            var package = _progressWorkflow.ExportNextDayPackage(
                outputDirectory,
                _loadedInputPath ?? InputPathBox.Text,
                _latestWorkflowResult,
                _latestSchedule,
                importResult,
                includePrintablePdf: true,
                GetDrawVisualOptions(WorkflowExportFormat.A4Pdf));
            var pendingText = importResult.PendingMatchNames.Count > 0
                ? $"，顺延 {importResult.PendingMatchNames.Count} 场未决比赛"
                : "";
            SetStatus(
                $"已从 {paths.Length} 张记录表累计读取 {importResult.Results.Count} 场结果{pendingText}，"
                + $"并导出 {package.DayLabel} 材料包到：{package.OutputDirectory}（共 {package.OutputPaths.Count} 个文件）。");
        }
        catch (Exception ex) when (IsHandledWorkflowException(ex))
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private async void CreateProgress_Click(object? sender, RoutedEventArgs e)
    {
        if (_progressState is not null)
        {
            SetStatus("当前已经打开赛事存档；后续导入会自动更新该文件。", isWarning: true);
            return;
        }

        if ((_latestSchedule is null && !TryGenerateSchedule())
            || _latestSchedule is null
            || _latestWorkflowResult is null
            || _latestResult is null)
        {
            return;
        }

        if (!_latestSchedule.IsComplete)
        {
            SetStatus("当前赛程不完整，不能创建赛事存档。", isWarning: true);
            return;
        }

        var path = await PickProgressSavePath(
            "创建赛事存档",
            TournamentProgressWorkflow.BuildDefaultFileName(
                _latestResult,
                _loadedInputPath ?? InputPathBox.Text));
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _progressState = _progressWorkflow.Create(
                path,
                _loadedInputPath ?? InputPathBox.Text,
                _latestWorkflowResult,
                _latestSchedule);
            _progressFilePath = path;
            ClearSingleScheduleUndoStack();
            UpdateProgressDisplay();
            SetStatus($"赛事存档已创建：{path}");
        }
        catch (Exception ex) when (IsHandledWorkflowException(ex))
        {
            SetStatus(ex.Message, isError: true);
        }
    }


    private async void OpenProgress_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开赛事存档",
            AllowMultiple = false,
            FileTypeFilter = [ProgressFileType]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            ApplyProgressState(_progressWorkflow.Open(path), path);
            SetStatus(
                $"已打开赛事存档：累计完成 {_progressState!.Results.Count} 场，"
                + $"待决 {_progressState.RemainingMatchCount} 场。");
        }
        catch (Exception ex) when (IsHandledWorkflowException(ex))
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private async System.Threading.Tasks.Task ImportMatchRecordsIntoProgress(IReadOnlyList<string> filePaths)
    {
        if (string.IsNullOrWhiteSpace(_progressFilePath) || _progressState is null)
        {
            throw new InvalidOperationException("请先创建或打开赛事存档。");
        }

        try
        {
            var preview = _progressWorkflow.PreviewImport(_progressFilePath, filePaths);
            if (preview.FilesToImport > 0 && preview.SelectedImportResult.ExpectedMatchCount == 0)
            {
                SetStatus("所选记录表中没有识别到可处理的比赛场次。", isError: true);
                return;
            }

            var projectedState = _progressState with
            {
                Results = preview.ProjectedCumulativeResult.Results,
                PendingMatchNames = preview.ProjectedCumulativeResult.PendingMatchNames,
                ProcessedDayLabels = preview.ProjectedCumulativeResult.DayLabels
            };
            var nextDayLabel = TournamentProgressWorkflow.GetNextMatchRecordDayLabel(projectedState);
            if (preview.HasWarnings)
            {
                var confirmed = await ConfirmAsync(
                    "更新赛事存档",
                    TournamentProgressWorkflow.BuildImportConfirmation(preview, nextDayLabel));
                if (!confirmed)
                {
                    SetStatus("已取消更新赛事存档。", isWarning: true);
                    return;
                }
            }

            var outcome = _progressWorkflow.Import(
                _progressFilePath,
                filePaths,
                allowCorrections: preview.Corrections.Count > 0);
            _progressState = outcome.State;
            _latestSchedule = outcome.State.Snapshot.Schedule;
            ClearSingleScheduleUndoStack();
            UpdateProgressDisplay();

            nextDayLabel = TournamentProgressWorkflow.GetNextMatchRecordDayLabel(outcome.State);
            if (string.IsNullOrWhiteSpace(nextDayLabel))
            {
                SetStatus(
                    $"赛事存档已更新，累计完成 {outcome.State.Results.Count} 场，"
                    + $"待决 {outcome.State.RemainingMatchCount} 场；当前没有下一比赛日需要导出。");
                return;
            }

            var outputDirectory = await PickFolderPath("选择下一比赛日材料包保存文件夹");
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                SetStatus(
                    $"赛事存档已更新，累计完成 {outcome.State.Results.Count} 场，"
                    + $"待决 {outcome.State.RemainingMatchCount} 场；已取消导出下一比赛日材料包。",
                    isWarning: true);
                return;
            }

            var package = _progressWorkflow.ExportNextDayPackage(
                outcome.State,
                outputDirectory,
                includePrintablePdf: true,
                GetDrawVisualOptions(WorkflowExportFormat.A4Pdf));
            SetStatus(
                $"赛事存档已更新：新增 {preview.NewResultCount} 场结果，"
                + $"累计完成 {outcome.State.Results.Count} 场，待决 {outcome.State.RemainingMatchCount} 场；"
                + $"{package.DayLabel} 材料包已导出到：{package.OutputDirectory}（共 {package.OutputPaths.Count} 个文件）。");
        }
        catch (Exception ex) when (IsHandledWorkflowException(ex))
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private bool TryGenerateSchedule(ScheduleSettings? settingsOverride = null, bool rollbackCustomOnFailure = false, ScheduleCustomAnchor? customAnchor = null)
    {
        var previousSchedule = _latestSchedule;
        var previousSettings = _singleScheduleLastAcceptedSettings;
        try
        {
            if (_latestResult is null && !TryGenerate())
            {
                return false;
            }

            if (_latestResult is null)
            {
                throw new DrawValidationException("请先预览抽签。");
            }

            var settings = settingsOverride ?? BuildScheduleSettings();
            var generatedSchedule = _scheduleWorkflow.Generate(_latestResult, settings);
            if (settings.AutoSchedulingStrategy == ScheduleAutoSchedulingStrategy.Custom
                && rollbackCustomOnFailure
                && previousSchedule is not null
                && !generatedSchedule.IsComplete)
            {
                _latestSchedule = previousSchedule;
                _singleScheduleLastAcceptedSettings = previousSettings ?? previousSchedule.Settings;
                ScheduleList.ItemsSource = FormatScheduleRows(_latestSchedule);
                UpdateScheduleConstraintReport(_latestSchedule);
                RefreshScheduleBoardWindow(_scheduleBoardWindowDayBox?.SelectedItem?.ToString());
                RebuildScheduleCustomSchedulingControls();
                SetStatus(
                    $"当前自定义参数不可行，已回滚到上一可行单项目赛程；仍有 {generatedSchedule.UnscheduledMatches.Count} 场无法安排。",
                    isError: true);
                _ = ShowSingleScheduleCustomFailureAsync(BuildSingleScheduleCustomFailureMessage(generatedSchedule, customAnchor ?? ScheduleCustomAnchor.None));
                return false;
            }

            _latestSchedule = generatedSchedule;
            _singleScheduleLastAcceptedSettings = settings;
            ClearSingleScheduleUndoStack();
            UpdateScheduleConstraintReport(_latestSchedule);
            ClearProgressReference();
            RebuildScheduleCustomSchedulingControls();
            ScheduleSummaryText.Text = _latestSchedule.IsComplete
                ? $"已生成 {_latestSchedule.Matches.Count} 场，预计 {_latestSchedule.DayCount} 个比赛日。{ScheduleWorkflow.BuildScheduleCapacityText(settings)}"
                  + BuildScheduleQualitySentence(_latestSchedule.QualityReport)
                : $"已安排 {_latestSchedule.Matches.Count} 场，未安排 {_latestSchedule.UnscheduledMatches.Count} 场，共 {_latestSchedule.TotalMatchCount} 场。{ScheduleWorkflow.BuildScheduleCapacityText(settings)}"
                  + BuildScheduleQualitySentence(_latestSchedule.QualityReport);
            ScheduleList.ItemsSource = FormatScheduleRows(_latestSchedule);
            RefreshScheduleBoardWindow();
            var hasConstraintWarnings = _latestScheduleConstraintReport is { SevereCount: > 0 } or { WarningCount: > 0 };
            SetStatus(_latestSchedule.IsComplete
                ? "赛程预览已生成，可导出赛程 Excel。"
                : "赛程资源不足：预览已保留，未安排场次会在列表底部显示。",
                isWarning: !_latestSchedule.IsComplete || hasConstraintWarnings);
            return true;
        }
        catch (Exception ex) when (ex is DrawValidationException or InvalidOperationException or IOException)
        {
            if (rollbackCustomOnFailure && previousSchedule is not null)
            {
                _latestSchedule = previousSchedule;
                _singleScheduleLastAcceptedSettings = previousSettings ?? previousSchedule.Settings;
                ScheduleList.ItemsSource = FormatScheduleRows(_latestSchedule);
                UpdateScheduleConstraintReport(_latestSchedule);
                RefreshScheduleBoardWindow(_scheduleBoardWindowDayBox?.SelectedItem?.ToString());
                RebuildScheduleCustomSchedulingControls();
            }

            SetStatus(ex.Message, isError: true);
            return false;
        }
    }

    private ScheduleSettings BuildScheduleSettings()
    {
        var strategy = GetScheduleAutoSchedulingStrategy();
        var settings = BuildScheduleSettings(strategy, includeCustomTargets: false);
        if (strategy != ScheduleAutoSchedulingStrategy.Custom)
        {
            return settings;
        }

        EnsureScheduleCustomRecommendation();
        var recommended = _singleScheduleRecommendedCustomSettings;
        return settings with
        {
            DayLoadTargets = ReadScheduleDayLoadTargetsFromControls(recommended),
            SynchronizeStageWaves = ScheduleStageWaveBox.IsChecked == true,
            StageWaveTargets = ReadScheduleStageWaveTargetsFromControls(recommended)
        };
    }

    private ScheduleSettings BuildScheduleSettings(
        ScheduleAutoSchedulingStrategy strategy,
        bool includeCustomTargets)
    {
        var boundary = ShouldShowScheduleTimingSplit()
            ? GetSelectedComboBoxTagInt(ScheduleTimingBoundaryBox)
            : 0;
        var settings = ScheduleWorkflow.BuildSettings(
            _scheduleDays.ToList(),
            ParsePositiveInt(ScheduleMatchMinutesBox.Text, boundary > 0 ? "分界线后每场分钟" : "每场分钟"),
            ParsePositiveInt(GetSelectedComboBoxText(ScheduleMaxMatchesBox), boundary > 0 ? "分界线后每日最多场" : "每日最多场"),
            boundary > 0 ? boundary : null,
            boundary > 0 ? ParsePositiveInt(BeforeBoundaryMatchMinutesBox.Text, "分界线前每场分钟") : null,
            boundary > 0 ? ParsePositiveInt(GetSelectedComboBoxText(BeforeBoundaryMaxMatchesBox), "分界线前每日最多场") : null,
            GetScheduleConstraintProfile(),
            strategy,
            ParseOptionalPositiveInt(ScheduleRefereeCountBox.Text, "裁判人数"));
        if (!includeCustomTargets || strategy != ScheduleAutoSchedulingStrategy.Custom)
        {
            return settings;
        }

        EnsureScheduleCustomRecommendation();
        var recommended = _singleScheduleRecommendedCustomSettings;
        return settings with
        {
            DayLoadTargets = ReadScheduleDayLoadTargetsFromControls(recommended),
            SynchronizeStageWaves = ScheduleStageWaveBox.IsChecked == true,
            StageWaveTargets = ReadScheduleStageWaveTargetsFromControls(recommended)
        };
    }

    private void AddCurrentScheduleDay(bool showStatus = true)
    {
        if (ScheduleDatePicker.SelectedDate is not DateTime selectedDate)
        {
            throw new DrawValidationException("请选择比赛日期。");
        }

        var date = DateOnly.FromDateTime(selectedDate.Date);
        if (!TimeOnly.TryParse(GetSelectedComboBoxText(ScheduleStartBox), out var start))
        {
            throw new DrawValidationException("请选择开始时间。");
        }

        if (!TimeOnly.TryParse(GetSelectedComboBoxText(ScheduleEndBox), out var end))
        {
            throw new DrawValidationException("请选择结束时间。");
        }

        if (end <= start)
        {
            throw new DrawValidationException("赛程结束时间必须晚于开始时间。");
        }

        var courtsText = ScheduleCourtsBox.Text ?? "";
        _ = ScheduleWorkflow.ParseCourts(courtsText);
        var venue = ScheduleVenueBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? "自定义"
            : "自定义";
        var existing = _scheduleDays.FirstOrDefault(day => day.Date == date);
        var unavailableCourtWindows = existing?.UnavailableCourtWindows;
        if (existing is not null)
        {
            _scheduleDays.Remove(existing);
        }

        var addedDay = new ScheduleDayWorkflowRequest(
            date,
            start,
            end,
            venue,
            courtsText,
            unavailableCourtWindows);
        _scheduleDays.Add(addedDay);
        ScheduleDaysList.SelectedItem = addedDay;
        RefreshScheduleResourcePanel();
        ClearSchedulePreview();
        if (showStatus)
        {
            SetStatus("已添加赛程日。");
        }
    }

    private ScheduleDayWorkflowRequest GetSelectedScheduleDayForResources()
    {
        if (ScheduleDaysList.SelectedItem is ScheduleDayWorkflowRequest selectedDay)
        {
            return selectedDay;
        }

        throw new DrawValidationException("请先在赛程日列表中选择一个比赛日。");
    }

    private void UpdateSelectedScheduleDayResources(ScheduleDayWorkflowRequest updatedDay)
    {
        if (ScheduleDaysList.SelectedItem is not ScheduleDayWorkflowRequest selectedDay)
        {
            throw new DrawValidationException("请先在赛程日列表中选择一个比赛日。");
        }

        var index = _scheduleDays.IndexOf(selectedDay);
        if (index < 0)
        {
            throw new DrawValidationException("所选赛程日已经变化，请重新选择。");
        }

        _scheduleDays[index] = updatedDay;
        ScheduleDaysList.SelectedItem = updatedDay;
        RefreshScheduleResourcePanel();
        ClearSchedulePreview();
    }

    private void RefreshScheduleResourcePanel()
    {
        if (ScheduleDaysList.SelectedItem is not ScheduleDayWorkflowRequest selectedDay)
        {
            ScheduleResourceSelectedDayText.Text = "未选择赛程日";
            ScheduleResourceItemsList.ItemsSource = Array.Empty<ScheduleResourceListItem>();
            return;
        }

        var items = new List<ScheduleResourceListItem>();
        var courtBlocks = selectedDay.UnavailableCourtWindows ?? [];
        for (var i = 0; i < courtBlocks.Count; i++)
        {
            var block = courtBlocks[i];
            var courtsText = block.Courts.Count == 0
                ? "全部场地"
                : string.Join("、", block.Courts);
            items.Add(new ScheduleResourceListItem(
                i,
                $"{block.StartTime:HH:mm}-{block.EndTime:HH:mm} · 不可用场地 {courtsText}"));
        }

        ScheduleResourceSelectedDayText.Text = $"{selectedDay.Date:MM-dd} · 不可用 {items.Count} 条";
        ScheduleResourceItemsList.ItemsSource = items;
    }

    private static TimeOnly ParseScheduleResourceTime(string? value, string fieldName)
    {
        if (!TimeOnly.TryParse(value?.Trim(), out var time))
        {
            throw new DrawValidationException($"{fieldName}必须形如 14:00。");
        }

        return time;
    }

    private static void ValidateScheduleResourceTimeRange(TimeOnly start, TimeOnly end)
    {
        if (end <= start)
        {
            throw new DrawValidationException("资源时段结束时间必须晚于开始时间。");
        }
    }

    private async System.Threading.Tasks.Task<string?> PickSavePath(
        string title,
        string suggestedName,
        WorkflowExportFormat format = WorkflowExportFormat.Excel)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = WorkflowExportHelpers.GetExtension(format).TrimStart('.'),
            FileTypeChoices = [GetFileType(format)]
        });
        return file?.TryGetLocalPath();
    }

    private async System.Threading.Tasks.Task<string?> PickProgressSavePath(
        string title,
        string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "szbd",
            FileTypeChoices = [ProgressFileType]
        });
        return file?.TryGetLocalPath();
    }

    private async System.Threading.Tasks.Task<string?> PickFolderPath(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private static FilePickerFileType GetFileType(WorkflowExportFormat format)
    {
        return format switch
        {
            WorkflowExportFormat.Jpeg => JpegFileType,
            WorkflowExportFormat.Png => PngFileType,
            WorkflowExportFormat.A4Pdf => PdfFileType,
            _ => ExcelFileType
        };
    }

    private static WorkflowExportFormat GetExportFormat(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem item
            && item.Tag is not null
            && Enum.TryParse<WorkflowExportFormat>(item.Tag.ToString(), out var format)
                ? format
                : WorkflowExportFormat.Excel;
    }

    private DrawResultVisualOptions GetDrawVisualOptions()
    {
        return GetDrawVisualOptions(GetExportFormat(DrawExportFormatBox));
    }

    private DrawResultVisualOptions GetDrawVisualOptions(WorkflowExportFormat format)
    {
        return _latestResult is not null
            && _latestResult.Settings.IsKnockout
            && (format is WorkflowExportFormat.A4Pdf or WorkflowExportFormat.All)
                ? new DrawResultVisualOptions(
                    GetPdfTileValue(PdfRowsBox, "PDF 行数"),
                    GetPdfTileValue(PdfColumnsBox, "PDF 列数"))
                : new DrawResultVisualOptions();
    }

    private static int GetPdfTileValue(TextBox textBox, string name)
    {
        if (!int.TryParse(textBox.Text?.Trim(), out var value) || value < 1 || value > 50)
        {
            throw new DrawValidationException($"{name}必须是 1 到 50 之间的整数。");
        }

        return value;
    }

    private static string GetSelectedComboBoxText(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? string.Empty
            : comboBox.Text ?? string.Empty;
    }

    private static int GetSelectedComboBoxTagInt(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var value))
        {
            return value;
        }

        return 0;
    }

    private static string FormatOutputPaths(IReadOnlyList<string> outputPaths)
    {
        return string.Join("；", outputPaths);
    }


    private void ResetPreview()
    {
        _participants = [];
        _importWarnings = [];
        _participantImportWarnings = [];
        _latestWorkflowResult = null;
        _latestResult = null;
        _latestSchedule = null;
        _loadedInputPath = null;
        ClearProgressReference();
        ParticipantCountText.Text = "-";
        GroupCountStatText.Text = "-";
        EventKindStatText.Text = "-";
        PreviewStateText.Text = "待预览";
        SummaryText.Text = "尚未生成抽签预览";
        SetWarnings([]);
        GroupsList.ItemsSource = Array.Empty<PreviewGroupRow>();
        RoundOneList.ItemsSource = Array.Empty<PreviewGroupRow>();
        ByeList.ItemsSource = Array.Empty<PreviewGroupRow>();
        ClearSchedulePreview();
        UpdateDrawPdfOptionsVisibility();
    }

    private void ApplyProgressState(TournamentProgressState state, string filePath)
    {
        var wasReady = _uiReady;
        _uiReady = false;
        try
        {
            _progressState = state;
            _progressFilePath = filePath;
            InputPathBox.Text = state.Snapshot.SourceInputPath ?? state.Snapshot.EventName;
            _loadedInputPath = state.Snapshot.SourceInputPath;
            _latestWorkflowResult = TournamentProgressWorkflow.BuildDrawWorkflowResult(state);
            _latestResult = state.Snapshot.DrawResult;
            _latestSchedule = state.Snapshot.Schedule;
            _participants = state.Snapshot.Participants;
            _participantImportWarnings = state.Snapshot.ImportWarnings;
            _importWarnings = _latestWorkflowResult.WarningMessages;
            ClearSingleScheduleUndoStack();

            CompetitionModeBox.SelectedIndex = state.Snapshot.DrawResult.Settings.CompetitionMode switch
            {
                CompetitionMode.SinglesRoundRobin => 1,
                CompetitionMode.TeamKnockout => 2,
                CompetitionMode.TeamRoundRobin => 3,
                _ => 0
            };
            EventKindBox.SelectedIndex = state.Snapshot.DrawResult.Settings.EventKind switch
            {
                EventKind.Singles => 0,
                EventKind.Team => 2,
                _ => 1
            };
            GroupCountBox.Text = state.Snapshot.DrawResult.Settings.GroupCount.ToString();
            SeedBox.Text = state.Snapshot.DrawResult.Settings.RandomSeed;
            KnockoutGoalBox.SelectedIndex =
                state.Snapshot.DrawResult.Settings.KnockoutGoal == KnockoutGoal.Champion ? 1 : 0;
            PlacementPlayoffBox.SelectedIndex = state.Snapshot.DrawResult.Settings.PlacementPlayoff switch
            {
                PlacementPlayoff.ThirdPlace => 1,
                PlacementPlayoff.ThirdToEighth => 2,
                _ => 0
            };
            ApplyStoredScheduleSettings(state.Snapshot.Schedule.Settings);
        }
        finally
        {
            _uiReady = wasReady;
        }

        ParticipantCountText.Text = state.Snapshot.DrawResult.Audit.ParticipantCount.ToString();
        GroupCountStatText.Text = state.Snapshot.DrawResult.Audit.GroupCount.ToString();
        EventKindStatText.Text = WorkflowLabels.GetEventKindDisplay(state.Snapshot.DrawResult.Settings.EventKind);
        PreviewStateText.Text = "存档已载入";
        SummaryText.Text = $"已从赛事存档恢复 {state.Snapshot.DrawResult.Groups.Count} 个小组";
        SetWarnings(_importWarnings);
        GroupsList.ItemsSource = FormatGroups(state.Snapshot.DrawResult.Groups);
        RoundOneList.ItemsSource = FormatGroups(state.Snapshot.DrawResult.RoundOneGroups);
        ByeList.ItemsSource = FormatGroups(state.Snapshot.DrawResult.ByeGroups);
        ScheduleList.ItemsSource = FormatScheduleRows(state.Snapshot.Schedule);
        UpdateScheduleConstraintReport(state.Snapshot.Schedule);
        RefreshScheduleBoardWindow();
        ScheduleSummaryText.Text =
            $"已恢复 {state.Snapshot.Schedule.Matches.Count} 场赛程，累计完成 {state.Results.Count} 场。"
            + BuildScheduleQualitySentence(state.Snapshot.Schedule.QualityReport);
        UpdateEventKindForMode();
        UpdateKnockoutGoalVisibility();
        UpdateDrawPdfOptionsVisibility();
        UpdateScheduleTimingSplitVisibility();
        UpdateProgressDisplay();
    }

    private void ApplyStoredScheduleSettings(ScheduleSettings settings)
    {
        _scheduleDays.Clear();
        foreach (var day in settings.Days)
        {
            _scheduleDays.Add(new ScheduleDayWorkflowRequest(
                day.Date,
                day.DayStart,
                day.DayEnd,
                "赛事存档",
                string.Join("，", day.Courts),
                day.UnavailableCourtWindows));
        }

        ScheduleDaysList.SelectedItem = _scheduleDays.FirstOrDefault();
        RefreshScheduleResourcePanel();

        ScheduleMatchMinutesBox.Text = settings.MatchMinutes.ToString();
        SelectComboBoxText(ScheduleMaxMatchesBox, settings.MaxMatchesPerEntrantPerDay.ToString());
        ScheduleRefereeCountBox.Text = settings.RefereeCount?.ToString() ?? "";
        if (settings.HasKnockoutTimingSplit)
        {
            SelectComboBoxTag(
                ScheduleTimingBoundaryBox,
                settings.KnockoutTimingBoundaryEntrants!.Value.ToString());
            BeforeBoundaryMatchMinutesBox.Text = settings.BeforeBoundaryTiming!.MatchMinutes.ToString();
            SelectComboBoxText(
                BeforeBoundaryMaxMatchesBox,
                settings.BeforeBoundaryTiming.MaxMatchesPerEntrantPerDay.ToString());
        }
        else
        {
            SelectComboBoxTag(ScheduleTimingBoundaryBox, "0");
        }

        SelectComboBoxTag(ScheduleConstraintProfileBox, settings.ConstraintProfile.ToString());
        SelectScheduleAutoStrategyWithoutEvent(settings.AutoSchedulingStrategy);
    }

    private void SelectScheduleAutoStrategyWithoutEvent(ScheduleAutoSchedulingStrategy strategy)
    {
        ScheduleAutoStrategyBox.SelectionChanged -= ScheduleAutoStrategyBox_SelectionChanged;
        SelectComboBoxTag(ScheduleAutoStrategyBox, strategy.ToString());
        ScheduleAutoStrategyBox.SelectionChanged += ScheduleAutoStrategyBox_SelectionChanged;
    }

    private static void SelectComboBoxText(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private static void SelectComboBoxTag(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void ClearProgressReference()
    {
        _progressFilePath = null;
        _progressState = null;
        UpdateProgressDisplay();
    }

    private void UpdateProgressDisplay()
    {
        if (ProgressFileText is null)
        {
            return;
        }

        ProgressFileText.Text = string.IsNullOrWhiteSpace(_progressFilePath) || _progressState is null
            ? "尚未创建或打开赛事存档"
            : $"{Path.GetFileName(_progressFilePath)} · 已完成 {_progressState.Results.Count} 场"
              + $" · 待决 {_progressState.RemainingMatchCount} 场";
    }


    private IReadOnlyList<SchedulePreviewRow> FormatScheduleRows(SchedulePlan schedule)
    {
        var scheduledBackground = ThemeBrush("AppSurfaceBrush", Color.FromRgb(255, 255, 255));
        var scheduledBorder = ThemeBrush("AppSoftBorderBrush", Color.FromRgb(226, 232, 240));
        var scheduledBadgeBackground = ThemeBrush("AppInfoCardBackgroundBrush", Color.FromRgb(236, 246, 255));
        var scheduledBadgeForeground = ThemeBrush("AppInfoTextBrush", Color.FromRgb(15, 95, 159));
        var unscheduledBackground = ThemeBrush("AppErrorCardBackgroundBrush", Color.FromRgb(255, 247, 247));
        var unscheduledBorder = ThemeBrush("AppErrorCardBorderBrush", Color.FromRgb(246, 190, 190));
        var unscheduledBadgeBackground = ThemeBrush("AppWarningCardBackgroundBrush", Color.FromRgb(255, 228, 230));
        var unscheduledBadgeForeground = ThemeBrush("AppErrorTextBrush", Color.FromRgb(159, 18, 57));

        var rows = schedule.Matches
            .Select(match => new SchedulePreviewRow(
                match.Order.ToString(),
                "已安排",
                match.DayLabel,
                match.TimeRange,
                match.Court,
                match.GroupName,
                match.Phase,
                match.MatchName,
                match.SideA,
                match.SideB,
                match.Note,
                scheduledBackground,
                scheduledBorder,
                scheduledBadgeBackground,
                scheduledBadgeForeground))
            .ToList();
        rows.AddRange(schedule.UnscheduledMatches.Select(match =>
            new SchedulePreviewRow(
                match.Order.ToString(),
                "未安排",
                "待排期",
                "未安排",
                "未定",
                match.GroupName,
                match.Phase,
                match.MatchName,
                match.SideA,
                match.SideB,
                match.Reason,
                unscheduledBackground,
                unscheduledBorder,
                unscheduledBadgeBackground,
                unscheduledBadgeForeground)));
        return rows.Count == 0
            ? [new SchedulePreviewRow("-", "空", "暂无", "-", "-", "-", "暂无赛程", "生成赛程后显示", "", "", "", scheduledBackground, scheduledBorder, scheduledBadgeBackground, scheduledBadgeForeground)]
            : rows;
    }

    private void UpdateScheduleConstraintReport(SchedulePlan? schedule)
    {
        _latestScheduleConstraintReport = schedule is null ? null : _scheduleConstraintAnalyzer.Analyze(schedule);
        UpdateScheduleConstraintButton();
    }

    private void UpdateScheduleConstraintButton()
    {
        if (_latestScheduleConstraintReport is null)
        {
            ScheduleConstraintButton.Content = "查看提醒";
            ScheduleConstraintButton.IsEnabled = false;
            return;
        }

        ScheduleConstraintButton.IsEnabled = true;
        ScheduleConstraintButton.Content = _latestScheduleConstraintReport.HasIssues
            ? $"提醒 {_latestScheduleConstraintReport.Issues.Count}"
            : "查看提醒";
    }

    private static string FormatScheduleConstraintSeverity(ScheduleConstraintSeverity severity)
    {
        return severity switch
        {
            ScheduleConstraintSeverity.Severe => "严重",
            ScheduleConstraintSeverity.Warning => "警告",
            _ => "提醒"
        };
    }

    private void SetWarnings(IReadOnlyList<string> warnings)
    {
        var message = FormatWarnings(warnings);
        WarningText.Text = message;
        WarningPanel.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private static string FormatWarnings(IReadOnlyList<string> warnings)
    {
        return warnings.Count == 0
            ? ""
            : string.Join(Environment.NewLine, warnings.Take(5));
    }

    private void ClearSchedulePreview()
    {
        _latestSchedule = null;
        _latestScheduleConstraintReport = null;
        _singleScheduleRecommendedCustomSettings = null;
        _singleScheduleLastAcceptedSettings = null;
        _pendingScheduleCustomAnchor = ScheduleCustomAnchor.None;
        _scheduleCustomRecalculateTimer?.Stop();
        _scheduleDayLoadSliders.Clear();
        _scheduleDayLoadLabels.Clear();
        _scheduleDayLoadRecommendedRanges.Clear();
        _scheduleStageWaveSliders.Clear();
        _scheduleStageWaveLabels.Clear();
        ClearSingleScheduleUndoStack();
        ScheduleSummaryText.Text = "尚未生成赛程";
        UpdateScheduleConstraintButton();
        ScheduleList.ItemsSource = Array.Empty<SchedulePreviewRow>();
        RefreshScheduleBoardWindow();
        RebuildScheduleCustomSchedulingControls();
        UpdateScheduleTimingSplitVisibility();
    }

    private ScheduleConstraintProfile GetScheduleConstraintProfile()
    {
        if (ScheduleConstraintProfileBox.SelectedItem is ComboBoxItem item
            && Enum.TryParse<ScheduleConstraintProfile>(item.Tag?.ToString(), out var profile))
        {
            return profile;
        }

        return ScheduleConstraintProfile.Campus;
    }

    private ScheduleAutoSchedulingStrategy GetScheduleAutoSchedulingStrategy()
    {
        if (ScheduleAutoStrategyBox.SelectedItem is ComboBoxItem item
            && Enum.TryParse<ScheduleAutoSchedulingStrategy>(item.Tag?.ToString(), out var strategy))
        {
            return strategy;
        }

        return ScheduleAutoSchedulingStrategy.BalancedRelaxed;
    }

    private static int ParsePositiveInt(string? value, string fieldName)
    {
        if (!int.TryParse(value?.Trim(), out var result) || result <= 0)
        {
            throw new DrawValidationException($"{fieldName}必须是大于 0 的整数。");
        }

        return result;
    }

    private static int? ParseOptionalPositiveInt(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ParsePositiveInt(value, fieldName);
    }

    private static bool IsHandledWorkflowException(Exception ex)
    {
        return ex is DrawValidationException or IOException or InvalidOperationException
            || ex is ExcelImportException or TournamentProgressException;
    }
}
