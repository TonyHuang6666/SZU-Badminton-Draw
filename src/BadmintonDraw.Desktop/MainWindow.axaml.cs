using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using BadmintonDraw.Workflows;

namespace BadmintonDraw.Desktop;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType ExcelFileType = new("Excel 文件")
    {
        Patterns = ["*.xlsx"],
        MimeTypes = ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"]
    };

    private static readonly FilePickerFileType JpegFileType = new("JPG 图片")
    {
        Patterns = ["*.jpg", "*.jpeg"],
        MimeTypes = ["image/jpeg"]
    };

    private static readonly FilePickerFileType PngFileType = new("PNG 图片")
    {
        Patterns = ["*.png"],
        MimeTypes = ["image/png"]
    };

    private static readonly FilePickerFileType PdfFileType = new("PDF 文件")
    {
        Patterns = ["*.pdf"],
        MimeTypes = ["application/pdf"]
    };

    private readonly DrawWorkflow _drawWorkflow = new();
    private readonly ScheduleWorkflow _scheduleWorkflow = new();
    private static readonly IBrush ReadyStatusBrush = new SolidColorBrush(Color.FromRgb(25, 169, 116));
    private static readonly IBrush WarningStatusBrush = new SolidColorBrush(Color.FromRgb(217, 119, 6));
    private static readonly IBrush ErrorStatusBrush = new SolidColorBrush(Color.FromRgb(185, 28, 28));
    private static readonly IBrush ReadyStatusBackground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
    private static readonly IBrush WarningStatusBackground = new SolidColorBrush(Color.FromRgb(255, 250, 235));
    private static readonly IBrush ErrorStatusBackground = new SolidColorBrush(Color.FromRgb(254, 242, 242));
    private static readonly IBrush ScheduledRowBackground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
    private static readonly IBrush ScheduledRowBorder = new SolidColorBrush(Color.FromRgb(226, 232, 240));
    private static readonly IBrush ScheduledBadgeBackground = new SolidColorBrush(Color.FromRgb(236, 246, 255));
    private static readonly IBrush ScheduledBadgeForeground = new SolidColorBrush(Color.FromRgb(15, 95, 159));
    private static readonly IBrush UnscheduledRowBackground = new SolidColorBrush(Color.FromRgb(255, 247, 247));
    private static readonly IBrush UnscheduledRowBorder = new SolidColorBrush(Color.FromRgb(246, 190, 190));
    private static readonly IBrush UnscheduledBadgeBackground = new SolidColorBrush(Color.FromRgb(255, 228, 230));
    private static readonly IBrush UnscheduledBadgeForeground = new SolidColorBrush(Color.FromRgb(159, 18, 57));

    private IReadOnlyList<DrawParticipant> _participants = [];
    private IReadOnlyList<string> _importWarnings = [];
    private readonly ObservableCollection<ScheduleDayWorkflowRequest> _scheduleDays = [];
    private DrawWorkflowResult? _latestWorkflowResult;
    private DrawResult? _latestResult;
    private SchedulePlan? _latestSchedule;
    private string? _loadedInputPath;

    public MainWindow()
    {
        InitializeComponent();
        SeedBox.Text = DrawWorkflow.GenerateSeed();
        ScheduleDateBox.Text = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
        ScheduleDaysList.ItemsSource = _scheduleDays;
        ApplyScheduleCourtPreset();
        AddCurrentScheduleDay(showStatus: false);
    }

    private async void BrowseInput_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择参赛名单",
            AllowMultiple = false,
            FileTypeFilter = [ExcelFileType]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        InputPathBox.Text = path;
        TryLoadParticipants();
    }

    private async void CreateTemplate_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickSavePath("保存名单模板", "深大羽协参赛名单模板.xlsx");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _drawWorkflow.WriteTemplate(path);
            SetStatus($"已生成名单模板：{path}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void GenerateSeed_Click(object? sender, RoutedEventArgs e)
    {
        SeedBox.Text = DrawWorkflow.GenerateSeed();
    }

    private void Preview_Click(object? sender, RoutedEventArgs e)
    {
        TryGenerate();
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGenerate() || _latestResult is null || _latestWorkflowResult is null)
        {
            return;
        }

        var exportFormat = GetExportFormat(DrawExportFormatBox);
        var suggestedName = DrawWorkflow.BuildDefaultDrawFileName(_latestResult, _loadedInputPath ?? InputPathBox.Text, exportFormat);
        var path = await PickSavePath("保存抽签结果", suggestedName, exportFormat);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var outputPaths = _drawWorkflow.ExportFiles(
                path,
                exportFormat,
                _latestWorkflowResult,
                GetDrawVisualOptions());
            SetStatus($"抽签结果已导出：{FormatOutputPaths(outputPaths)}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private bool TryLoadParticipants()
    {
        try
        {
            var inputPath = InputPathBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                throw new DrawValidationException("请先选择参赛名单 Excel。");
            }

            var importResult = _drawWorkflow.LoadParticipants(inputPath, GetEventKind());
            ApplyDetectedEventKind(importResult.DetectedEventKind);
            _participants = importResult.Participants;
            _importWarnings = importResult.WarningMessages;
            _loadedInputPath = inputPath;
            _latestWorkflowResult = null;
            _latestResult = null;
            _latestSchedule = null;
            ParticipantCountText.Text = _participants.Count.ToString();
            EventKindStatText.Text = WorkflowLabels.GetEventKindDisplay(importResult.DetectedEventKind);
            PreviewStateText.Text = "待预览";
            SummaryText.Text = $"已导入 {_participants.Count} 个参赛单位";
            SetWarnings(_importWarnings);
            ClearSchedulePreview();
            SetStatus(_importWarnings.Count > 0
                ? $"名单已导入，但有 {_importWarnings.Count} 条提醒。确认无误后可以预览抽签。"
                : "名单已导入，可以预览抽签。",
                _importWarnings.Count > 0);
            return true;
        }
        catch (Exception ex) when (IsHandledWorkflowException(ex))
        {
            ResetPreview();
            SetStatus(ex.Message, isError: true);
            return false;
        }
    }

    private bool TryGenerate()
    {
        try
        {
            var inputPath = InputPathBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                throw new DrawValidationException("请先选择参赛名单 Excel。");
            }

            if (_participants.Count == 0 || !string.Equals(_loadedInputPath, inputPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryLoadParticipants())
                {
                    return false;
                }
            }

            if (!int.TryParse(GroupCountBox.Text?.Trim(), out var groupCount))
            {
                throw new DrawValidationException("小组数必须是数字。");
            }

            var request = new DrawWorkflowRequest(
                inputPath,
                GetCompetitionMode(),
                GetEventKind(),
                groupCount,
                SeedBox.Text ?? "",
                GetKnockoutGoal(),
                GetPlacementPlayoff());
            _latestWorkflowResult = _drawWorkflow.Generate(request);
            _participants = _latestWorkflowResult.Participants;
            _importWarnings = _latestWorkflowResult.WarningMessages;
            _latestResult = _latestWorkflowResult.Result;
            _latestSchedule = null;

            ParticipantCountText.Text = _latestResult.Audit.ParticipantCount.ToString();
            GroupCountStatText.Text = _latestResult.Audit.GroupCount.ToString();
            EventKindStatText.Text = WorkflowLabels.GetEventKindDisplay(request.EventKind);
            PreviewStateText.Text = "已预览";
            SummaryText.Text = $"已生成 {_latestResult.Groups.Count} 个小组，随机种子 {_latestResult.Audit.RandomSeed}";
            SetWarnings(_importWarnings);
            GroupsList.ItemsSource = FormatGroups(_latestResult.Groups);
            RoundOneList.ItemsSource = FormatGroups(_latestResult.RoundOneGroups);
            ByeList.ItemsSource = FormatGroups(_latestResult.ByeGroups);
            ClearSchedulePreview();
            SetStatus(_importWarnings.Count > 0
                ? "抽签预览已生成；名单提醒请人工复核。"
                : "抽签预览已生成，可导出 Excel。",
                _importWarnings.Count > 0);
            return true;
        }
        catch (Exception ex) when (IsHandledWorkflowException(ex))
        {
            ResetPreview();
            SetStatus(ex.Message, isError: true);
            return false;
        }
        catch (Exception ex) when (ex is DrawValidationException or InvalidOperationException)
        {
            SetStatus(ex.Message, isError: true);
            return false;
        }
    }

    private void GenerateSchedule_Click(object? sender, RoutedEventArgs e)
    {
        TryGenerateSchedule();
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
            ClearSchedulePreview();
            SetStatus("已删除选中的赛程日。");
        }
    }

    private void ScheduleVenueBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized)
        {
            ApplyScheduleCourtPreset();
        }
    }

    private async void ExportSchedule_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGenerateSchedule() || _latestSchedule is null || _latestResult is null)
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
            if (ExportTimedBracketBox.IsChecked == true && _latestWorkflowResult is not null)
            {
                var timedBracketPaths = _scheduleWorkflow.ExportTimedBracketFiles(
                    path,
                    exportFormat,
                    _latestWorkflowResult,
                    _latestSchedule);
                SetStatus($"赛程表已导出：{FormatOutputPaths(schedulePaths)}；带比赛时间和场地的对阵表已导出：{FormatOutputPaths(timedBracketPaths)}");
            }
            else
            {
                SetStatus($"赛程表已导出：{FormatOutputPaths(schedulePaths)}");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private async void ExportMatchRecord_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGenerateSchedule() || _latestSchedule is null)
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

        var dayLabel = GetSelectedMatchRecordDayLabel(_latestSchedule);
        if (string.IsNullOrWhiteSpace(dayLabel))
        {
            SetStatus("当前赛程没有可导出的比赛日。", isError: true);
            return;
        }

        var path = await PickSavePath("保存赛程记录表", ScheduleWorkflow.BuildDefaultMatchRecordFileName(dayLabel), WorkflowExportFormat.Excel);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _scheduleWorkflow.ExportMatchRecord(path, _latestSchedule, dayLabel);
            SetStatus($"赛程记录表已导出：{path}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private async void ImportMatchRecordAndExportNext_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGenerateSchedule() || _latestSchedule is null)
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

            var exportPath = await PickSavePath(
                "保存下一比赛日赛程记录表",
                ScheduleWorkflow.BuildDefaultMatchRecordFileName(nextDayLabel),
                WorkflowExportFormat.Excel);
            if (string.IsNullOrWhiteSpace(exportPath))
            {
                return;
            }

            _scheduleWorkflow.ExportMatchRecord(
                exportPath,
                _latestSchedule,
                nextDayLabel,
                importResult.Results,
                importResult.PendingMatchNames.ToHashSet(StringComparer.Ordinal));
            var pendingText = importResult.PendingMatchNames.Count > 0
                ? $"，顺延 {importResult.PendingMatchNames.Count} 场未决比赛"
                : "";
            SetStatus($"已从 {paths.Length} 张记录表累计读取 {importResult.Results.Count} 场结果{pendingText}，并导出下一比赛日记录表：{exportPath}");
        }
        catch (Exception ex) when (IsHandledWorkflowException(ex))
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private bool TryGenerateSchedule()
    {
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

            var settings = BuildScheduleSettings();
            _latestSchedule = _scheduleWorkflow.Generate(_latestResult, settings);
            ScheduleSummaryText.Text = _latestSchedule.IsComplete
                ? $"已生成 {_latestSchedule.Matches.Count} 场，预计 {_latestSchedule.DayCount} 个比赛日。{ScheduleWorkflow.BuildScheduleCapacityText(settings)}"
                : $"已安排 {_latestSchedule.Matches.Count} 场，未安排 {_latestSchedule.UnscheduledMatches.Count} 场，共 {_latestSchedule.TotalMatchCount} 场。{ScheduleWorkflow.BuildScheduleCapacityText(settings)}";
            ScheduleList.ItemsSource = FormatScheduleRows(_latestSchedule);
            SetStatus(_latestSchedule.IsComplete
                ? "赛程预览已生成，可导出赛程 Excel。"
                : "赛程资源不足：预览已保留，未安排场次会在列表底部显示。",
                isWarning: !_latestSchedule.IsComplete);
            return true;
        }
        catch (Exception ex) when (ex is DrawValidationException or InvalidOperationException or IOException)
        {
            SetStatus(ex.Message, isError: true);
            return false;
        }
    }

    private ScheduleSettings BuildScheduleSettings()
    {
        var boundary = GetSelectedComboBoxTagInt(ScheduleTimingBoundaryBox);
        return ScheduleWorkflow.BuildSettings(
            _scheduleDays.ToList(),
            ParsePositiveInt(ScheduleMatchMinutesBox.Text, boundary > 0 ? "分界线后每场分钟" : "每场分钟"),
            ParsePositiveInt(GetSelectedComboBoxText(ScheduleMaxMatchesBox), boundary > 0 ? "分界线后每日最多场" : "每日最多场"),
            boundary > 0 ? boundary : null,
            boundary > 0 ? ParsePositiveInt(BeforeBoundaryMatchMinutesBox.Text, "分界线前每场分钟") : null,
            boundary > 0 ? ParsePositiveInt(GetSelectedComboBoxText(BeforeBoundaryMaxMatchesBox), "分界线前每日最多场") : null);
    }

    private void AddCurrentScheduleDay(bool showStatus = true)
    {
        if (!DateOnly.TryParse(ScheduleDateBox.Text?.Trim(), out var date))
        {
            throw new DrawValidationException("比赛日期格式应为 yyyy-MM-dd。");
        }

        if (!TimeOnly.TryParse(ScheduleStartBox.Text?.Trim(), out var start))
        {
            throw new DrawValidationException("开始时间格式应为 HH:mm。");
        }

        if (!TimeOnly.TryParse(ScheduleEndBox.Text?.Trim(), out var end))
        {
            throw new DrawValidationException("结束时间格式应为 HH:mm。");
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
        if (existing is not null)
        {
            _scheduleDays.Remove(existing);
        }

        _scheduleDays.Add(new ScheduleDayWorkflowRequest(
            date,
            start,
            end,
            venue,
            courtsText));
        ClearSchedulePreview();
        if (showStatus)
        {
            SetStatus("已添加赛程日。");
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
        return _latestResult is not null
            && _latestResult.Settings.IsKnockout
            && GetExportFormat(DrawExportFormatBox) is WorkflowExportFormat.A4Pdf or WorkflowExportFormat.All
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

    private string? GetSelectedMatchRecordDayLabel(SchedulePlan plan)
    {
        if (ScheduleDaysList.SelectedItem is ScheduleDayWorkflowRequest selectedDay
            && plan.Matches.Any(match => match.DayLabel == selectedDay.DateText))
        {
            return selectedDay.DateText;
        }

        return ScheduleWorkflow.GetFirstRecordDayLabel(plan);
    }

    private async System.Threading.Tasks.Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = 330,
            MinWidth = 480,
            MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
            LineHeight = 22
        };
        var yesButton = new Button
        {
            Content = "继续导出",
            MinWidth = 110,
            Background = new SolidColorBrush(Color.FromRgb(15, 95, 159)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(15, 95, 159))
        };
        var noButton = new Button
        {
            Content = "取消",
            MinWidth = 90
        };
        yesButton.Click += (_, _) => dialog.Close(true);
        noButton.Click += (_, _) => dialog.Close(false);

        dialog.Content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            },
            Margin = new Avalonia.Thickness(22),
            Children =
            {
                new ScrollViewer { Content = text },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Margin = new Avalonia.Thickness(0, 18, 0, 0),
                    Children = { noButton, yesButton },
                    [Grid.RowProperty] = 1
                }
            }
        };

        return await dialog.ShowDialog<bool>(this);
    }

    private void ResetPreview()
    {
        _participants = [];
        _importWarnings = [];
        _latestWorkflowResult = null;
        _latestResult = null;
        _latestSchedule = null;
        _loadedInputPath = null;
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
    }

    private CompetitionMode GetCompetitionMode()
    {
        return CompetitionModeBox.SelectedIndex switch
        {
            1 => CompetitionMode.SinglesRoundRobin,
            2 => CompetitionMode.TeamKnockout,
            3 => CompetitionMode.TeamRoundRobin,
            _ => CompetitionMode.SinglesKnockout
        };
    }

    private EventKind GetEventKind()
    {
        return EventKindBox.SelectedIndex switch
        {
            0 => EventKind.Singles,
            2 => EventKind.Team,
            _ => EventKind.Doubles
        };
    }

    private KnockoutGoal GetKnockoutGoal()
    {
        return KnockoutGoalBox.SelectedIndex == 1
            ? KnockoutGoal.Champion
            : KnockoutGoal.OneQualifierPerGroup;
    }

    private PlacementPlayoff GetPlacementPlayoff()
    {
        return PlacementPlayoffBox.SelectedIndex switch
        {
            1 => PlacementPlayoff.ThirdPlace,
            2 => PlacementPlayoff.ThirdToEighth,
            _ => PlacementPlayoff.None
        };
    }

    private void ApplyDetectedEventKind(EventKind eventKind)
    {
        EventKindBox.SelectedIndex = eventKind switch
        {
            EventKind.Singles => 0,
            EventKind.Team => 2,
            _ => 1
        };

        if (eventKind == EventKind.Team && CompetitionModeBox.SelectedIndex < 2)
        {
            CompetitionModeBox.SelectedIndex = 2;
        }
        else if (eventKind != EventKind.Team && CompetitionModeBox.SelectedIndex >= 2)
        {
            CompetitionModeBox.SelectedIndex = 0;
        }
    }

    private static IReadOnlyList<PreviewGroupRow> FormatGroups(IReadOnlyList<DrawGroup> groups)
    {
        if (groups.Count == 0)
        {
            return [new PreviewGroupRow("无", "", "暂无内容")];
        }

        return groups
            .Select(group => new PreviewGroupRow(
                WorkflowLabels.BuildGroupName(group.Number),
                $"{group.Count} 人",
                string.Join("、", group.Participants.Select(FormatParticipant))))
            .ToList();
    }

    private static string FormatParticipant(DrawParticipant participant)
    {
        return participant.IsSeed && participant.SeedRank.HasValue
            ? $"{participant.DisplayName}（{participant.SeedRank}号种子）"
            : participant.IsSeed
                ? $"{participant.DisplayName}（种子）"
                : participant.DisplayName;
    }

    private static IReadOnlyList<SchedulePreviewRow> FormatScheduleRows(SchedulePlan schedule)
    {
        var rows = schedule.Matches
            .Take(80)
            .Select(match => new SchedulePreviewRow(
                "已安排",
                $"{match.DayLabel} {match.TimeRange}",
                match.Court,
                $"{match.GroupName} {match.Phase}",
                $"{match.SideA} vs {match.SideB}",
                string.IsNullOrWhiteSpace(match.Note) ? match.MatchName : $"{match.MatchName} · {match.Note}",
                ScheduledRowBackground,
                ScheduledRowBorder,
                ScheduledBadgeBackground,
                ScheduledBadgeForeground))
            .ToList();
        rows.AddRange(schedule.UnscheduledMatches.Take(40).Select(match =>
            new SchedulePreviewRow(
                "未安排",
                "待排期",
                "未定",
                $"{match.GroupName} {match.Phase}",
                $"{match.SideA} vs {match.SideB}",
                $"{match.MatchName} · {match.Reason}",
                UnscheduledRowBackground,
                UnscheduledRowBorder,
                UnscheduledBadgeBackground,
                UnscheduledBadgeForeground)));
        return rows.Count == 0
            ? [new SchedulePreviewRow("空", "暂无", "-", "暂无赛程", "生成赛程后显示", "", ScheduledRowBackground, ScheduledRowBorder, ScheduledBadgeBackground, ScheduledBadgeForeground)]
            : rows;
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
        ScheduleSummaryText.Text = "尚未生成赛程";
        ScheduleList.ItemsSource = Array.Empty<SchedulePreviewRow>();
    }

    private static int ParsePositiveInt(string? value, string fieldName)
    {
        if (!int.TryParse(value?.Trim(), out var result) || result <= 0)
        {
            throw new DrawValidationException($"{fieldName}必须是大于 0 的整数。");
        }

        return result;
    }

    private static bool IsHandledWorkflowException(Exception ex)
    {
        return ex is DrawValidationException or IOException or InvalidOperationException
            || ex.GetType().Name == "ExcelImportException";
    }

    private void SetStatus(string message, bool isWarning = false, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? ErrorStatusBrush
            : isWarning
                ? WarningStatusBrush
                : new SolidColorBrush(Color.FromRgb(65, 80, 106));
        StatusDot.Background = isError
            ? ErrorStatusBrush
            : isWarning
                ? WarningStatusBrush
                : ReadyStatusBrush;
        StatusBar.Background = isError
            ? ErrorStatusBackground
            : isWarning
                ? WarningStatusBackground
                : ReadyStatusBackground;
    }

    public sealed record PreviewGroupRow(
        string Title,
        string CountText,
        string Body);

    public sealed record SchedulePreviewRow(
        string Status,
        string TimeText,
        string CourtText,
        string Title,
        string Pairing,
        string Note,
        IBrush BackgroundBrush,
        IBrush BorderBrush,
        IBrush BadgeBrush,
        IBrush BadgeForeground);
}
