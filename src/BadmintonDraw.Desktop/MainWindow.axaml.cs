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
    private const double ScheduleBoardDragSourceOpacity = 0.45;
    private const double ScheduleBoardAutoScrollEdgeThreshold = 64;
    private const double ScheduleBoardAutoScrollStep = 36;
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
    private ScheduleSettings? _singleScheduleRecommendedCustomSettings;
    private ScheduleSettings? _singleScheduleLastAcceptedSettings;
    private bool _updatingScheduleCustomControls;
    private bool _runningSingleScheduleCustom;
    private bool _showingSingleScheduleCustomFailureDialog;
    private ScheduleCustomAnchor _pendingScheduleCustomAnchor = ScheduleCustomAnchor.None;
    private readonly Dictionary<string, Slider> _scheduleDayLoadSliders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _scheduleDayLoadLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Min, double Max, double Recommended)> _scheduleDayLoadRecommendedRanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Slider> _scheduleStageWaveSliders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _scheduleStageWaveLabels = new(StringComparer.Ordinal);
    private DispatcherTimer? _scheduleCustomRecalculateTimer;
    private string? _loadedInputPath;
    private string? _progressFilePath;
    private TournamentProgressState? _progressState;
    private double _scheduleBoardWindowZoom = 1.0;
    private Window? _scheduleBoardWindow;
    private ComboBox? _scheduleBoardWindowDayBox;
    private StackPanel? _scheduleBoardWindowDayPickerPanel;
    private TextBlock? _scheduleBoardWindowSummaryText;
    private Grid? _scheduleBoardWindowGrid;
    private ScrollViewer? _scheduleBoardWindowScrollViewer;
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
    private ScrollViewer? _crossEventBoardWindowScrollViewer;
    private Button? _crossEventBoardWindowUndoButton;
    private StackPanel? _crossEventBoardWindowDayTabs;
    private readonly Stack<CrossEventScheduleUndoSnapshot> _crossEventScheduleUndoStack = new();
    private readonly Dictionary<string, Border> _crossEventBoardWindowMatchCards = new(StringComparer.Ordinal);
    private Window? _crossEventConflictWindow;
    private readonly Dictionary<string, ScheduleBoardMoveValidationResult> _scheduleBoardMoveValidationCache = new(StringComparer.Ordinal);
    private Border? _scheduleBoardDragHoverCell;
    private Border? _scheduleBoardDragSourceCard;
    private Control? _scheduleBoardDragFeedbackCard;
    private double _scheduleBoardDragSourceOriginalOpacity = 1.0;
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

    private enum ScheduleCustomAnchorKind
    {
        None,
        Recommended,
        DayLoad,
        StageWave,
        StageWaveEnabled
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

    private sealed record ScheduleCustomAnchor(
        ScheduleCustomAnchorKind Kind,
        string? DayLabel = null)
    {
        public static ScheduleCustomAnchor None { get; } = new(ScheduleCustomAnchorKind.None);

        public string Describe()
        {
            return Kind switch
            {
                ScheduleCustomAnchorKind.DayLoad when !string.IsNullOrWhiteSpace(DayLabel) => $"{DayLabel} 目标负载率",
                ScheduleCustomAnchorKind.StageWave when !string.IsNullOrWhiteSpace(DayLabel) => $"{DayLabel} 阶段推进",
                ScheduleCustomAnchorKind.StageWaveEnabled => "阶段波次推进",
                ScheduleCustomAnchorKind.Recommended => "推荐分布",
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

    private sealed record ScheduleCustomSliderTag(
        ScheduleCustomAnchorKind Kind,
        string DayLabel);

    private sealed record SingleScheduleUndoSnapshot(
        SchedulePlan Schedule,
        TournamentProgressState? ProgressState,
        string? ProgressFilePath,
        string? DayLabel);

    private sealed record CrossEventScheduleUndoSnapshot(
        CrossEventScheduleBoard Board,
        string? DayLabel);

    private sealed record ScheduleResourceListItem(
        int Index,
        string DisplayText);

    public MainWindow()
    {
        InitializeComponent();
        ApplyWindowIcon();
        SeedBox.Text = DrawWorkflow.GenerateSeed();
        ScheduleDatePicker.SelectedDate = DateTime.Today;
        ScheduleDaysList.ItemsSource = _scheduleDays;
        RefreshScheduleResourcePanel();
        ScheduleCustomSchedulingPanel.IsVisible = false;
        CrossEventCustomSchedulingPanel.IsVisible = false;
        UpdateScheduleUndoButtons();
        UpdateCrossEventUndoButtons();
        ScheduleStageWaveBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty)
            {
                QueueScheduleCustomRecalculate(new ScheduleCustomAnchor(ScheduleCustomAnchorKind.StageWaveEnabled));
            }
        };
        CrossEventStageWaveBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty)
            {
                QueueCrossEventCustomRecalculate(new CrossEventCustomAnchor(CrossEventCustomAnchorKind.StageWaveEnabled));
            }
        };
        _scheduleCustomRecalculateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _scheduleCustomRecalculateTimer.Tick += ScheduleCustomRecalculateTimer_Tick;
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
