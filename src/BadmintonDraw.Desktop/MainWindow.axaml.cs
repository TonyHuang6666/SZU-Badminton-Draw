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

    private static readonly FilePickerFileType ProgressFileType = new("深大羽协赛事存档")
    {
        Patterns = ["*.szbd"],
        MimeTypes = ["application/x-szuba-badminton-draw"]
    };

    private readonly DrawWorkflow _drawWorkflow = new();
    private readonly ScheduleWorkflow _scheduleWorkflow = new();
    private readonly ScheduleConstraintAnalyzer _scheduleConstraintAnalyzer = new();
    private readonly TournamentProgressWorkflow _progressWorkflow = new();
    private readonly CrossEventConflictWorkflow _crossEventConflictWorkflow = new();
    private const int ScheduleBoardDayDropdownThreshold = 5;
    private const double ParticipantRosterMinZoom = 0.5;
    private const double ParticipantRosterMaxZoom = 1.8;
    private const double ParticipantRosterZoomStep = 0.1;
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
    private static readonly IBrush DropAllowedBackground = new SolidColorBrush(Color.FromRgb(236, 253, 245));
    private static readonly IBrush DropAllowedBorder = new SolidColorBrush(Color.FromRgb(34, 197, 94));
    private static readonly IBrush DropWarningBackground = new SolidColorBrush(Color.FromRgb(255, 251, 235));
    private static readonly IBrush DropWarningBorder = new SolidColorBrush(Color.FromRgb(245, 158, 11));
    private static readonly IBrush DropBlockedBackground = new SolidColorBrush(Color.FromRgb(254, 242, 242));
    private static readonly IBrush DropBlockedBorder = new SolidColorBrush(Color.FromRgb(220, 38, 38));

    private IBrush ThemeBrush(string resourceKey, Color fallbackColor)
    {
        if (TryGetResource(resourceKey, ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }

        if (Application.Current?.TryGetResource(resourceKey, ActualThemeVariant, out value) == true
            && value is IBrush appBrush)
        {
            return appBrush;
        }

        return new SolidColorBrush(fallbackColor);
    }

    private IReadOnlyList<DrawParticipant> _participants = [];
    private IReadOnlyList<string> _importWarnings = [];
    private IReadOnlyList<ParticipantImportWarning> _participantImportWarnings = [];
    private readonly ObservableCollection<ScheduleDayWorkflowRequest> _scheduleDays = [];
    private DrawWorkflowResult? _latestWorkflowResult;
    private DrawResult? _latestResult;
    private SchedulePlan? _latestSchedule;
    private ScheduleConstraintReport? _latestScheduleConstraintReport;
    private string? _loadedInputPath;
    private string? _progressFilePath;
    private TournamentProgressState? _progressState;
    private double _scheduleBoardWindowZoom = 1.0;
    private Window? _scheduleBoardWindow;
    private ComboBox? _scheduleBoardWindowDayBox;
    private StackPanel? _scheduleBoardWindowDayPickerPanel;
    private TextBlock? _scheduleBoardWindowSummaryText;
    private Grid? _scheduleBoardWindowGrid;
    private Button? _scheduleBoardWindowUndoButton;
    private StackPanel? _scheduleBoardWindowDayTabs;
    private readonly Stack<SingleScheduleUndoSnapshot> _singleScheduleUndoStack = new();
    private readonly Dictionary<string, Border> _scheduleBoardWindowMatchCards = new(StringComparer.Ordinal);
    private int _scheduleBoardHighlightVersion;
    private Window? _scheduleConstraintWindow;
    private CrossEventScheduleBoard? _crossEventScheduleBoard;
    private CrossEventScheduleBoard? _crossEventBaseScheduleBoard;
    private CrossEventScheduleBoard? _crossEventLastAcceptedBoard;
    private CrossEventSchedulingOptions? _crossEventSchedulingOptions;
    private CrossEventSchedulingOptions? _crossEventRecommendedCustomOptions;
    private CrossEventSchedulingOptions? _crossEventLastAcceptedOptions;
    private bool _updatingCrossEventCustomControls;
    private bool _runningCrossEventScheduling;
    private bool _showingCrossEventSchedulingFailureDialog;
    private int _crossEventSchedulingVersion;
    private CrossEventCustomAnchor _pendingCrossEventCustomAnchor = CrossEventCustomAnchor.None;
    private readonly Dictionary<string, Slider> _crossEventDayLoadSliders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _crossEventDayLoadLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Min, double Max, double Recommended)> _crossEventDayLoadRecommendedRanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Slider> _crossEventStageWaveSliders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _crossEventStageWaveLabels = new(StringComparer.Ordinal);
    private DispatcherTimer? _crossEventCustomRecalculateTimer;
    private double _crossEventBoardZoom = 1.0;
    private double _crossEventBoardWindowZoom = 1.0;
    private Window? _crossEventBoardWindow;
    private ComboBox? _crossEventBoardWindowDayBox;
    private StackPanel? _crossEventBoardWindowDayPickerPanel;
    private TextBlock? _crossEventBoardWindowSummaryText;
    private Grid? _crossEventBoardWindowGrid;
    private Button? _crossEventBoardWindowUndoButton;
    private StackPanel? _crossEventBoardWindowDayTabs;
    private readonly Stack<CrossEventScheduleUndoSnapshot> _crossEventScheduleUndoStack = new();
    private readonly Dictionary<string, Border> _crossEventBoardWindowMatchCards = new(StringComparer.Ordinal);
    private Window? _crossEventConflictWindow;
    private readonly Dictionary<string, ScheduleBoardMoveValidationResult> _scheduleBoardMoveValidationCache = new(StringComparer.Ordinal);
    private Border? _scheduleBoardDragHoverCell;
    private string? _lastScheduleBoardDragFeedbackMessage;
    private string? _scheduleBoardDragSwitchDayLabel;
    private bool _uiReady;

    private enum CrossEventCustomAnchorKind
    {
        None,
        Recommended,
        DayLoad,
        StageWave,
        StageWaveEnabled,
        MinimumRest,
        RefereeCount
    }

    private enum ScheduleBoardCascadeMoveAction
    {
        Cancel,
        MoveCurrentOnly,
        CascadeMove
    }

    private enum CrossEventIssueCategory
    {
        All,
        ScheduleConflict,
        MultiEventInterval,
        SameDayLoad,
        LoadForecast
    }

    private sealed record CrossEventCustomAnchor(
        CrossEventCustomAnchorKind Kind,
        string? DayLabel = null)
    {
        public static CrossEventCustomAnchor None { get; } = new(CrossEventCustomAnchorKind.None);

        public string Describe()
        {
            return Kind switch
            {
                CrossEventCustomAnchorKind.DayLoad when !string.IsNullOrWhiteSpace(DayLabel) => $"{DayLabel} 目标负载率",
                CrossEventCustomAnchorKind.StageWave when !string.IsNullOrWhiteSpace(DayLabel) => $"{DayLabel} 阶段推进",
                CrossEventCustomAnchorKind.StageWaveEnabled => "阶段波次推进",
                CrossEventCustomAnchorKind.MinimumRest => "最小休息间隔",
                CrossEventCustomAnchorKind.RefereeCount => "裁判人数",
                CrossEventCustomAnchorKind.Recommended => "推荐分布",
                _ => "当前参数"
            };
        }
    }

    private sealed record ScheduleBoardDayTabTarget(
        ScheduleBoardKind Kind,
        string DayLabel);

    private sealed record CrossEventCustomSliderTag(
        CrossEventCustomAnchorKind Kind,
        string DayLabel);

    private sealed record SingleScheduleUndoSnapshot(
        SchedulePlan Schedule,
        TournamentProgressState? ProgressState,
        string? ProgressFilePath,
        string? DayLabel);

    private sealed record CrossEventScheduleUndoSnapshot(
        CrossEventScheduleBoard Board,
        string? DayLabel);

    public MainWindow()
    {
        InitializeComponent();
        ApplyWindowIcon();
        SeedBox.Text = DrawWorkflow.GenerateSeed();
        ScheduleDatePicker.SelectedDate = new DateTimeOffset(DateTime.Today);
        ScheduleDaysList.ItemsSource = _scheduleDays;
        CrossEventCustomSchedulingPanel.IsVisible = false;
        UpdateScheduleUndoButtons();
        UpdateCrossEventUndoButtons();
        CrossEventStageWaveBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty)
            {
                QueueCrossEventCustomRecalculate(new CrossEventCustomAnchor(CrossEventCustomAnchorKind.StageWaveEnabled));
            }
        };
        _crossEventCustomRecalculateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _crossEventCustomRecalculateTimer.Tick += CrossEventCustomRecalculateTimer_Tick;
        Opened += MainWindow_Opened;
    }

    private void ApplyWindowIcon()
    {
        try
        {
            using var iconStream = Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://BadmintonDraw.Desktop/Assets/szuba-app-icon.ico"));
            Icon = new WindowIcon(iconStream);
        }
        catch
        {
            // The packaged application icon remains available even if a host cannot load a window icon.
        }
    }

    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        if (_uiReady)
        {
            return;
        }

        _uiReady = true;
        UpdateEventKindForMode();
        UpdateKnockoutGoalVisibility();
        UpdateDrawPdfOptionsVisibility();
        UpdateScheduleTimingSplitVisibility();
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

    private void BrowseParticipants_Click(object? sender, RoutedEventArgs e)
    {
        if (_participants.Count == 0)
        {
            SetStatus("请先选择并导入参赛名单。", isWarning: true);
            return;
        }

        ShowParticipantRosterWindow();
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
        if ((_latestWorkflowResult is null && !TryGenerate())
            || _latestResult is null
            || _latestWorkflowResult is null)
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
            _participantImportWarnings = importResult.ImportWarnings;
            _loadedInputPath = inputPath;
            _latestWorkflowResult = null;
            _latestResult = null;
            _latestSchedule = null;
            ClearProgressReference();
            ParticipantCountText.Text = _participants.Count.ToString();
            EventKindStatText.Text = WorkflowLabels.GetEventKindDisplay(importResult.DetectedEventKind);
            PreviewStateText.Text = "待预览";
            SummaryText.Text = $"已导入 {_participants.Count} 个参赛单位";
            SetWarnings(_importWarnings);
            _ = ShowImportWarningsIfNeededAsync(importResult.ImportWarnings);
            ClearSchedulePreview();
            UpdateDrawPdfOptionsVisibility();
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

            if (_participants.Count == 0
                || (!string.IsNullOrWhiteSpace(_loadedInputPath)
                    && !string.Equals(_loadedInputPath, inputPath, StringComparison.OrdinalIgnoreCase)))
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
            _latestWorkflowResult = _drawWorkflow.GenerateFromParticipants(
                request,
                _participants,
                _participantImportWarnings);
            ClearProgressReference();
            _participants = _latestWorkflowResult.Participants;
            _importWarnings = _latestWorkflowResult.WarningMessages;
            _participantImportWarnings = _latestWorkflowResult.ImportWarnings;
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
            UpdateKnockoutGoalVisibility();
            UpdateDrawPdfOptionsVisibility();
            UpdateScheduleTimingSplitVisibility();
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

    private void ScheduleAutoStrategyBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _latestResult is null)
        {
            return;
        }

        TryGenerateSchedule();
    }

    private void ScheduleTimingBoundaryBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateScheduleTimingSplitVisibility();
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
            SetStatus(
                $"多项目合并材料包已导出到：{result.OutputDirectory}（共 {result.OutputPaths.Count} 个文件）。",
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

        var boardHost = new Border
        {
            Background = ThemeBrush("AppSurfaceAltBrush", Color.FromRgb(251, 252, 255)),
            BorderBrush = ThemeBrush("AppPanelBorderBrush", Color.FromRgb(216, 224, 236)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10),
            Margin = new Avalonia.Thickness(0, 10, 0, 0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = _scheduleBoardWindowGrid
            }
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
        return $"已安排 {schedule.Matches.Count} 场{unscheduledText}，比赛日 {schedule.DayCount} 个；缩放 {Math.Round(zoom * 100)}%。";
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
        ApplyScheduleBoardDragFeedback(cell, result);
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
        ScheduleBoardMoveValidationResult result)
    {
        if (!ReferenceEquals(_scheduleBoardDragHoverCell, cell))
        {
            ClearScheduleBoardDragFeedback();
            _scheduleBoardDragHoverCell = cell;
        }

        var (background, border) = result.Severity switch
        {
            ScheduleBoardMoveValidationSeverity.Blocked => (DropBlockedBackground, DropBlockedBorder),
            ScheduleBoardMoveValidationSeverity.Warning => (DropWarningBackground, DropWarningBorder),
            _ => (DropAllowedBackground, DropAllowedBorder)
        };
        cell.Background = background;
        cell.BorderBrush = border;
        cell.BorderThickness = new Avalonia.Thickness(2);
        ToolTip.SetTip(cell, result.Message);
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
            _scheduleBoardDragHoverCell.Background = Brushes.White;
            _scheduleBoardDragHoverCell.BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240));
            _scheduleBoardDragHoverCell.BorderThickness = new Avalonia.Thickness(0, 0, 1, 1);
            ToolTip.SetTip(_scheduleBoardDragHoverCell, null);
        }

        _scheduleBoardDragHoverCell = null;
        _lastScheduleBoardDragFeedbackMessage = null;
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
        ScheduleSummaryText.Text = $"已调整 {_latestSchedule.Matches.Count} 场赛程，预计 {_latestSchedule.DayCount} 个比赛日。";
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
        ScheduleSummaryText.Text = $"已连锁调整 {_latestSchedule.Matches.Count} 场赛程，预计 {_latestSchedule.DayCount} 个比赛日。";
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
        ScheduleSummaryText.Text = $"已撤销上一步调整；当前 {_latestSchedule.Matches.Count} 场赛程，预计 {_latestSchedule.DayCount} 个比赛日。";
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

        var boardHost = new Border
        {
            Background = ThemeBrush("AppSurfaceAltBrush", Color.FromRgb(251, 252, 255)),
            BorderBrush = ThemeBrush("AppPanelBorderBrush", Color.FromRgb(216, 224, 236)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10),
            Margin = new Avalonia.Thickness(0, 10, 0, 0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = _crossEventBoardWindowGrid
            }
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
            ClearSingleScheduleUndoStack();
            UpdateScheduleConstraintReport(_latestSchedule);
            ClearProgressReference();
            ScheduleSummaryText.Text = _latestSchedule.IsComplete
                ? $"已生成 {_latestSchedule.Matches.Count} 场，预计 {_latestSchedule.DayCount} 个比赛日。{ScheduleWorkflow.BuildScheduleCapacityText(settings)}"
                : $"已安排 {_latestSchedule.Matches.Count} 场，未安排 {_latestSchedule.UnscheduledMatches.Count} 场，共 {_latestSchedule.TotalMatchCount} 场。{ScheduleWorkflow.BuildScheduleCapacityText(settings)}";
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
            SetStatus(ex.Message, isError: true);
            return false;
        }
    }

    private ScheduleSettings BuildScheduleSettings()
    {
        var boundary = ShouldShowScheduleTimingSplit()
            ? GetSelectedComboBoxTagInt(ScheduleTimingBoundaryBox)
            : 0;
        return ScheduleWorkflow.BuildSettings(
            _scheduleDays.ToList(),
            ParsePositiveInt(ScheduleMatchMinutesBox.Text, boundary > 0 ? "分界线后每场分钟" : "每场分钟"),
            ParsePositiveInt(GetSelectedComboBoxText(ScheduleMaxMatchesBox), boundary > 0 ? "分界线后每日最多场" : "每日最多场"),
            boundary > 0 ? boundary : null,
            boundary > 0 ? ParsePositiveInt(BeforeBoundaryMatchMinutesBox.Text, "分界线前每场分钟") : null,
            boundary > 0 ? ParsePositiveInt(GetSelectedComboBoxText(BeforeBoundaryMaxMatchesBox), "分界线前每日最多场") : null,
            GetScheduleConstraintProfile(),
            GetScheduleAutoSchedulingStrategy(),
            ParseOptionalPositiveInt(ScheduleRefereeCountBox.Text, "裁判人数"));
    }

    private void AddCurrentScheduleDay(bool showStatus = true)
    {
        if (ScheduleDatePicker.SelectedDate is not DateTimeOffset selectedDate)
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
            day => Math.Max(1, (int)Math.Round((day.EndTime - day.StartTime).TotalMinutes) * GetEffectiveCrossEventConcurrentMatchLimit(day)),
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

    private int GetEffectiveCrossEventConcurrentMatchLimit(CrossEventScheduleBoardDay day)
    {
        var courtCount = Math.Max(1, day.Courts.Count);
        var refereeCount = GetCrossEventRefereeCount();
        return refereeCount is > 0
            ? Math.Max(1, Math.Min(courtCount, refereeCount.Value))
            : courtCount;
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

    private void UpdateEventKindForMode()
    {
        if (GetCompetitionMode() is CompetitionMode.TeamKnockout or CompetitionMode.TeamRoundRobin)
        {
            EventKindBox.SelectedIndex = 2;
        }
        else if (EventKindBox.SelectedIndex == 2)
        {
            EventKindBox.SelectedIndex = 0;
        }
    }

    private void UpdateKnockoutGoalVisibility()
    {
        var isKnockout = GetCompetitionMode() is CompetitionMode.SinglesKnockout or CompetitionMode.TeamKnockout;
        var hasGroupCount = TryGetGroupCount(out var groupCount);
        var showGoalOptions = isKnockout && hasGroupCount && IsPowerOfTwo(groupCount);
        KnockoutGoalPanel.IsVisible = showGoalOptions;
        if (!showGoalOptions)
        {
            SelectKnockoutGoal(isKnockout && hasGroupCount && groupCount <= 1
                ? KnockoutGoal.Champion
                : KnockoutGoal.OneQualifierPerGroup);
        }

        UpdatePlacementPlayoffVisibility();
        UpdateScheduleTimingSplitVisibility();
    }

    private void UpdatePlacementPlayoffVisibility()
    {
        var showPlacementOptions = GetCompetitionMode() is CompetitionMode.SinglesKnockout or CompetitionMode.TeamKnockout
            && GetKnockoutGoal() == KnockoutGoal.Champion;
        PlacementPlayoffPanel.IsVisible = showPlacementOptions;
        if (!showPlacementOptions)
        {
            PlacementPlayoffBox.SelectedIndex = 0;
        }
    }

    private void UpdateDrawPdfOptionsVisibility()
    {
        var showPdfOptions = _latestResult?.Settings.IsKnockout == true
            && GetCompetitionMode() is CompetitionMode.SinglesKnockout or CompetitionMode.TeamKnockout
            && GetExportFormat(DrawExportFormatBox) is WorkflowExportFormat.A4Pdf or WorkflowExportFormat.All;
        DrawPdfRowsPanel.IsVisible = showPdfOptions;
        DrawPdfColumnsPanel.IsVisible = showPdfOptions;
    }

    private void UpdateScheduleTimingSplitVisibility()
    {
        if (ScheduleTimingSplitPanel is null
            || BeforeBoundarySettingsPanel is null
            || ScheduleDefaultTimingLabel is null
            || ScheduleMaxMatchesLabel is null)
        {
            return;
        }

        var showTimingSplit = ShouldShowScheduleTimingSplit();
        var useTimingSplit = showTimingSplit && GetSelectedComboBoxTagInt(ScheduleTimingBoundaryBox) > 0;
        ScheduleTimingSplitPanel.IsVisible = showTimingSplit;
        BeforeBoundarySettingsPanel.IsVisible = useTimingSplit;
        ScheduleDefaultTimingLabel.Text = useTimingSplit ? "分界线后设置（关键轮次）" : "统一赛程设置";
        ScheduleMaxMatchesLabel.Text = useTimingSplit ? "本段最多场/日" : "每日最多场";
    }

    private bool ShouldShowScheduleTimingSplit()
    {
        return GetCompetitionMode() is CompetitionMode.SinglesKnockout or CompetitionMode.TeamKnockout
            && GetKnockoutGoal() == KnockoutGoal.Champion
            && _latestResult?.Settings.IsKnockout == true
            && _latestResult.Settings.KnockoutGoal == KnockoutGoal.Champion;
    }

    private bool TryGetGroupCount(out int groupCount)
    {
        return int.TryParse(GroupCountBox.Text?.Trim(), out groupCount);
    }

    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    private void SelectKnockoutGoal(KnockoutGoal knockoutGoal)
    {
        KnockoutGoalBox.SelectedIndex = knockoutGoal == KnockoutGoal.Champion ? 1 : 0;
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

    private async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 620,
            Height = 360,
            MinWidth = 520,
            MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        var okButton = new Button
        {
            Content = "知道了",
            MinWidth = 100,
            Background = ThemeBrush("AppAccentBrush", Color.FromRgb(15, 95, 159)),
            Foreground = ThemeBrush("AppAccentTextBrush", Colors.White),
            BorderBrush = ThemeBrush("AppAccentBrush", Color.FromRgb(15, 95, 159))
        };
        okButton.Click += (_, _) => dialog.Close();

        dialog.Content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            },
            Background = ThemeBrush("AppBackgroundBrush", Colors.White),
            Margin = new Avalonia.Thickness(22),
            Children =
            {
                new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ThemeBrush("AppTextBrush", Color.FromRgb(31, 41, 55)),
                        LineHeight = 22
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Avalonia.Thickness(0, 18, 0, 0),
                    Children = { okButton },
                    [Grid.RowProperty] = 1
                }
            }
        };

        await dialog.ShowDialog(this);
    }

    private Task ShowImportWarningsIfNeededAsync(IReadOnlyList<ParticipantImportWarning> warnings)
    {
        var duplicateWarnings = warnings
            .Where(warning => warning.Kind == ParticipantImportWarningKind.DuplicatePlayerName)
            .ToList();
        if (duplicateWarnings.Count == 0)
        {
            return Task.CompletedTask;
        }

        var message = "导入名单时发现同名选手，请优先通过“学号”或“搭档学号”确认是否为不同的人："
            + Environment.NewLine
            + Environment.NewLine
            + FormatImportWarningList(duplicateWarnings)
            + Environment.NewLine
            + Environment.NewLine
            + "这些提醒不会阻止预览抽签，但建议在抽签前完成身份核对。";
        return ShowInfoAsync("名单提醒", message);
    }

    private static string FormatImportWarningList(IReadOnlyList<ParticipantImportWarning> warnings)
    {
        return string.Join(Environment.NewLine, warnings.Select((warning, index) => $"{index + 1}. {warning.Detail}"));
    }

    private void ShowParticipantRosterWindow()
    {
        var seedEditors = new List<ParticipantSeedEditor>();
        var rosterZoom = 1.0;
        var window = new Window
        {
            Title = "参赛选手/队伍信息",
            Width = 1120,
            Height = 640,
            MinWidth = 760,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        var title = new TextBlock
        {
            Text = $"参赛选手/队伍信息 · {_participants.Count} 个参赛单位",
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(47, 22, 93)),
            Margin = new Avalonia.Thickness(0, 0, 0, 14)
        };
        var hint = new TextBlock
        {
            Text = "可在此直接修改“是否种子”和“种子序号”；两项都留空即按非种子处理，修改后点击“应用修改”再预览抽签。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(90, 105, 130)),
            Margin = new Avalonia.Thickness(0, 0, 0, 12),
            [Grid.RowProperty] = 1
        };
        var rosterGrid = BuildParticipantRosterGrid(_participants, seedEditors);
        var rosterScale = new LayoutTransformControl
        {
            Child = rosterGrid,
            LayoutTransform = new ScaleTransform { ScaleX = rosterZoom, ScaleY = rosterZoom }
        };
        var zoomText = new TextBlock
        {
            Text = "100%",
            MinWidth = 48,
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeight.Bold,
            Foreground = ThemeBrush("AppTitleBrush", Color.FromRgb(47, 22, 93)),
            VerticalAlignment = VerticalAlignment.Center
        };
        void SetRosterZoom(double value)
        {
            rosterZoom = Math.Clamp(value, ParticipantRosterMinZoom, ParticipantRosterMaxZoom);
            rosterScale.LayoutTransform = new ScaleTransform { ScaleX = rosterZoom, ScaleY = rosterZoom };
            zoomText.Text = $"{Math.Round(rosterZoom * 100)}%";
        }

        var zoomControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 0, 0, 10),
            Children =
            {
                new TextBlock
                {
                    Text = "缩放",
                    Foreground = ThemeBrush("AppMutedTextBrush", Color.FromRgb(100, 116, 139)),
                    VerticalAlignment = VerticalAlignment.Center
                },
                CreateCrossEventWindowButton("缩小", (_, _) => SetRosterZoom(rosterZoom - ParticipantRosterZoomStep)),
                zoomText,
                CreateCrossEventWindowButton("100%", (_, _) => SetRosterZoom(1.0)),
                CreateCrossEventWindowButton("放大", (_, _) => SetRosterZoom(rosterZoom + ParticipantRosterZoomStep))
            },
            [Grid.RowProperty] = 2
        };
        var applyButton = new Button
        {
            Content = "应用修改",
            Classes = { "primary" },
            MinWidth = 110
        };
        var closeButton = new Button
        {
            Content = "关闭",
            Classes = { "secondary" },
            MinWidth = 90,
            Margin = new Avalonia.Thickness(10, 0, 0, 0)
        };
        applyButton.Click += (_, _) =>
        {
            try
            {
                ApplyParticipantSeedEdits(seedEditors);
                window.Close();
            }
            catch (Exception ex) when (ex is DrawValidationException or InvalidOperationException)
            {
                SetStatus(ex.Message, isError: true);
            }
        };
        closeButton.Click += (_, _) => window.Close();

        window.Content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            },
            Background = ThemeBrush("AppBackgroundBrush", Colors.White),
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                title,
                hint,
                zoomControls,
                new Border
                {
                    Background = ThemeBrush("AppSurfaceBrush", Colors.White),
                    BorderBrush = ThemeBrush("AppSoftBorderBrush", Color.FromRgb(210, 224, 240)),
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    ClipToBounds = true,
                    Child = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = rosterScale
                    },
                    [Grid.RowProperty] = 3
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Avalonia.Thickness(0, 14, 0, 0),
                    Children = { applyButton, closeButton },
                    [Grid.RowProperty] = 4
                }
            }
        };

        window.Show(this);
    }

    private Grid BuildParticipantRosterGrid(
        IReadOnlyList<DrawParticipant> participants,
        ICollection<ParticipantSeedEditor> seedEditors)
    {
        var headers = new[]
        {
            "序号",
            "姓名",
            "学号",
            "学院/学部",
            "搭档姓名",
            "搭档学号",
            "搭档学院/学部",
            "是否种子",
            "种子序号",
            "备注"
        };
        var rows = BuildParticipantRosterRows(participants);
        var table = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(70)),
                new ColumnDefinition(new GridLength(130)),
                new ColumnDefinition(new GridLength(130)),
                new ColumnDefinition(new GridLength(180)),
                new ColumnDefinition(new GridLength(130)),
                new ColumnDefinition(new GridLength(130)),
                new ColumnDefinition(new GridLength(180)),
                new ColumnDefinition(new GridLength(100)),
                new ColumnDefinition(new GridLength(100)),
                new ColumnDefinition(new GridLength(280))
            }
        };

        table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var column = 0; column < headers.Length; column++)
        {
            AddRosterCell(table, 0, column, headers[column], isHeader: true);
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var gridRow = rowIndex + 1;
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var values = new[]
            {
                row.Order,
                row.PrimaryName,
                row.PrimaryStudentId,
                row.TeamName,
                row.PartnerName,
                row.PartnerStudentId,
                row.PartnerTeamName,
                row.Note
            };

            for (var column = 0; column < 7; column++)
            {
                AddRosterCell(table, gridRow, column, values[column], isHeader: false);
            }

            var seedFlagBox = CreateSeedFlagBox(row.SeedFlag);
            var seedRankBox = CreateSeedRankBox(row.SeedRank);
            AddRosterControlCell(table, gridRow, 7, seedFlagBox);
            AddRosterControlCell(table, gridRow, 8, seedRankBox);
            AddRosterCell(table, gridRow, 9, row.Note, isHeader: false);
            seedEditors.Add(new ParticipantSeedEditor(rowIndex, row.Order, row.PrimaryName, seedFlagBox, seedRankBox));
        }

        return table;
    }

    private void AddRosterCell(Grid table, int row, int column, string text, bool isHeader)
    {
        var border = new Border
        {
            Background = isHeader
                ? ThemeBrush("AppTableHeaderBackgroundBrush", Color.FromRgb(226, 214, 248))
                : row % 2 == 0
                    ? ThemeBrush("AppSurfaceBrush", Color.FromRgb(255, 255, 255))
                    : ThemeBrush("AppSurfaceAltBrush", Color.FromRgb(248, 250, 252)),
            BorderBrush = ThemeBrush("AppSoftBorderBrush", Color.FromRgb(210, 224, 240)),
            BorderThickness = new Avalonia.Thickness(0, 0, 1, 1),
            Padding = new Avalonia.Thickness(10, 8),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = isHeader ? FontWeight.Bold : FontWeight.Normal,
                Foreground = isHeader
                    ? ThemeBrush("AppTableHeaderTextBrush", Color.FromRgb(47, 22, 93))
                    : ThemeBrush("AppTextBrush", Color.FromRgb(17, 24, 39))
            }
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        table.Children.Add(border);
    }

    private static ComboBox CreateSeedFlagBox(string seedFlag)
    {
        return new ComboBox
        {
            ItemsSource = new[] { "", "是", "否" },
            SelectedItem = string.IsNullOrWhiteSpace(seedFlag) ? "" : seedFlag,
            MinWidth = 78,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private static TextBox CreateSeedRankBox(string seedRank)
    {
        return new TextBox
        {
            Text = seedRank,
            PlaceholderText = "空",
            MinWidth = 72,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private void AddRosterControlCell(Grid table, int row, int column, Control control)
    {
        var border = new Border
        {
            Background = row % 2 == 0
                ? ThemeBrush("AppSurfaceBrush", Color.FromRgb(255, 255, 255))
                : ThemeBrush("AppSurfaceAltBrush", Color.FromRgb(248, 250, 252)),
            BorderBrush = ThemeBrush("AppSoftBorderBrush", Color.FromRgb(210, 224, 240)),
            BorderThickness = new Avalonia.Thickness(0, 0, 1, 1),
            Padding = new Avalonia.Thickness(8, 5),
            Child = control
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        table.Children.Add(border);
    }

    private void ApplyParticipantSeedEdits(IReadOnlyList<ParticipantSeedEditor> seedEditors)
    {
        var editedParticipants = _participants.ToArray();
        foreach (var editor in seedEditors)
        {
            var seedFlag = ParseRosterSeedFlag(editor.SeedFlagBox.SelectedItem?.ToString() ?? "", editor.OrderText);
            var seedRank = ParseRosterSeedRank(editor.SeedRankBox.Text ?? "", editor.OrderText);
            if (seedFlag == false && seedRank.HasValue)
            {
                throw new DrawValidationException($"序号 {editor.OrderText} {editor.DisplayName} 填写了种子序号，但“是否种子”为否。");
            }

            var isSeed = seedFlag == true || seedRank.HasValue;
            editedParticipants[editor.ParticipantIndex] = editedParticipants[editor.ParticipantIndex] with
            {
                IsSeed = isSeed,
                SeedRank = seedRank
            };
        }

        ValidateParticipantSeedEdits(editedParticipants);
        _participants = editedParticipants;
        _participantImportWarnings = _participantImportWarnings
            .Where(warning => warning.Kind == ParticipantImportWarningKind.DuplicatePlayerName)
            .Concat(BuildParticipantSeedEditWarnings(_participants))
            .ToList();
        _importWarnings = FormatParticipantWarnings(_participantImportWarnings);
        _latestWorkflowResult = null;
        _latestResult = null;
        ClearProgressReference();
        GroupCountStatText.Text = "-";
        PreviewStateText.Text = "待预览";
        SummaryText.Text = $"已更新 {_participants.Count} 个参赛单位的种子设置，请重新预览抽签。";
        SetWarnings(_importWarnings);
        GroupsList.ItemsSource = Array.Empty<PreviewGroupRow>();
        RoundOneList.ItemsSource = Array.Empty<PreviewGroupRow>();
        ByeList.ItemsSource = Array.Empty<PreviewGroupRow>();
        ClearSchedulePreview();
        SetStatus("参赛名单种子设置已更新；两项留空的参赛单位会按非种子处理。");
    }

    private static bool? ParseRosterSeedFlag(string value, string orderText)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (string.Equals(trimmed, "是", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(trimmed, "否", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "n", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new DrawValidationException($"序号 {orderText} 的“是否种子”只能选择“是”“否”或留空。");
    }

    private static int? ParseRosterSeedRank(string value, string orderText)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (!int.TryParse(trimmed, out var rank) || rank <= 0)
        {
            throw new DrawValidationException($"序号 {orderText} 的“种子序号”必须是大于 0 的整数，或留空。");
        }

        return rank;
    }

    private static void ValidateParticipantSeedEdits(IReadOnlyList<DrawParticipant> participants)
    {
        var seedCount = participants.Count(participant => participant.IsSeed);
        var maxSeedCount = OfficialDrawRules.GetMaximumSeedCount(participants.Count);
        if (seedCount > maxSeedCount)
        {
            throw new DrawValidationException($"当前参赛数量最多设置 {maxSeedCount} 个种子，当前设置了 {seedCount} 个。");
        }

        var overflowSeedRank = participants.FirstOrDefault(participant =>
            participant.SeedRank.HasValue && participant.SeedRank.Value > maxSeedCount);
        if (overflowSeedRank is not null)
        {
            throw new DrawValidationException(
                $"种子序号不能大于当前参赛数量允许的种子数量 {maxSeedCount}：{overflowSeedRank.DisplayName} 的种子序号为 {overflowSeedRank.SeedRank}。");
        }

        var duplicateSeedRank = participants
            .Where(participant => participant.SeedRank.HasValue)
            .GroupBy(participant => participant.SeedRank!.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSeedRank is not null)
        {
            var duplicateNames = string.Join("、", duplicateSeedRank.Select(participant => participant.DisplayName));
            throw new DrawValidationException($"参赛名单中存在重复种子序号 {duplicateSeedRank.Key}：{duplicateNames}");
        }
    }

    private static IReadOnlyList<ParticipantImportWarning> BuildParticipantSeedEditWarnings(
        IReadOnlyList<DrawParticipant> participants)
    {
        return participants
            .Where(participant => participant.IsSeed && !participant.SeedRank.HasValue)
            .Select(participant => new ParticipantImportWarning(
                ParticipantImportWarningKind.UnrankedSeed,
                "种子未编号",
                $"“{participant.DisplayName}”标记为种子，但未填写种子序号。"))
            .ToList();
    }

    private static IReadOnlyList<string> FormatParticipantWarnings(IReadOnlyList<ParticipantImportWarning> warnings)
    {
        return warnings
            .Select(warning => $"{warning.Summary}：{warning.Detail}")
            .ToList();
    }

    private static IReadOnlyList<ParticipantRosterRow> BuildParticipantRosterRows(IReadOnlyList<DrawParticipant> participants)
    {
        return participants
            .Select((participant, index) => new ParticipantRosterRow(
                (index + 1).ToString(),
                GetPrimaryRosterName(participant),
                participant.PrimaryStudentId ?? "",
                participant.TeamName ?? "",
                participant.PartnerName ?? "",
                participant.PartnerStudentId ?? "",
                participant.PartnerTeamName ?? "",
                participant.IsSeed ? "是" : "",
                participant.SeedRank?.ToString() ?? "",
                participant.Note ?? ""))
            .ToList();
    }

    private static string GetPrimaryRosterName(DrawParticipant participant)
    {
        if (!string.IsNullOrWhiteSpace(participant.PrimaryName))
        {
            return participant.PrimaryName;
        }

        return string.IsNullOrWhiteSpace(participant.TeamName)
            ? participant.DisplayName
            : "";
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
            $"已恢复 {state.Snapshot.Schedule.Matches.Count} 场赛程，累计完成 {state.Results.Count} 场。";
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
                string.Join("，", day.Courts)));
        }

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
        ScheduleAutoStrategyBox.SelectionChanged -= ScheduleAutoStrategyBox_SelectionChanged;
        SelectComboBoxTag(ScheduleAutoStrategyBox, settings.AutoSchedulingStrategy.ToString());
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
        return $"项目 {board.Sources.Count}，场次 {board.Items.Count}，兼项 {board.MultiEventPlayerCount}；"
               + $"严重 {board.Report.SevereCount}，警告 {board.Report.WarningCount}，提醒/推演 {board.Report.NoticeCount}，"
               + $"冲突卡 {board.BlockingConflictItemCount}{refereeText}{zoomText}{changedText}";
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
        return $"{prefix}：兼项选手 {board.MultiEventPlayerCount} 人，严重 {board.Report.SevereCount} 条，警告 {board.Report.WarningCount} 条，"
               + $"同日/负荷推演提醒 {board.Report.NoticeCount} 条，{refereeText}冲突卡片 {board.BlockingConflictItemCount} 张。";
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
        if (GetCompetitionMode() is not (CompetitionMode.SinglesKnockout or CompetitionMode.TeamKnockout))
        {
            return KnockoutGoal.Champion;
        }

        if (!TryGetGroupCount(out var groupCount))
        {
            return KnockoutGoal.OneQualifierPerGroup;
        }

        if (groupCount <= 1)
        {
            return KnockoutGoal.Champion;
        }

        return KnockoutGoalPanel.IsVisible && KnockoutGoalBox.SelectedIndex == 1
            ? KnockoutGoal.Champion
            : KnockoutGoal.OneQualifierPerGroup;
    }

    private PlacementPlayoff GetPlacementPlayoff()
    {
        if (GetCompetitionMode() is not (CompetitionMode.SinglesKnockout or CompetitionMode.TeamKnockout)
            || GetKnockoutGoal() != KnockoutGoal.Champion
            || !PlacementPlayoffPanel.IsVisible)
        {
            return PlacementPlayoff.None;
        }

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

        if (_uiReady)
        {
            UpdateKnockoutGoalVisibility();
            UpdateScheduleTimingSplitVisibility();
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
        ClearSingleScheduleUndoStack();
        ScheduleSummaryText.Text = "尚未生成赛程";
        UpdateScheduleConstraintButton();
        ScheduleList.ItemsSource = Array.Empty<SchedulePreviewRow>();
        RefreshScheduleBoardWindow();
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

    private void SetStatus(string message, bool isWarning = false, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? ThemeBrush("AppErrorTextBrush", Color.FromRgb(185, 28, 28))
            : isWarning
                ? ThemeBrush("AppWarningTextBrush", Color.FromRgb(217, 119, 6))
                : ThemeBrush("AppMutedTextBrush", Color.FromRgb(65, 80, 106));
        StatusDot.Background = isError
            ? ThemeBrush("AppErrorTextBrush", Color.FromRgb(185, 28, 28))
            : isWarning
                ? ThemeBrush("AppWarningTextBrush", Color.FromRgb(217, 119, 6))
                : ThemeBrush("AppStatusDotBrush", Color.FromRgb(25, 169, 116));
        StatusBar.Background = isError
            ? ThemeBrush("AppErrorCardBackgroundBrush", Color.FromRgb(254, 242, 242))
            : isWarning
                ? ThemeBrush("AppWarningCardBackgroundBrush", Color.FromRgb(255, 250, 235))
                : ThemeBrush("AppStatusBarBackgroundBrush", Colors.White);
    }

    public sealed record PreviewGroupRow(
        string Title,
        string CountText,
        string Body);

    public sealed record SchedulePreviewRow(
        string Order,
        string Status,
        string DayLabel,
        string TimeRange,
        string Court,
        string GroupName,
        string Phase,
        string MatchName,
        string SideA,
        string SideB,
        string Note,
        IBrush BackgroundBrush,
        IBrush BorderBrush,
        IBrush BadgeBrush,
        IBrush BadgeForeground);

    private sealed record ParticipantRosterRow(
        string Order,
        string PrimaryName,
        string PrimaryStudentId,
        string TeamName,
        string PartnerName,
        string PartnerStudentId,
        string PartnerTeamName,
        string SeedFlag,
        string SeedRank,
        string Note);

    private sealed record ParticipantSeedEditor(
        int ParticipantIndex,
        string OrderText,
        string DisplayName,
        ComboBox SeedFlagBox,
        TextBox SeedRankBox);

    private sealed record CrossEventPlayerSummaryRow(
        CrossEventPlayerMultiEntry Entry,
        int Order,
        string Title,
        string Detail)
    {
        public override string ToString()
        {
            return $"{Title}\n{Detail}";
        }
    }

    private enum CrossEventPlayerSortMode
    {
        Default,
        RestAscending,
        RestDescending
    }
}
