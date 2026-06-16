using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using BadmintonDraw.Workflows;
using Microsoft.Win32;

namespace BadmintonDraw.App;

public partial class MainWindow : Window
{
    private readonly DrawWorkflow _drawWorkflow = new();
    private readonly ScheduleWorkflow _scheduleWorkflow = new();
    private readonly TournamentProgressWorkflow _progressWorkflow = new();
    private readonly CrossEventConflictWorkflow _crossEventConflictWorkflow = new();
    private const double CrossEventBoardMinZoom = 0.65;
    private const double CrossEventBoardWindowMinZoom = 0.25;
    private const double CrossEventBoardMaxZoom = 1.6;
    private const double CrossEventBoardZoomStep = 0.15;
    private const string ScheduleDragPrefix = "schedule:";
    private IReadOnlyList<DrawParticipant> _participants = Array.Empty<DrawParticipant>();
    private IReadOnlyList<ParticipantImportWarning> _importWarnings = Array.Empty<ParticipantImportWarning>();
    private readonly ObservableCollection<ScheduleDayRow> _scheduleDays = [];
    private string? _loadedInputPath;
    private DrawResult? _latestResult;
    private DrawWorkflowResult? _latestWorkflowResult;
    private SchedulePlan? _latestSchedule;
    private string? _progressFilePath;
    private TournamentProgressState? _progressState;
    private double _scheduleBoardWindowZoom = 1.0;
    private Window? _scheduleBoardWindow;
    private ComboBox? _scheduleBoardWindowDayBox;
    private TextBlock? _scheduleBoardWindowSummaryText;
    private Grid? _scheduleBoardWindowGrid;
    private CrossEventScheduleBoard? _crossEventScheduleBoard;
    private double _crossEventBoardZoom = 1.0;
    private double _crossEventBoardWindowZoom = 1.0;
    private Window? _crossEventBoardWindow;
    private ComboBox? _crossEventBoardWindowDayBox;
    private TextBlock? _crossEventBoardWindowSummaryText;
    private Grid? _crossEventBoardWindowGrid;

    public MainWindow()
    {
        InitializeComponent();
        ApplyInitialWindowSize();
        SeedBox.Text = GenerateSeed();
        UpdateEventKindForMode();
        UpdateKnockoutGoalVisibility();
        UpdateScheduleTimingSplitVisibility();
        UpdatePreviewBadges();
        UpdateExportOptionsVisibility();
        ScheduleDatePicker.SelectedDate = DateTime.Today;
        ApplyScheduleCourtPreset();
        ScheduleDaysGrid.ItemsSource = _scheduleDays;
        AddCurrentScheduleDay();
    }

    private void ApplyInitialWindowSize()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width * 0.92));
        Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height * 0.9));
    }

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel 文件 (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            CheckFileExists = true,
            Title = "选择参赛名单"
        };

        if (dialog.ShowDialog(this) == true)
        {
            InputPathBox.Text = dialog.FileName;
            TryLoadParticipants();
        }
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (TryGenerate())
        {
            UpdateExportOptionsVisibility();
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_latestWorkflowResult is null && !TryGenerate())
        {
            return;
        }

        var exportFormat = GetExportFormat();
        var dialog = new SaveFileDialog
        {
            Filter = GetDialogFilter(exportFormat),
            DefaultExt = GetExportExtension(exportFormat),
            AddExtension = true,
            FileName = DrawWorkflow.BuildDefaultDrawFileName(_latestResult!, _loadedInputPath ?? InputPathBox.Text, ToWorkflowExportFormat(exportFormat)),
            Title = "保存抽签结果"
        };

        if (dialog.ShowDialog(this) == true && _latestResult is not null)
        {
            try
            {
                var outputPaths = ExportDrawResultFiles(dialog.FileName, exportFormat);
                SetStatus($"已导出：{FormatOutputPaths(outputPaths)}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or DrawValidationException)
            {
                SetStatus(ex.Message, isError: true);
            }
        }
    }

    private void CreateTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel 文件 (*.xlsx)|*.xlsx",
            FileName = "深大羽协参赛名单模板.xlsx",
            Title = "保存名单模板"
        };

        if (dialog.ShowDialog(this) == true)
        {
            _drawWorkflow.WriteTemplate(dialog.FileName);
            SetStatus($"已生成名单模板：{dialog.FileName}");
        }
    }

    private void BrowseParticipants_Click(object sender, RoutedEventArgs e)
    {
        if (_participants.Count == 0)
        {
            SetStatus("请先选择并导入参赛名单。", StatusKind.Warning);
            return;
        }

        ShowParticipantRosterWindow();
    }

    private void GenerateSeed_Click(object sender, RoutedEventArgs e)
    {
        SeedBox.Text = GenerateSeed();
    }

    private void GenerateSchedule_Click(object sender, RoutedEventArgs e)
    {
        TryGenerateSchedule();
    }

    private void AddScheduleDay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AddCurrentScheduleDay();
            SetStatus("已添加赛程日。");
        }
        catch (Exception ex) when (ex is DrawValidationException or InvalidOperationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void RemoveScheduleDay_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleDaysGrid.SelectedItem is ScheduleDayRow row)
        {
            _scheduleDays.Remove(row);
            SetStatus("已删除选中的赛程日。");
        }
    }

    private void ExportSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_latestSchedule is null && !TryGenerateSchedule())
        {
            return;
        }

        if (_latestSchedule is null || _latestResult is null)
        {
            return;
        }

        if (!_latestSchedule.IsComplete)
        {
            SetStatus(
                $"当前赛程资源不足，仍有 {_latestSchedule.UnscheduledMatches.Count} 场无法安排；不支持导出不完整赛程。",
                StatusKind.Warning);
            return;
        }

        var exportFormat = GetScheduleExportFormat();
        var dialog = new SaveFileDialog
        {
            Filter = GetDialogFilter(exportFormat),
            DefaultExt = GetExportExtension(exportFormat),
            AddExtension = true,
            FileName = ScheduleWorkflow.BuildDefaultScheduleFileName(_latestResult, _loadedInputPath ?? InputPathBox.Text, ToWorkflowExportFormat(exportFormat)),
            Title = "保存赛程表"
        };

        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                var schedulePaths = ExportScheduleFiles(dialog.FileName, exportFormat);
                SetStatus($"完整赛程表已导出：{FormatOutputPaths(schedulePaths)}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or DrawValidationException)
            {
                SetStatus(ex.Message, isError: true);
            }
        }
    }

    private void ExportFirstDayPackage_Click(object sender, RoutedEventArgs e)
    {
        if (_latestSchedule is null && !TryGenerateSchedule())
        {
            return;
        }

        if (_latestSchedule is null || _latestWorkflowResult is null)
        {
            return;
        }

        if (!_latestSchedule.IsComplete)
        {
            SetStatus(
                $"当前赛程资源不足，仍有 {_latestSchedule.UnscheduledMatches.Count} 场无法安排；不支持导出不完整赛程。",
                StatusKind.Warning);
            return;
        }

        var outputDirectory = PickFolderPath("选择首日材料包保存文件夹");
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
                    GetDrawVisualOptions(ExportFormat.A4Pdf))
                : _progressWorkflow.ExportFirstDayPackage(
                    outputDirectory,
                    _loadedInputPath ?? InputPathBox.Text,
                    _latestWorkflowResult,
                    _latestSchedule,
                    includePrintablePdf: true,
                    GetDrawVisualOptions(ExportFormat.A4Pdf));
            SetStatus($"{package.DayLabel} 首日材料包已导出到：{package.OutputDirectory}（共 {package.OutputPaths.Count} 个文件）。");
        }
        catch (Exception ex) when (ex is TournamentProgressException or ExcelImportException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void ImportMatchRecordAndExportNext_Click(object sender, RoutedEventArgs e)
    {
        if (_latestSchedule is null && !TryGenerateSchedule())
        {
            return;
        }

        if (_latestSchedule is null)
        {
            return;
        }

        if (!_latestSchedule.IsComplete)
        {
            SetStatus(
                $"当前赛程资源不足，仍有 {_latestSchedule.UnscheduledMatches.Count} 场无法安排；不支持导出不完整赛程。",
                StatusKind.Warning);
            return;
        }

        var importDialog = new OpenFileDialog
        {
            Filter = "Excel 文件 (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            CheckFileExists = true,
            Multiselect = true,
            Title = "选择已填写的赛程记录表（可多选）"
        };

        if (importDialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_progressFilePath))
        {
            ImportMatchRecordsIntoProgress(importDialog.FileNames);
            return;
        }

        try
        {
            var importResult = _scheduleWorkflow.ImportMatchRecords(importDialog.FileNames);
            if (importResult.ExpectedMatchCount == 0)
            {
                SetStatus("所选记录表中没有识别到可处理的比赛场次，请确认是本工具导出的赛程记录表。", isError: true);
                return;
            }

            var nextDayLabel = ScheduleWorkflow.GetNextMatchRecordDayLabel(_latestSchedule, importResult);
            if (string.IsNullOrWhiteSpace(nextDayLabel))
            {
                SetStatus("已读取比赛结果，但当前赛程没有下一比赛日可导出。", StatusKind.Warning);
                return;
            }

            if (importResult.HasWarnings && !ConfirmMatchRecordWarnings(importResult, nextDayLabel))
            {
                SetStatus("已取消导出，请修正记录表后重新导入。", StatusKind.Warning);
                return;
            }

            if (_latestWorkflowResult is null)
            {
                SetStatus("请先生成或打开完整赛事，再导出下一比赛日材料包。", isError: true);
                return;
            }

            var outputDirectory = PickFolderPath("选择下一比赛日材料包保存文件夹");
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
                GetDrawVisualOptions(ExportFormat.A4Pdf));
            var pendingText = importResult.PendingMatchNames.Count > 0
                ? $"，顺延 {importResult.PendingMatchNames.Count} 场未决比赛"
                : "";
            SetStatus(
                $"已从 {importDialog.FileNames.Length} 张记录表累计读取 {importResult.Results.Count} 场结果{pendingText}，"
                + $"并导出 {package.DayLabel} 材料包到：{package.OutputDirectory}（共 {package.OutputPaths.Count} 个文件）。");
        }
        catch (Exception ex) when (ex is ExcelImportException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void CreateProgress_Click(object sender, RoutedEventArgs e)
    {
        if (_progressState is not null)
        {
            SetStatus("当前已经打开赛事存档；后续导入会自动更新该文件。", StatusKind.Warning);
            return;
        }

        if (_latestSchedule is null && !TryGenerateSchedule())
        {
            return;
        }

        if (_latestSchedule is null || _latestWorkflowResult is null || _latestResult is null)
        {
            SetStatus("请先生成完整抽签和赛程。", isError: true);
            return;
        }

        if (!_latestSchedule.IsComplete)
        {
            SetStatus("当前赛程不完整，不能创建赛事存档。", StatusKind.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "深大羽协赛事存档 (*.szbd)|*.szbd",
            DefaultExt = ".szbd",
            AddExtension = true,
            FileName = TournamentProgressWorkflow.BuildDefaultFileName(
                _latestResult,
                _loadedInputPath ?? InputPathBox.Text),
            Title = "创建赛事存档"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _progressState = _progressWorkflow.Create(
                dialog.FileName,
                _loadedInputPath ?? InputPathBox.Text,
                _latestWorkflowResult,
                _latestSchedule);
            _progressFilePath = dialog.FileName;
            UpdateProgressDisplay();
            SetStatus($"赛事存档已创建：{dialog.FileName}");
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void ExportCrossEventConflictReport_Click(object sender, RoutedEventArgs e)
    {
        if (_crossEventScheduleBoard is not null)
        {
            ExportCurrentCrossEventBoardReport();
            return;
        }

        var openDialog = new OpenFileDialog
        {
            Filter = "深大羽协赛事存档 (*.szbd)|*.szbd",
            DefaultExt = ".szbd",
            CheckFileExists = true,
            Multiselect = true,
            Title = "选择需要一起检查的赛事存档（至少两个）"
        };
        if (openDialog.ShowDialog(this) != true)
        {
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Filter = "Excel 文件 (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = CrossEventConflictWorkflow.BuildDefaultReportFileName(),
            Title = "保存多项目排程检查报告"
        };
        if (saveDialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var minimumRestMinutes = GetCrossEventMinimumRestMinutes();
            var result = _crossEventConflictWorkflow.ExportProgressReport(
                openDialog.FileNames,
                saveDialog.FileName,
                minimumRestMinutes);
            SetStatus(
                $"多项目排程检查报告已导出：{result.OutputPath}。"
                + $"严重 {result.Report.SevereCount} 条，间隔过短 {result.Report.WarningCount} 条，"
                + $"同日提醒 {result.Report.NoticeCount} 条。");
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void LoadCrossEventScheduleBoard_Click(object sender, RoutedEventArgs e)
    {
        var openDialog = new OpenFileDialog
        {
            Filter = "深大羽协赛事存档 (*.szbd)|*.szbd",
            DefaultExt = ".szbd",
            CheckFileExists = true,
            Multiselect = true,
            Title = "选择需要一起编排的赛事存档（至少两个）"
        };
        if (openDialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _crossEventScheduleBoard = _crossEventConflictWorkflow.LoadScheduleBoard(
                openDialog.FileNames,
                GetCrossEventMinimumRestMinutes());
            RefreshCrossEventScheduleBoard();
            RefreshCrossEventBoardWindow(GetSelectedCrossEventDayLabel());
            SetStatus(BuildCrossEventStatus("多项目赛程已加载", _crossEventScheduleBoard));
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void AutoAdjustCrossEventSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_crossEventScheduleBoard is null)
        {
            SetStatus("请先加载多项目赛程。", isError: true);
            return;
        }

        try
        {
            var result = _crossEventConflictWorkflow.AutoAdjustScheduleBoard(_crossEventScheduleBoard);
            _crossEventScheduleBoard = result.Board;
            RefreshCrossEventScheduleBoard(GetSelectedCrossEventDayLabel());
            RefreshCrossEventBoardWindow(GetSelectedCrossEventDayLabel());
            var message = $"自动调整完成：移动 {result.MovedCount} 场，仍有 {result.RemainingBlockingConflictItemCount} 张冲突卡片。";
            if (result.Messages.Count > 0)
            {
                message += $" {string.Join("；", result.Messages.Take(3))}";
            }

            SetStatus(
                message,
                result.RemainingBlockingConflictItemCount > 0 ? StatusKind.Warning : StatusKind.Normal);
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void SaveCrossEventScheduleBoard_Click(object sender, RoutedEventArgs e)
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
            RefreshCrossEventScheduleBoard(GetSelectedCrossEventDayLabel());
            RefreshCrossEventBoardWindow(GetSelectedCrossEventDayLabel());
            SetStatus($"已保存 {result.UpdatedPaths.Count} 个赛事存档，备份 {result.BackupPaths.Count} 个。");
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void ExportCurrentCrossEventBoardReport()
    {
        if (_crossEventScheduleBoard is null)
        {
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Filter = "Excel 文件 (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = CrossEventConflictWorkflow.BuildDefaultReportFileName(),
            Title = "保存多项目排程检查报告"
        };
        if (saveDialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var result = _crossEventConflictWorkflow.ExportScheduleBoardReport(_crossEventScheduleBoard, saveDialog.FileName);
            SetStatus(
                $"当前多项目排程检查报告已导出：{result.OutputPath}。"
                + $"严重 {result.Report.SevereCount} 条，间隔过短 {result.Report.WarningCount} 条，"
                + $"同日提醒 {result.Report.NoticeCount} 条。");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void ExportCrossEventMergedMaterials_Click(object sender, RoutedEventArgs e)
    {
        if (_crossEventScheduleBoard is null)
        {
            SetStatus("请先加载多项目赛程。", isError: true);
            return;
        }

        var outputDirectory = PickFolderPath("选择合并材料包保存文件夹");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        try
        {
            var result = _crossEventConflictWorkflow.ExportMergedScheduleMaterials(_crossEventScheduleBoard, outputDirectory);
            SetStatus(
                $"多项目合并材料包已导出到：{result.OutputDirectory}（共 {result.OutputPaths.Count} 个文件）。",
                _crossEventScheduleBoard.Report.WarningCount > 0 ? StatusKind.Warning : StatusKind.Normal);
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void CrossEventDayBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshCrossEventScheduleBoard(GetSelectedCrossEventDayLabel());
    }

    private void ShowCrossEventPlayerDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_crossEventScheduleBoard is null)
        {
            SetStatus("请先加载多项目赛程。", isError: true);
            return;
        }

        var rows = BuildCrossEventPlayerSummaryRows(_crossEventScheduleBoard.PlayerDetails, CrossEventPlayerSortMode.Default);
        if (rows.Count == 0)
        {
            SetStatus("当前没有识别到跨项目兼项选手。", StatusKind.Warning);
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
        var summaryGrid = CreateCrossEventPlayerSummaryGrid(rows);
        var appearanceGrid = CreateCrossEventPlayerAppearanceGrid();
        summaryGrid.SelectionChanged += (_, _) =>
        {
            if (summaryGrid.SelectedItem is CrossEventPlayerSummaryRow row)
            {
                appearanceGrid.ItemsSource = BuildCrossEventPlayerAppearanceRows(row.Entry.Appearances);
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
            summaryGrid.ItemsSource = BuildCrossEventPlayerSummaryRows(_crossEventScheduleBoard.PlayerDetails, sortMode);
            summaryGrid.SelectedIndex = 0;
        }

        sortBox.SelectionChanged += (_, _) => RefreshPlayerRows();

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = $"兼项选手 {rows.Count} 人；明细会随多项目赛程调整实时重新计算。",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 20, 95)),
            Margin = new Thickness(0, 0, 0, 12)
        });
        var sortPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 0, 0, 0)
        };
        sortPanel.Children.Add(new TextBlock
        {
            Text = "排序",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        sortPanel.Children.Add(sortBox);
        Grid.SetColumn(sortPanel, 1);
        header.Children.Add(sortPanel);
        root.Children.Add(header);

        var grids = new Grid();
        grids.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.46, GridUnitType.Star) });
        grids.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grids.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.54, GridUnitType.Star) });
        Grid.SetRow(grids, 1);
        root.Children.Add(grids);

        var summaryPanel = CreateDialogPanel("选手兼项汇总", summaryGrid);
        var detailPanel = CreateDialogPanel("该选手赛程明细", appearanceGrid);
        Grid.SetColumn(summaryPanel, 0);
        Grid.SetColumn(detailPanel, 2);
        grids.Children.Add(summaryPanel);
        grids.Children.Add(detailPanel);

        var workArea = SystemParameters.WorkArea;
        var dialog = new Window
        {
            Owner = this,
            Title = "兼项明细",
            Width = Math.Min(workArea.Width * 0.86, 1180),
            Height = Math.Min(workArea.Height * 0.82, 760),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root
        };
        RefreshPlayerRows();
        dialog.ShowDialog();
    }

    private static DockPanel CreateDialogPanel(string title, UIElement content)
    {
        var panel = new DockPanel();
        var label = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 59, 99)),
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(label, Dock.Top);
        panel.Children.Add(label);
        panel.Children.Add(content);
        return panel;
    }

    private static DataGrid CreateCrossEventPlayerSummaryGrid(IReadOnlyList<CrossEventPlayerSummaryRow> rows)
    {
        var grid = CreateReadOnlyDataGrid();
        AddGridTextColumn(grid, "序号", nameof(CrossEventPlayerSummaryRow.OrderText), 56);
        AddGridTextColumn(grid, "选手", nameof(CrossEventPlayerSummaryRow.PlayerName), 92);
        AddGridTextColumn(grid, "项目", nameof(CrossEventPlayerSummaryRow.EventNamesText), new DataGridLength(1, DataGridLengthUnitType.Star));
        AddGridTextColumn(grid, "场次", nameof(CrossEventPlayerSummaryRow.MatchCountText), 64);
        AddGridTextColumn(grid, "未完成", nameof(CrossEventPlayerSummaryRow.PendingMatchCountText), 74);
        AddGridTextColumn(grid, "严重", nameof(CrossEventPlayerSummaryRow.SevereIssueCountText), 58);
        AddGridTextColumn(grid, "间隔", nameof(CrossEventPlayerSummaryRow.WarningIssueCountText), 58);
        AddGridTextColumn(grid, "最短休息", nameof(CrossEventPlayerSummaryRow.ShortestRestText), 78);
        AddGridTextColumn(grid, "下一场", nameof(CrossEventPlayerSummaryRow.NextMatchText), new DataGridLength(1.2, DataGridLengthUnitType.Star));
        grid.ItemsSource = rows;
        return grid;
    }

    private static DataGrid CreateCrossEventPlayerAppearanceGrid()
    {
        var grid = CreateReadOnlyDataGrid();
        AddGridTextColumn(grid, "状态", nameof(CrossEventPlayerAppearanceRow.Status), 82);
        AddGridTextColumn(grid, "日期", nameof(CrossEventPlayerAppearanceRow.DayLabel), 104);
        AddGridTextColumn(grid, "时间", nameof(CrossEventPlayerAppearanceRow.TimeRange), 104);
        AddGridTextColumn(grid, "场地", nameof(CrossEventPlayerAppearanceRow.Court), 64);
        AddGridTextColumn(grid, "项目", nameof(CrossEventPlayerAppearanceRow.EventName), 118);
        AddGridTextColumn(grid, "阶段", nameof(CrossEventPlayerAppearanceRow.Phase), 118);
        AddGridTextColumn(grid, "场次", nameof(CrossEventPlayerAppearanceRow.MatchName), 132);
        AddGridTextColumn(grid, "本方", nameof(CrossEventPlayerAppearanceRow.SideText), new DataGridLength(1, DataGridLengthUnitType.Star));
        AddGridTextColumn(grid, "对方", nameof(CrossEventPlayerAppearanceRow.OpponentText), new DataGridLength(1, DataGridLengthUnitType.Star));
        AddGridTextColumn(grid, "冲突说明", nameof(CrossEventPlayerAppearanceRow.ConflictSummary), new DataGridLength(1.2, DataGridLengthUnitType.Star));
        return grid;
    }

    private static DataGrid CreateReadOnlyDataGrid()
    {
        return new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single
        };
    }

    private static void AddGridTextColumn(DataGrid grid, string header, string propertyName, double width)
    {
        AddGridTextColumn(grid, header, propertyName, new DataGridLength(width));
    }

    private static void AddGridTextColumn(DataGrid grid, string header, string propertyName, DataGridLength width)
    {
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new System.Windows.Data.Binding(propertyName),
            Width = width
        });
    }

    private void ZoomOutCrossEventBoard_Click(object sender, RoutedEventArgs e)
    {
        SetCrossEventBoardZoom(_crossEventBoardZoom - CrossEventBoardZoomStep);
    }

    private void ResetCrossEventBoardZoom_Click(object sender, RoutedEventArgs e)
    {
        SetCrossEventBoardZoom(1.0);
    }

    private void ZoomInCrossEventBoard_Click(object sender, RoutedEventArgs e)
    {
        SetCrossEventBoardZoom(_crossEventBoardZoom + CrossEventBoardZoomStep);
    }

    private void OpenScheduleBoardWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_latestSchedule is null)
        {
            SetStatus("请先生成或打开赛程。", isError: true);
            return;
        }

        if (_scheduleBoardWindow is { IsVisible: true })
        {
            _scheduleBoardWindow.Activate();
            return;
        }

        _scheduleBoardWindowZoom = 1.0;
        _scheduleBoardWindowDayBox = new ComboBox { Width = 180 };
        _scheduleBoardWindowDayBox.SelectionChanged += ScheduleBoardWindowDayBox_SelectionChanged;
        _scheduleBoardWindowSummaryText = new TextBlock
        {
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
            TextWrapping = TextWrapping.Wrap
        };
        _scheduleBoardWindowGrid = new Grid
        {
            Margin = new Thickness(10),
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
            Owner = this,
            Content = BuildScheduleBoardWindowContent()
        };
        _scheduleBoardWindow.Closed += (_, _) =>
        {
            _scheduleBoardWindow = null;
            _scheduleBoardWindowDayBox = null;
            _scheduleBoardWindowSummaryText = null;
            _scheduleBoardWindowGrid = null;
        };
        RefreshScheduleBoardWindow();
        _scheduleBoardWindow.Show();
    }

    private UIElement BuildScheduleBoardWindowContent()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12)
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = "赛程安排窗口",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 16, 78))
        });
        titleStack.Children.Add(_scheduleBoardWindowSummaryText!);
        headerGrid.Children.Add(titleStack);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12, 0, 0, 0)
        };
        controls.Children.Add(new TextBlock
        {
            Text = "比赛日",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        controls.Children.Add(_scheduleBoardWindowDayBox!);
        controls.Children.Add(CreateCrossEventWindowButton("缩小", (_, _) => SetScheduleBoardWindowZoom(_scheduleBoardWindowZoom - CrossEventBoardZoomStep)));
        controls.Children.Add(CreateCrossEventWindowButton("100%", (_, _) => SetScheduleBoardWindowZoom(1.0)));
        controls.Children.Add(CreateCrossEventWindowButton("放大", (_, _) => SetScheduleBoardWindowZoom(_scheduleBoardWindowZoom + CrossEventBoardZoomStep)));
        Grid.SetColumn(controls, 1);
        headerGrid.Children.Add(controls);
        header.Child = headerGrid;
        root.Children.Add(header);

        var boardHost = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 252, 255)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(216, 224, 236)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 10, 0, 0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _scheduleBoardWindowGrid
            }
        };
        Grid.SetRow(boardHost, 1);
        root.Children.Add(boardHost);
        return root;
    }

    private void ScheduleBoardWindowDayBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshScheduleBoardWindow(_scheduleBoardWindowDayBox?.SelectedItem?.ToString());
    }

    private void SetScheduleBoardWindowZoom(double value)
    {
        _scheduleBoardWindowZoom = Math.Clamp(value, CrossEventBoardWindowMinZoom, CrossEventBoardMaxZoom);
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
            RenderScheduleBoard(_scheduleBoardWindowGrid, null, _scheduleBoardWindowZoom);
            return;
        }

        var dayLabels = ScheduleWorkflow.BuildBoardDays(_latestSchedule)
            .Select(day => day.DayLabel)
            .ToList();
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
        _scheduleBoardWindowDayBox.SelectionChanged += ScheduleBoardWindowDayBox_SelectionChanged;
        _scheduleBoardWindowSummaryText.Text = BuildScheduleBoardSummary(_latestSchedule, _scheduleBoardWindowZoom);
        RenderScheduleBoard(_scheduleBoardWindowGrid, selectedDay, _scheduleBoardWindowZoom);
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
        targetGrid.Children.Clear();
        targetGrid.RowDefinitions.Clear();
        targetGrid.ColumnDefinitions.Clear();
        if (_latestSchedule is null || string.IsNullOrWhiteSpace(dayLabel))
        {
            AddCrossEventEmptyText(targetGrid, "尚未选择比赛日。", zoom);
            return;
        }

        var days = ScheduleWorkflow.BuildBoardDays(_latestSchedule);
        var day = days.FirstOrDefault(item => string.Equals(item.DayLabel, dayLabel, StringComparison.Ordinal));
        if (day is null)
        {
            AddCrossEventEmptyText(targetGrid, "当前比赛日没有赛程。", zoom);
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

        var dayMatches = _latestSchedule.Matches
            .Where(match => string.Equals(match.DayLabel, day.DayLabel, StringComparison.Ordinal))
            .ToList();
        for (var slotIndex = 0; slotIndex < day.TimeSlots.Count; slotIndex++)
        {
            var slot = day.TimeSlots[slotIndex];
            AddCrossEventTimeCell(targetGrid, slot, slotIndex + 1, zoom);
            for (var courtIndex = 0; courtIndex < day.Courts.Count; courtIndex++)
            {
                var court = day.Courts[courtIndex];
                var cellMatches = dayMatches
                    .Where(match => string.Equals(match.Court, court, StringComparison.Ordinal)
                                    && match.StartTime == slot)
                    .OrderBy(match => match.Order)
                    .ToList();
                AddScheduleDropCell(targetGrid, day.DayLabel, slot, court, slotIndex + 1, courtIndex + 1, cellMatches, zoom);
            }
        }
    }

    private void AddScheduleDropCell(
        Grid targetGrid,
        string dayLabel,
        TimeOnly slot,
        string court,
        int row,
        int column,
        IReadOnlyList<ScheduledMatch> matches,
        double zoom)
    {
        var stack = new StackPanel();
        foreach (var match in matches)
        {
            stack.Children.Add(CreateScheduleMatchCard(match, zoom));
        }

        var border = new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(0, 0, 1, 1),
            MinHeight = ScaleCrossEvent(72, zoom),
            Padding = new Thickness(ScaleCrossEvent(6, zoom)),
            Tag = new ScheduleDropTarget(dayLabel, slot, court),
            AllowDrop = true,
            Child = stack
        };
        border.DragOver += ScheduleCell_DragOver;
        border.Drop += ScheduleCell_Drop;
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        targetGrid.Children.Add(border);
    }

    private Border CreateScheduleMatchCard(ScheduledMatch match, double zoom)
    {
        var isCompleted = IsScheduleMatchCompleted(match);
        var card = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(isCompleted
                ? System.Windows.Media.Color.FromRgb(241, 245, 249)
                : System.Windows.Media.Color.FromRgb(248, 251, 255)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(199, 210, 228)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(ScaleCrossEvent(8, zoom)),
            Margin = new Thickness(0, 0, 0, ScaleCrossEvent(6, zoom)),
            Tag = match,
            Cursor = isCompleted ? Cursors.Arrow : Cursors.Hand
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"{match.GroupName} · {match.MatchName}",
            FontSize = ScaleCrossEventFont(13, zoom),
            FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 20, 95)),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{match.TimeRange} · {match.Phase}" + (isCompleted ? " · 已完成" : ""),
            Margin = new Thickness(0, ScaleCrossEvent(3, zoom), 0, 0),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
            FontSize = ScaleCrossEventFont(12, zoom)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{match.SideA}  vs  {match.SideB}",
            Margin = new Thickness(0, ScaleCrossEvent(3, zoom), 0, 0),
            TextWrapping = TextWrapping.Wrap,
            FontSize = ScaleCrossEventFont(12, zoom)
        });
        card.Child = stack;
        card.MouseMove += ScheduleMatchCard_MouseMove;
        return card;
    }

    private bool IsScheduleMatchCompleted(ScheduledMatch match)
    {
        return _progressState?.Results.ContainsKey(match.MatchName) == true;
    }

    private void ScheduleMatchCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Border { Tag: ScheduledMatch match } || IsScheduleMatchCompleted(match) || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        DragDrop.DoDragDrop((DependencyObject)sender, $"{ScheduleDragPrefix}{match.MatchName}", DragDropEffects.Move);
    }

    private void ScheduleCell_DragOver(object sender, DragEventArgs e)
    {
        var text = e.Data.GetData(DataFormats.StringFormat) as string;
        e.Effects = !string.IsNullOrWhiteSpace(text) && text.StartsWith(ScheduleDragPrefix, StringComparison.Ordinal)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ScheduleCell_Drop(object sender, DragEventArgs e)
    {
        if (_latestSchedule is null
            || sender is not Border { Tag: ScheduleDropTarget target }
            || e.Data.GetData(DataFormats.StringFormat) is not string text
            || !text.StartsWith(ScheduleDragPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var matchName = text[ScheduleDragPrefix.Length..];
        try
        {
            _latestSchedule = ScheduleWorkflow.MoveScheduledMatch(
                _latestSchedule,
                matchName,
                target.DayLabel,
                target.StartTime,
                target.Court,
                _progressState?.Results.Keys.ToHashSet(StringComparer.Ordinal));
            if (_progressState is not null)
            {
                _progressState = ReplaceProgressSchedule(_progressState, _latestSchedule);
                UpdateProgressDisplay();
            }

            ScheduleGrid.ItemsSource = ToScheduleRows(_latestSchedule);
            ScheduleSummaryText.Text = $"已调整 {_latestSchedule.Matches.Count} 场赛程，预计 {_latestSchedule.DayCount} 个比赛日。";
            RefreshScheduleBoardWindow(target.DayLabel);
            SetStatus("赛程安排已调整；后续导出会使用调整后的时间和场地。");
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
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

    private void OpenCrossEventBoardWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_crossEventScheduleBoard is null)
        {
            SetStatus("请先加载多项目赛程。", isError: true);
            return;
        }

        if (_crossEventBoardWindow is { IsVisible: true })
        {
            _crossEventBoardWindow.Activate();
            return;
        }

        _crossEventBoardWindowZoom = Math.Max(_crossEventBoardZoom, 0.85);
        _crossEventBoardWindowDayBox = new ComboBox { Width = 180 };
        _crossEventBoardWindowDayBox.SelectionChanged += CrossEventBoardWindowDayBox_SelectionChanged;
        _crossEventBoardWindowSummaryText = new TextBlock
        {
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
            TextWrapping = TextWrapping.Wrap
        };
        _crossEventBoardWindowGrid = new Grid
        {
            Margin = new Thickness(10),
            MinWidth = 980,
            MinHeight = 560
        };

        var root = BuildCrossEventBoardWindowContent();
        _crossEventBoardWindow = new Window
        {
            Owner = this,
            Title = "多项目赛程窗口",
            Width = Math.Min(SystemParameters.WorkArea.Width * 0.94, 1420),
            Height = Math.Min(SystemParameters.WorkArea.Height * 0.9, 880),
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
        };
        RefreshCrossEventBoardWindow(GetSelectedCrossEventDayLabel());
        _crossEventBoardWindow.Show();
    }

    private UIElement BuildCrossEventBoardWindowContent()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12)
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = "多项目赛程窗口",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 16, 78))
        });
        titleStack.Children.Add(_crossEventBoardWindowSummaryText!);
        headerGrid.Children.Add(titleStack);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12, 0, 0, 0)
        };
        controls.Children.Add(new TextBlock
        {
            Text = "比赛日",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        controls.Children.Add(_crossEventBoardWindowDayBox!);
        controls.Children.Add(CreateCrossEventWindowButton("缩小", (_, _) => SetCrossEventBoardWindowZoom(_crossEventBoardWindowZoom - CrossEventBoardZoomStep)));
        controls.Children.Add(CreateCrossEventWindowButton("100%", (_, _) => SetCrossEventBoardWindowZoom(1.0)));
        controls.Children.Add(CreateCrossEventWindowButton("放大", (_, _) => SetCrossEventBoardWindowZoom(_crossEventBoardWindowZoom + CrossEventBoardZoomStep)));
        Grid.SetColumn(controls, 1);
        headerGrid.Children.Add(controls);
        header.Child = headerGrid;
        root.Children.Add(header);

        var boardHost = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 252, 255)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(216, 224, 236)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 10, 0, 0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _crossEventBoardWindowGrid
            }
        };
        Grid.SetRow(boardHost, 1);
        root.Children.Add(boardHost);
        return root;
    }

    private static Button CreateCrossEventWindowButton(string text, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            Width = 64,
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0)
        };
        button.Click += handler;
        return button;
    }

    private void CrossEventBoardWindowDayBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshCrossEventBoardWindow(_crossEventBoardWindowDayBox?.SelectedItem?.ToString());
    }

    private void SetCrossEventBoardWindowZoom(double value)
    {
        _crossEventBoardWindowZoom = Math.Clamp(value, CrossEventBoardWindowMinZoom, CrossEventBoardMaxZoom);
        RefreshCrossEventBoardWindow(_crossEventBoardWindowDayBox?.SelectedItem?.ToString());
    }

    private void RefreshCrossEventBoardWindow(string? preferredDayLabel = null)
    {
        if (_crossEventScheduleBoard is null
            || _crossEventBoardWindowDayBox is null
            || _crossEventBoardWindowSummaryText is null
            || _crossEventBoardWindowGrid is null)
        {
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
        _crossEventBoardWindowDayBox.SelectionChanged += CrossEventBoardWindowDayBox_SelectionChanged;
        _crossEventBoardWindowSummaryText.Text = BuildCrossEventBoardSummary(_crossEventScheduleBoard, _crossEventBoardWindowZoom);
        RenderCrossEventScheduleBoard(_crossEventBoardWindowGrid, selectedDay, _crossEventBoardWindowZoom);
    }

    private void SetCrossEventBoardZoom(double value)
    {
        var next = Math.Clamp(value, CrossEventBoardMinZoom, CrossEventBoardMaxZoom);
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
                (index + 1).ToString(),
                entry.PlayerName,
                entry.EventCount.ToString(),
                string.Join("、", entry.EventNames),
                entry.MatchCount.ToString(),
                entry.PendingMatchCount.ToString(),
                entry.SevereIssueCount.ToString(),
                entry.WarningIssueCount.ToString(),
                entry.ShortestRestMinutes.HasValue ? $"{entry.ShortestRestMinutes.Value} 分钟" : "-",
                entry.NextMatchText))
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

    private static IReadOnlyList<CrossEventPlayerAppearanceRow> BuildCrossEventPlayerAppearanceRows(
        IReadOnlyList<CrossEventPlayerScheduleAppearance> appearances)
    {
        return appearances
            .Select(appearance => new CrossEventPlayerAppearanceRow(
                appearance.Status,
                appearance.DayLabel,
                appearance.TimeRange,
                appearance.Court,
                appearance.EventName,
                appearance.Phase,
                appearance.MatchName,
                appearance.Side,
                appearance.SideText,
                appearance.OpponentText,
                string.IsNullOrWhiteSpace(appearance.ConflictSummary) ? "-" : appearance.ConflictSummary))
            .ToList();
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
        return Math.Round(value * zoom);
    }

    private static double ScaleCrossEventFont(double value, double zoom)
    {
        return Math.Round(value * Math.Clamp(zoom, 0.82, 1.35), 1);
    }

    private void OpenProgress_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "深大羽协赛事存档 (*.szbd)|*.szbd",
            DefaultExt = ".szbd",
            CheckFileExists = true,
            Title = "打开赛事存档"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ApplyProgressState(_progressWorkflow.Open(dialog.FileName), dialog.FileName);
            SetStatus(
                $"已打开赛事存档：累计完成 {_progressState!.Results.Count} 场，"
                + $"待决 {_progressState.RemainingMatchCount} 场。");
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void ImportMatchRecordsIntoProgress(IReadOnlyList<string> filePaths)
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
                var message = TournamentProgressWorkflow.BuildImportConfirmation(preview, nextDayLabel);
                if (MessageBox.Show(
                        this,
                        message,
                        "更新赛事存档",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    SetStatus("已取消更新赛事存档。", StatusKind.Warning);
                    return;
                }
            }

            var outcome = _progressWorkflow.Import(
                _progressFilePath,
                filePaths,
                allowCorrections: preview.Corrections.Count > 0);
            _progressState = outcome.State;
            _latestSchedule = outcome.State.Snapshot.Schedule;
            UpdateProgressDisplay();

            nextDayLabel = TournamentProgressWorkflow.GetNextMatchRecordDayLabel(outcome.State);
            if (string.IsNullOrWhiteSpace(nextDayLabel))
            {
                SetStatus(
                    $"赛事存档已更新，累计完成 {outcome.State.Results.Count} 场，"
                    + $"待决 {outcome.State.RemainingMatchCount} 场；当前没有下一比赛日需要导出。");
                return;
            }

            var outputDirectory = PickFolderPath("选择下一比赛日材料包保存文件夹");
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                SetStatus(
                    $"赛事存档已更新，累计完成 {outcome.State.Results.Count} 场，"
                    + $"待决 {outcome.State.RemainingMatchCount} 场；已取消导出下一比赛日材料包。",
                    StatusKind.Warning);
                return;
            }

            var package = _progressWorkflow.ExportNextDayPackage(
                outcome.State,
                outputDirectory,
                includePrintablePdf: true,
                GetDrawVisualOptions(ExportFormat.A4Pdf));
            SetStatus(
                $"赛事存档已更新：新增 {preview.NewResultCount} 场结果，"
                + $"累计完成 {outcome.State.Results.Count} 场，待决 {outcome.State.RemainingMatchCount} 场；"
                + $"{package.DayLabel} 材料包已导出到：{package.OutputDirectory}（共 {package.OutputPaths.Count} 个文件）。");
        }
        catch (Exception ex) when (ex is TournamentProgressException or ExcelImportException or IOException or InvalidOperationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void CompetitionModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateEventKindForMode();
            UpdateKnockoutGoalVisibility();
        }
    }

    private void KnockoutGoalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdatePlacementPlayoffVisibility();
            UpdateScheduleTimingSplitVisibility();
        }
    }

    private void ScheduleVenueBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyScheduleCourtPreset();
        }
    }

    private void ExportFormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateExportOptionsVisibility();
        }
    }

    private void GroupCountBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateKnockoutGoalVisibility();
        }
    }

    private void UpdateEventKindForMode()
    {
        var mode = GetCompetitionMode();
        if (mode is CompetitionMode.TeamKnockout or CompetitionMode.TeamRoundRobin)
        {
            EventKindBox.SelectedIndex = 2;
            EventKindBox.IsEnabled = false;
        }
        else
        {
            EventKindBox.IsEnabled = true;
            if (EventKindBox.SelectedIndex == 2)
            {
                EventKindBox.SelectedIndex = 0;
            }
        }
    }

    private bool TryLoadParticipants()
    {
        try
        {
            var detectedEventKind = TryApplyDetectedEventKind();
            ApplyImportResult(_drawWorkflow.LoadParticipants(InputPathBox.Text, GetEventKind()));
            ClearProgressReference();
            SummaryText.Text = $"已导入 {_participants.Count} 个参赛单位";
            PreviewStateText.Text = "待预览";
            UpdatePreviewBadges();
            ShowImportWarningsIfNeeded(_importWarnings);
            SetStatus(detectedEventKind.HasValue
                ? $"检测到名单类型为{GetEventKindDisplay(detectedEventKind.Value)}，已自动切换并导入成功。"
                : "名单导入成功。",
                _importWarnings.Count > 0 ? StatusKind.Warning : StatusKind.Normal,
                _importWarnings);
            return true;
        }
        catch (Exception ex) when (ex is ExcelImportException or DrawValidationException or IOException)
        {
            ResetLoadedData();
            SetStatus(ex.Message, isError: true);
            return false;
        }
    }

    private bool TryGenerate()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(InputPathBox.Text))
            {
                throw new DrawValidationException("请先选择参赛名单 Excel。");
            }

            if ((_participants.Count == 0 || !IsCurrentInputLoaded()) && !TryLoadParticipants())
            {
                return false;
            }

            var detectedEventKind = TryApplyDetectedEventKind();
            if (!int.TryParse(GroupCountBox.Text.Trim(), out var groupCount))
            {
                throw new DrawValidationException("小组数必须是数字。");
            }

            var request = new DrawWorkflowRequest(
                InputPathBox.Text,
                GetCompetitionMode(),
                GetEventKind(),
                groupCount,
                SeedBox.Text,
                GetKnockoutGoal(),
                GetPlacementPlayoff());
            ApplyWorkflowResult(_drawWorkflow.Generate(request));
            ClearProgressReference();
            ClearSchedulePreview();
            UpdateScheduleTimingSplitVisibility();

            GroupsGrid.ItemsSource = ToRows(_latestResult!.Groups);
            RoundOneGrid.ItemsSource = ToRows(_latestResult.RoundOneGroups);
            ByeGrid.ItemsSource = ToRows(_latestResult.ByeGroups);
            SummaryText.Text = $"已生成 {_latestResult.Groups.Count} 个小组";
            ParticipantCountText.Text = _latestResult.Audit.ParticipantCount.ToString();
            EventKindStatText.Text = GetEventKindDisplay(request.EventKind);
            GroupCountStatText.Text = groupCount.ToString();
            PreviewStateText.Text = "已预览";
            UpdatePreviewBadges(_latestResult);
            UpdateExportOptionsVisibility();
            SetStatus(detectedEventKind.HasValue
                ? $"检测到名单类型为{GetEventKindDisplay(detectedEventKind.Value)}，已自动切换并生成预览。"
                : "抽签预览已生成，可继续切换轮次或导出结果。",
                _importWarnings.Count > 0 ? StatusKind.Warning : StatusKind.Normal,
                _importWarnings);
            return true;
        }
        catch (Exception ex) when (ex is ExcelImportException or IOException)
        {
            ResetLoadedData();
            SetStatus(ex.Message, isError: true);
            return false;
        }
        catch (Exception ex) when (ex is DrawValidationException or InvalidOperationException)
        {
            SetStatus(ex.Message, isError: true);
            return false;
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
            ClearProgressReference();
            ScheduleGrid.ItemsSource = ToScheduleRows(_latestSchedule);
            RefreshScheduleBoardWindow();
            ScheduleSummaryText.Text = _latestSchedule.IsComplete
                ? $"已生成 {_latestSchedule.Matches.Count} 场，预计 {_latestSchedule.DayCount} 个比赛日"
                : $"已安排 {_latestSchedule.Matches.Count} 场，未安排 {_latestSchedule.UnscheduledMatches.Count} 场，共 {_latestSchedule.TotalMatchCount} 场";
            ScheduleCapacityText.Text = ScheduleWorkflow.BuildScheduleCapacityText(settings);
            if (_latestSchedule.IsComplete)
            {
                SetStatus($"赛程预览已生成：共 {_latestSchedule.Matches.Count} 场，预计 {_latestSchedule.DayCount} 个比赛日。");
            }
            else
            {
                SetStatus(
                    $"赛程资源不足：已安排 {_latestSchedule.Matches.Count} 场，仍有 {_latestSchedule.UnscheduledMatches.Count} 场无法安排。预览已保留，未安排行已标红；请增加比赛日、场地、时间段，或提高对应阶段单名选手每日最多场次。",
                    StatusKind.Warning);
            }
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
        var useTimingSplit = ShouldShowScheduleTimingSplit();
        var matchMinutes = ParsePositiveScheduleInt(
            ScheduleMatchMinutesBox.Text,
            useTimingSplit ? "分界线后单场比赛耗时" : "单场比赛耗时");
        var maxMatchesPerDay = ParsePositiveScheduleInt(
            GetSelectedComboBoxText(MaxMatchesPerDayBox),
            useTimingSplit ? "分界线后单名选手每日最多场次" : "单名选手每日最多场次");
        var boundaryEntrants = useTimingSplit ? GetSelectedComboBoxTagInt(ScheduleTimingBoundaryBox) : 0;
        int? beforeBoundaryMatchMinutes = null;
        int? beforeBoundaryMaxMatchesPerDay = null;
        if (boundaryEntrants > 0)
        {
            beforeBoundaryMatchMinutes = ParsePositiveScheduleInt(BeforeBoundaryMatchMinutesBox.Text, "分界线前单场比赛耗时");
            beforeBoundaryMaxMatchesPerDay = ParsePositiveScheduleInt(
                GetSelectedComboBoxText(BeforeBoundaryMaxMatchesPerDayBox),
                "分界线前单名选手每日最多场次");
        }

        var days = _scheduleDays
            .OrderBy(day => day.DateValue)
            .Select(day => new ScheduleDayWorkflowRequest(
                day.DateValue,
                day.StartTime,
                day.EndTime,
                day.Venue,
                string.Join("，", day.Courts)))
            .ToList();

        return ScheduleWorkflow.BuildSettings(
            days,
            matchMinutes,
            maxMatchesPerDay,
            boundaryEntrants > 0 ? boundaryEntrants : null,
            beforeBoundaryMatchMinutes,
            beforeBoundaryMaxMatchesPerDay);
    }

    private void AddCurrentScheduleDay()
    {
        var date = ScheduleDatePicker.SelectedDate.HasValue
            ? DateOnly.FromDateTime(ScheduleDatePicker.SelectedDate.Value)
            : DateOnly.FromDateTime(DateTime.Today);
        var start = ParseScheduleTime(GetSelectedComboBoxText(ScheduleStartBox), "开始时间");
        var end = ParseScheduleTime(GetSelectedComboBoxText(ScheduleEndBox), "结束时间");
        if (end <= start)
        {
            throw new DrawValidationException("赛程结束时间必须晚于开始时间。");
        }

        var courts = ScheduleWorkflow.ParseCourts(ScheduleCourtsBox.Text);
        var venue = ScheduleVenueBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? "自定义"
            : "自定义";
        var existing = _scheduleDays.FirstOrDefault(day => day.DateValue == date);
        if (existing is not null)
        {
            _scheduleDays.Remove(existing);
        }

        _scheduleDays.Add(new ScheduleDayRow(
            date,
            start,
            end,
            venue,
            courts));
    }

    private static string GetSelectedComboBoxText(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? string.Empty
            : comboBox.Text;
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

    private static TimeOnly ParseScheduleTime(string value, string name)
    {
        if (!TimeOnly.TryParse(value.Trim(), out var time))
        {
            throw new DrawValidationException($"{name}格式不正确，请使用 14:00 这样的格式。");
        }

        return time;
    }

    private static int ParsePositiveScheduleInt(string value, string name)
    {
        if (!int.TryParse(value.Trim(), out var number) || number <= 0)
        {
            throw new DrawValidationException($"{name}必须是大于 0 的整数。");
        }

        return number;
    }

    private static int ParseNonNegativeScheduleInt(string value, string name)
    {
        if (!int.TryParse(value.Trim(), out var number) || number < 0)
        {
            throw new DrawValidationException($"{name}必须是大于或等于 0 的整数。");
        }

        return number;
    }

    private static string BuildScheduleCapacityText(ScheduleSettings settings)
    {
        string BuildCapacity(int matchMinutes)
        {
            var capacity = settings.Days
                .Select(day =>
                {
                    var minutes = (day.DayEnd - day.DayStart).TotalMinutes;
                    var slots = Math.Max(0, (int)Math.Floor(minutes / matchMinutes));
                    return $"{day.DayLabel} {day.Courts.Count}片/{slots * day.Courts.Count}场";
                });

            return string.Join("；", capacity);
        }

        if (!settings.HasKnockoutTimingSplit)
        {
            return $"每日上限{settings.MaxMatchesPerEntrantPerDay}场；" + BuildCapacity(settings.MatchMinutes);
        }

        return $"分界线前每日上限{settings.BeforeBoundaryTiming!.MaxMatchesPerEntrantPerDay}场、每场{settings.BeforeBoundaryTiming.MatchMinutes}分钟：{BuildCapacity(settings.BeforeBoundaryTiming.MatchMinutes)}；"
            + $"分界线后每日上限{settings.MaxMatchesPerEntrantPerDay}场、每场{settings.MatchMinutes}分钟：{BuildCapacity(settings.MatchMinutes)}";
    }

    private void ClearSchedulePreview()
    {
        _latestSchedule = null;
        ScheduleGrid.ItemsSource = null;
        ScheduleSummaryText.Text = "尚未生成赛程。";
        ScheduleCapacityText.Text = "待选择日期、时间段与场地";
        RefreshScheduleBoardWindow();
        UpdateScheduleTimingSplitVisibility();
    }

    private bool IsCurrentInputLoaded()
    {
        return !string.IsNullOrWhiteSpace(_loadedInputPath)
            && string.Equals(_loadedInputPath, InputPathBox.Text, StringComparison.OrdinalIgnoreCase);
    }

    private void ResetLoadedData()
    {
        _participants = Array.Empty<DrawParticipant>();
        _importWarnings = Array.Empty<ParticipantImportWarning>();
        _loadedInputPath = null;
        _latestResult = null;
        _latestWorkflowResult = null;
        ClearProgressReference();
        GroupsGrid.ItemsSource = null;
        RoundOneGrid.ItemsSource = null;
        ByeGrid.ItemsSource = null;
        ClearSchedulePreview();
        SummaryText.Text = "尚未导入名单。";
        PreviewStateText.Text = "待导入";
        UpdatePreviewBadges();
    }

    private void ApplyImportResult(ParticipantLoadResult importResult)
    {
        _participants = importResult.Participants;
        _importWarnings = importResult.ImportWarnings;
        _loadedInputPath = InputPathBox.Text;
        _latestWorkflowResult = null;
    }

    private void ApplyWorkflowResult(DrawWorkflowResult workflowResult)
    {
        _latestWorkflowResult = workflowResult;
        _latestResult = workflowResult.Result;
        _participants = workflowResult.Participants;
        _importWarnings = workflowResult.ImportWarnings;
        _loadedInputPath = InputPathBox.Text;
    }

    private void ApplyProgressState(TournamentProgressState state, string filePath)
    {
        _progressState = state;
        _progressFilePath = filePath;
        InputPathBox.Text = state.Snapshot.SourceInputPath ?? state.Snapshot.EventName;
        ApplyWorkflowResult(TournamentProgressWorkflow.BuildDrawWorkflowResult(state));
        _loadedInputPath = state.Snapshot.SourceInputPath;
        _latestSchedule = state.Snapshot.Schedule;

        SelectCompetitionMode(state.Snapshot.DrawResult.Settings.CompetitionMode);
        UpdateEventKindForMode();
        SelectEventKind(state.Snapshot.DrawResult.Settings.EventKind);
        GroupCountBox.Text = state.Snapshot.DrawResult.Settings.GroupCount.ToString();
        SeedBox.Text = state.Snapshot.DrawResult.Settings.RandomSeed;
        UpdateKnockoutGoalVisibility();
        SelectKnockoutGoal(state.Snapshot.DrawResult.Settings.KnockoutGoal);
        UpdatePlacementPlayoffVisibility();
        SelectPlacementPlayoff(state.Snapshot.DrawResult.Settings.PlacementPlayoff);
        UpdateScheduleTimingSplitVisibility();
        ApplyStoredScheduleSettings(state.Snapshot.Schedule.Settings);

        GroupsGrid.ItemsSource = ToRows(state.Snapshot.DrawResult.Groups);
        RoundOneGrid.ItemsSource = ToRows(state.Snapshot.DrawResult.RoundOneGroups);
        ByeGrid.ItemsSource = ToRows(state.Snapshot.DrawResult.ByeGroups);
        ScheduleGrid.ItemsSource = ToScheduleRows(state.Snapshot.Schedule);
        RefreshScheduleBoardWindow();
        SummaryText.Text = $"已从赛事存档恢复 {state.Snapshot.DrawResult.Groups.Count} 个小组";
        ParticipantCountText.Text = state.Snapshot.DrawResult.Audit.ParticipantCount.ToString();
        EventKindStatText.Text = GetEventKindDisplay(state.Snapshot.DrawResult.Settings.EventKind);
        GroupCountStatText.Text = state.Snapshot.DrawResult.Audit.GroupCount.ToString();
        PreviewStateText.Text = "存档已载入";
        ScheduleSummaryText.Text =
            $"已恢复 {state.Snapshot.Schedule.Matches.Count} 场赛程，累计完成 {state.Results.Count} 场";
        ScheduleCapacityText.Text = ScheduleWorkflow.BuildScheduleCapacityText(state.Snapshot.Schedule.Settings);
        UpdatePreviewBadges(state.Snapshot.DrawResult);
        UpdateExportOptionsVisibility();
        UpdateProgressDisplay();
    }

    private void ApplyStoredScheduleSettings(ScheduleSettings settings)
    {
        _scheduleDays.Clear();
        foreach (var day in settings.Days)
        {
            _scheduleDays.Add(new ScheduleDayRow(
                day.Date,
                day.DayStart,
                day.DayEnd,
                "赛事存档",
                day.Courts));
        }

        ScheduleMatchMinutesBox.Text = settings.MatchMinutes.ToString();
        SelectComboBoxText(MaxMatchesPerDayBox, settings.MaxMatchesPerEntrantPerDay.ToString());
        if (settings.HasKnockoutTimingSplit)
        {
            SelectComboBoxTag(
                ScheduleTimingBoundaryBox,
                settings.KnockoutTimingBoundaryEntrants!.Value.ToString());
            BeforeBoundaryMatchMinutesBox.Text = settings.BeforeBoundaryTiming!.MatchMinutes.ToString();
            SelectComboBoxText(
                BeforeBoundaryMaxMatchesPerDayBox,
                settings.BeforeBoundaryTiming.MaxMatchesPerEntrantPerDay.ToString());
        }
        else
        {
            SelectComboBoxTag(ScheduleTimingBoundaryBox, "0");
        }
    }

    private static void SelectComboBoxText(ComboBox comboBox, string value)
    {
        foreach (ComboBoxItem item in comboBox.Items)
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
        foreach (ComboBoxItem item in comboBox.Items)
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

    private void ShowImportWarningsIfNeeded(IReadOnlyList<ParticipantImportWarning> warnings)
    {
        var duplicateWarnings = warnings
            .Where(warning => warning.Kind == ParticipantImportWarningKind.DuplicatePlayerName)
            .ToList();
        var unrankedSeedWarnings = warnings
            .Where(warning => warning.Kind == ParticipantImportWarningKind.UnrankedSeed)
            .ToList();
        var sections = new List<string>();

        if (duplicateWarnings.Count > 0)
        {
            sections.Add("发现同名选手，请优先通过“学号”或“搭档学号”确认是否为不同的人：\n"
                + FormatWarningList(duplicateWarnings));
        }

        if (unrankedSeedWarnings.Count >= 2)
        {
            sections.Add("发现多个标记为种子但未填写种子序号的参赛单位：\n" + FormatWarningList(unrankedSeedWarnings));
        }

        if (sections.Count == 0)
        {
            return;
        }

        MessageBox.Show(
            this,
            string.Join("\n\n", sections) + "\n\n这些提醒不会阻止预览抽签，你可以继续手动点击预览。",
            "名单提醒",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ShowParticipantRosterWindow()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = true,
            FrozenColumnCount = 1,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            ItemsSource = BuildParticipantRosterRows(_participants),
            Margin = new Thickness(16)
        };

        AddRosterColumn(grid, "序号", nameof(ParticipantRosterRow.Order), 70);
        AddRosterColumn(grid, "姓名", nameof(ParticipantRosterRow.PrimaryName), 130);
        AddRosterColumn(grid, "学号", nameof(ParticipantRosterRow.PrimaryStudentId), 130);
        AddRosterColumn(grid, "学院/学部", nameof(ParticipantRosterRow.TeamName), 180);
        AddRosterColumn(grid, "搭档姓名", nameof(ParticipantRosterRow.PartnerName), 130);
        AddRosterColumn(grid, "搭档学号", nameof(ParticipantRosterRow.PartnerStudentId), 130);
        AddRosterColumn(grid, "搭档学院/学部", nameof(ParticipantRosterRow.PartnerTeamName), 180);
        AddRosterColumn(grid, "是否种子", nameof(ParticipantRosterRow.SeedFlag), 100);
        AddRosterColumn(grid, "种子序号", nameof(ParticipantRosterRow.SeedRank), 100);
        AddRosterColumn(grid, "备注", nameof(ParticipantRosterRow.Note), 260);

        var window = new Window
        {
            Title = "参赛选手/队伍信息",
            Owner = this,
            Width = 1120,
            Height = 640,
            MinWidth = 760,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"参赛选手/队伍信息 · {_participants.Count} 个参赛单位",
                        FontSize = 22,
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(18, 18, 18, 0)
                    },
                    grid
                }
            }
        };
        DockPanel.SetDock((UIElement)((DockPanel)window.Content).Children[0], Dock.Top);

        window.ShowDialog();
    }

    private static void AddRosterColumn(DataGrid grid, string header, string bindingPath, double width)
    {
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(bindingPath),
            Width = width
        });
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

    private static string FormatWarningList(IReadOnlyList<ParticipantImportWarning> warnings)
    {
        return string.Join("\n", warnings.Select((warning, index) => $"{index + 1}. {warning.Detail}"));
    }

    private CompetitionMode GetCompetitionMode()
    {
        return Enum.Parse<CompetitionMode>(GetSelectedTag(CompetitionModeBox));
    }

    private EventKind GetEventKind()
    {
        return Enum.Parse<EventKind>(GetSelectedTag(EventKindBox));
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

        return IsPowerOfTwo(groupCount)
            ? Enum.Parse<KnockoutGoal>(GetSelectedTag(KnockoutGoalBox))
            : KnockoutGoal.OneQualifierPerGroup;
    }

    private PlacementPlayoff GetPlacementPlayoff()
    {
        return GetCompetitionMode() is CompetitionMode.SinglesKnockout or CompetitionMode.TeamKnockout
            && GetKnockoutGoal() == KnockoutGoal.Champion
            ? Enum.Parse<PlacementPlayoff>(GetSelectedTag(PlacementPlayoffBox))
            : PlacementPlayoff.None;
    }

    private EventKind? TryApplyDetectedEventKind()
    {
        if (string.IsNullOrWhiteSpace(InputPathBox.Text))
        {
            return null;
        }

        var originalMode = GetCompetitionMode();
        var originalEventKind = GetEventKind();
        var detectedEventKind = _drawWorkflow.DetectEventKind(InputPathBox.Text, originalEventKind);

        if (detectedEventKind == EventKind.Team)
        {
            SelectCompetitionMode(originalMode switch
            {
                CompetitionMode.SinglesRoundRobin => CompetitionMode.TeamRoundRobin,
                CompetitionMode.TeamRoundRobin => CompetitionMode.TeamRoundRobin,
                _ => CompetitionMode.TeamKnockout
            });
            UpdateEventKindForMode();
            UpdateKnockoutGoalVisibility();
        }
        else
        {
            SelectCompetitionMode(originalMode switch
            {
                CompetitionMode.TeamRoundRobin => CompetitionMode.SinglesRoundRobin,
                CompetitionMode.SinglesRoundRobin => CompetitionMode.SinglesRoundRobin,
                _ => CompetitionMode.SinglesKnockout
            });
            UpdateEventKindForMode();
            UpdateKnockoutGoalVisibility();
            SelectEventKind(detectedEventKind);
        }

        return originalMode != GetCompetitionMode() || originalEventKind != GetEventKind()
            ? detectedEventKind
            : null;
    }

    private void SelectCompetitionMode(CompetitionMode competitionMode)
    {
        foreach (ComboBoxItem item in CompetitionModeBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), competitionMode.ToString(), StringComparison.Ordinal))
            {
                CompetitionModeBox.SelectedItem = item;
                return;
            }
        }

        throw new InvalidOperationException("缺少比赛模式选项配置。");
    }

    private void SelectEventKind(EventKind eventKind)
    {
        foreach (ComboBoxItem item in EventKindBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), eventKind.ToString(), StringComparison.Ordinal))
            {
                EventKindBox.SelectedItem = item;
                return;
            }
        }

        throw new InvalidOperationException("缺少项目类型选项配置。");
    }

    private static string GetSelectedTag(ComboBox comboBox)
    {
        return ((ComboBoxItem)comboBox.SelectedItem).Tag?.ToString()
            ?? throw new InvalidOperationException("缺少选项配置。");
    }

    private static ObservableCollection<ResultRow> ToRows(IReadOnlyList<DrawGroup> groups)
    {
        var rows = new ObservableCollection<ResultRow>();

        foreach (var group in groups)
        {
            for (var i = 0; i < group.Participants.Count; i++)
            {
                var participant = group.Participants[i];
                rows.Add(new ResultRow(
                    $"第 {group.Number} 组",
                    i + 1,
                    participant.DisplayName,
                    participant.IsSeed ? participant.SeedRank?.ToString() ?? "是" : ""));
            }
        }

        return rows;
    }

    private static ObservableCollection<ScheduleRow> ToScheduleRows(SchedulePlan plan)
    {
        var rows = new ObservableCollection<ScheduleRow>();

        foreach (var match in plan.Matches)
        {
            rows.Add(new ScheduleRow(
                match.Order,
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
                false,
                ""));
        }

        foreach (var match in plan.UnscheduledMatches)
        {
            rows.Add(new ScheduleRow(
                match.Order,
                "未安排",
                "未安排",
                "未安排",
                "未安排",
                match.GroupName,
                match.Phase,
                match.MatchName,
                match.SideA,
                match.SideB,
                string.IsNullOrWhiteSpace(match.Note) ? match.Reason : $"{match.Note}；{match.Reason}",
                true,
                match.Reason));
        }

        return rows;
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
            _ => BuildAllPresetCourts()
        };
        ScheduleCourtsBox.Text = string.Join("，", courts);
    }

    private static IReadOnlyList<string> BuildAllPresetCourts()
    {
        return BuildYuehaiEastCourts()
            .Concat(BuildZhikuaiCourts())
            .Concat(BuildZhichangCourts())
            .ToList();
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

    private static string GenerateSeed()
    {
        return DrawWorkflow.GenerateSeed();
    }

    private bool ConfirmMatchRecordWarnings(MatchRecordImportResult importResult, string nextDayLabel)
    {
        var message = ScheduleWorkflow.BuildMatchRecordImportWarning(importResult, nextDayLabel);
        var result = MessageBox.Show(
            this,
            message,
            "赛程记录表提醒",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    private void RefreshCrossEventScheduleBoard(string? preferredDayLabel = null)
    {
        if (_crossEventScheduleBoard is null)
        {
            CrossEventBoardSummaryText.Text = "尚未加载多项目赛程。请先在左侧选择至少两个赛事存档。";
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
    }

    private static string BuildCrossEventBoardSummary(
        CrossEventScheduleBoard board,
        double zoom,
        string changedText = "",
        bool includeZoom = true)
    {
        var zoomText = includeZoom ? $"；缩放 {Math.Round(zoom * 100)}%" : "";
        return $"项目 {board.Sources.Count}，场次 {board.Items.Count}，兼项 {board.MultiEventPlayerCount}；"
               + $"严重 {board.Report.SevereCount}，间隔 {board.Report.WarningCount}，同日 {board.Report.NoticeCount}，"
               + $"冲突卡 {board.BlockingConflictItemCount}{zoomText}{changedText}";
    }

    private void RenderCrossEventScheduleBoard(string? dayLabel)
    {
        RenderCrossEventScheduleBoard(CrossEventScheduleBoardGrid, dayLabel, _crossEventBoardZoom);
    }

    private void RenderCrossEventScheduleBoard(Grid targetGrid, string? dayLabel, double zoom)
    {
        targetGrid.Children.Clear();
        targetGrid.RowDefinitions.Clear();
        targetGrid.ColumnDefinitions.Clear();
        if (_crossEventScheduleBoard is null || string.IsNullOrWhiteSpace(dayLabel))
        {
            AddCrossEventEmptyText(targetGrid, "尚未选择比赛日。", zoom);
            return;
        }

        var day = _crossEventScheduleBoard.Days.FirstOrDefault(item => string.Equals(item.DayLabel, dayLabel, StringComparison.Ordinal));
        if (day is null)
        {
            AddCrossEventEmptyText(targetGrid, "当前比赛日没有赛程。", zoom);
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

        var dayItems = _crossEventScheduleBoard.Items
            .Where(item => string.Equals(item.DayLabel, day.DayLabel, StringComparison.Ordinal))
            .ToList();
        for (var slotIndex = 0; slotIndex < day.TimeSlots.Count; slotIndex++)
        {
            var slot = day.TimeSlots[slotIndex];
            AddCrossEventTimeCell(targetGrid, slot, slotIndex + 1, zoom);
            for (var courtIndex = 0; courtIndex < day.Courts.Count; courtIndex++)
            {
                var court = day.Courts[courtIndex];
                var cellItems = dayItems
                    .Where(item => string.Equals(item.Court, court, StringComparison.Ordinal)
                                   && item.StartTime == slot)
                    .OrderBy(item => item.EventName, StringComparer.Ordinal)
                    .ToList();
                AddCrossEventDropCell(targetGrid, day.DayLabel, slot, court, slotIndex + 1, courtIndex + 1, cellItems, zoom);
            }
        }
    }

    private void AddCrossEventEmptyText(Grid targetGrid, string text, double zoom)
    {
        targetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var block = new TextBlock
        {
            Text = text,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(111, 102, 144)),
            FontSize = ScaleCrossEventFont(13, zoom),
            Margin = new Thickness(12)
        };
        targetGrid.Children.Add(block);
    }

    private void AddCrossEventHeaderCell(Grid targetGrid, string text, int row, int column, double zoom)
    {
        var border = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(237, 225, 252)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(216, 199, 244)),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(ScaleCrossEvent(10, zoom), ScaleCrossEvent(8, zoom), ScaleCrossEvent(10, zoom), ScaleCrossEvent(8, zoom)),
            Child = new TextBlock
            {
                Text = text,
                FontSize = ScaleCrossEventFont(13, zoom),
                FontWeight = FontWeights.Bold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 17, 109)),
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
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(ScaleCrossEvent(10, zoom), ScaleCrossEvent(14, zoom), ScaleCrossEvent(10, zoom), ScaleCrossEvent(14, zoom)),
            Child = new TextBlock
            {
                Text = slot.ToString("HH:mm"),
                FontSize = ScaleCrossEventFont(13, zoom),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center
            }
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, 0);
        targetGrid.Children.Add(border);
    }

    private void AddCrossEventDropCell(
        Grid targetGrid,
        string dayLabel,
        TimeOnly slot,
        string court,
        int row,
        int column,
        IReadOnlyList<CrossEventScheduleBoardItem> items,
        double zoom)
    {
        var stack = new StackPanel();
        foreach (var item in items)
        {
            stack.Children.Add(CreateCrossEventMatchCard(item, zoom));
        }

        var border = new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(0, 0, 1, 1),
            MinHeight = ScaleCrossEvent(72, zoom),
            Padding = new Thickness(ScaleCrossEvent(6, zoom)),
            Tag = new CrossEventDropTarget(dayLabel, slot, court),
            AllowDrop = true,
            Child = stack
        };
        border.DragOver += CrossEventCell_DragOver;
        border.Drop += CrossEventCell_Drop;
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        targetGrid.Children.Add(border);
    }

    private Border CreateCrossEventMatchCard(CrossEventScheduleBoardItem item, double zoom)
    {
        var borderColor = item.IsBlockingConflict
            ? System.Windows.Media.Color.FromRgb(220, 38, 38)
            : System.Windows.Media.Color.FromRgb(199, 210, 228);
        var backgroundColor = item.IsBlockingConflict
            ? System.Windows.Media.Color.FromRgb(254, 242, 242)
            : item.IsCompleted
                ? System.Windows.Media.Color.FromRgb(241, 245, 249)
                : System.Windows.Media.Color.FromRgb(248, 251, 255);
        var card = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(backgroundColor),
            BorderBrush = new System.Windows.Media.SolidColorBrush(borderColor),
            BorderThickness = new Thickness(item.IsBlockingConflict ? 2 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(ScaleCrossEvent(8, zoom)),
            Margin = new Thickness(0, 0, 0, ScaleCrossEvent(6, zoom)),
            Tag = item,
            Cursor = item.IsCompleted ? Cursors.Arrow : Cursors.Hand,
            ToolTip = string.IsNullOrWhiteSpace(item.ConflictSummary) ? null : item.ConflictSummary
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = item.MatchLabel,
            FontSize = ScaleCrossEventFont(13, zoom),
            FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(43, 20, 95)),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{item.TimeRange} · {item.Status}",
            Margin = new Thickness(0, ScaleCrossEvent(3, zoom), 0, 0),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
            FontSize = ScaleCrossEventFont(12, zoom)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{item.SideA}  vs  {item.SideB}",
            Margin = new Thickness(0, ScaleCrossEvent(3, zoom), 0, 0),
            TextWrapping = TextWrapping.Wrap,
            FontSize = ScaleCrossEventFont(12, zoom)
        });
        if (item.IsBlockingConflict)
        {
            stack.Children.Add(new TextBlock
            {
                Text = item.ConflictSummary,
                Margin = new Thickness(0, ScaleCrossEvent(3, zoom), 0, 0),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(185, 28, 28)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = ScaleCrossEventFont(12, zoom)
            });
        }

        card.Child = stack;
        card.MouseMove += CrossEventMatchCard_MouseMove;
        return card;
    }

    private void CrossEventMatchCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Border { Tag: CrossEventScheduleBoardItem item } || item.IsCompleted || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        DragDrop.DoDragDrop((DependencyObject)sender, item.Key, DragDropEffects.Move);
    }

    private void CrossEventCell_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void CrossEventCell_Drop(object sender, DragEventArgs e)
    {
        if (_crossEventScheduleBoard is null
            || sender is not Border { Tag: CrossEventDropTarget target }
            || e.Data.GetData(DataFormats.StringFormat) is not string key)
        {
            return;
        }

        if (key.StartsWith(ScheduleDragPrefix, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _crossEventScheduleBoard = _crossEventConflictWorkflow.MoveScheduleItem(
                _crossEventScheduleBoard,
                key,
                target.DayLabel,
                target.StartTime,
                target.Court);
            RefreshCrossEventScheduleBoard(target.DayLabel);
            RefreshCrossEventBoardWindow(target.DayLabel);
            SetStatus(BuildCrossEventStatus("已调整多项目赛程", _crossEventScheduleBoard));
        }
        catch (Exception ex) when (ex is TournamentProgressException or IOException or InvalidOperationException or DrawValidationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private string? GetSelectedCrossEventDayLabel()
    {
        return CrossEventDayBox.SelectedItem?.ToString();
    }

    private static string BuildCrossEventStatus(string prefix, CrossEventScheduleBoard board)
    {
        return $"{prefix}：兼项选手 {board.MultiEventPlayerCount} 人，严重 {board.Report.SevereCount} 条，间隔过短 {board.Report.WarningCount} 条，"
               + $"同日提醒 {board.Report.NoticeCount} 条，冲突卡片 {board.BlockingConflictItemCount} 张。";
    }

    private ExportFormat GetExportFormat()
    {
        return ExportFormatBox.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? Enum.Parse<ExportFormat>(item.Tag.ToString()!)
            : ExportFormat.Excel;
    }

    private ExportFormat GetScheduleExportFormat()
    {
        return ScheduleExportFormatBox.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? Enum.Parse<ExportFormat>(item.Tag.ToString()!)
            : ExportFormat.Excel;
    }

    private static string GetExportExtension(ExportFormat format)
    {
        return format switch
        {
            ExportFormat.Png => ".png",
            ExportFormat.Jpeg => ".jpg",
            ExportFormat.A4Pdf => ".pdf",
            _ => ".xlsx"
        };
    }

    private static string GetDialogFilter(ExportFormat format)
    {
        return format switch
        {
            ExportFormat.Png => "PNG 图片 (*.png)|*.png",
            ExportFormat.Jpeg => "JPG 图片 (*.jpg)|*.jpg",
            ExportFormat.A4Pdf => "A4 PDF (*.pdf)|*.pdf",
            ExportFormat.All => "全部导出文件基名 (*.xlsx)|*.xlsx",
            _ => "Excel 文件 (*.xlsx)|*.xlsx"
        };
    }

    private IReadOnlyList<string> ExportDrawResultFiles(string selectedPath, ExportFormat exportFormat)
    {
        if (_latestWorkflowResult is null)
        {
            throw new InvalidOperationException("请先预览抽签。");
        }

        return _drawWorkflow.ExportFiles(
            selectedPath,
            ToWorkflowExportFormat(exportFormat),
            _latestWorkflowResult,
            GetDrawVisualOptions(exportFormat));
    }

    private IReadOnlyList<string> ExportScheduleFiles(string selectedPath, ExportFormat exportFormat)
    {
        if (_latestSchedule is null)
        {
            throw new InvalidOperationException("请先生成赛程预览。");
        }

        return _scheduleWorkflow.ExportFiles(
            selectedPath,
            ToWorkflowExportFormat(exportFormat),
            _latestSchedule);
    }

    private IReadOnlyList<string> ExportTimedBracketFiles(string scheduleSelectedPath, ExportFormat exportFormat)
    {
        if (_latestWorkflowResult is null || _latestSchedule is null)
        {
            throw new InvalidOperationException("请先生成赛程预览。");
        }

        return _scheduleWorkflow.ExportTimedBracketFiles(
            scheduleSelectedPath,
            ToWorkflowExportFormat(exportFormat),
            _latestWorkflowResult,
            _latestSchedule,
            GetDrawVisualOptions(exportFormat));
    }

    private DrawResultVisualOptions GetDrawVisualOptions(ExportFormat format)
    {
        return (format is ExportFormat.A4Pdf or ExportFormat.All) && _latestResult is not null && _latestResult.Settings.IsKnockout
            ? new DrawResultVisualOptions(GetPdfTileValue(PdfRowsBox, "PDF 行数"), GetPdfTileValue(PdfColumnsBox, "PDF 列数"))
            : new DrawResultVisualOptions();
    }

    private static WorkflowExportFormat ToWorkflowExportFormat(ExportFormat format)
    {
        return Enum.Parse<WorkflowExportFormat>(format.ToString());
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

    private string? PickFolderPath(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void UpdateExportOptionsVisibility()
    {
        A4PdfOptionsPanel.Visibility = _latestResult is not null
            && GetExportFormat() is ExportFormat.A4Pdf or ExportFormat.All
            && _latestResult.Settings.IsKnockout
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateKnockoutGoalVisibility()
    {
        var isKnockout = GetCompetitionMode() is CompetitionMode.SinglesKnockout or CompetitionMode.TeamKnockout;
        var showGoalOptions = isKnockout
            && TryGetGroupCount(out var groupCount)
            && groupCount > 1
            && IsPowerOfTwo(groupCount);

        KnockoutGoalPanel.Visibility = showGoalOptions ? Visibility.Visible : Visibility.Collapsed;
        if (!showGoalOptions)
        {
            SelectKnockoutGoal(isKnockout && TryGetGroupCount(out groupCount) && groupCount <= 1
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
        PlacementPlayoffPanel.Visibility = showPlacementOptions ? Visibility.Visible : Visibility.Collapsed;
        if (!showPlacementOptions)
        {
            SelectPlacementPlayoff(PlacementPlayoff.None);
        }
    }

    private void UpdateScheduleTimingSplitVisibility()
    {
        var showTimingSplit = ShouldShowScheduleTimingSplit();
        ScheduleTimingSplitPanel.Visibility = showTimingSplit ? Visibility.Visible : Visibility.Collapsed;
        ScheduleDefaultTimingLabel.Content = showTimingSplit ? "分界线后设置" : "统一赛程设置";
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
        return int.TryParse(GroupCountBox.Text.Trim(), out groupCount);
    }

    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    private void SelectKnockoutGoal(KnockoutGoal knockoutGoal)
    {
        foreach (ComboBoxItem item in KnockoutGoalBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), knockoutGoal.ToString(), StringComparison.Ordinal))
            {
                KnockoutGoalBox.SelectedItem = item;
                return;
            }
        }

        throw new InvalidOperationException("缺少淘汰赛目标选项配置。");
    }

    private void SelectPlacementPlayoff(PlacementPlayoff placementPlayoff)
    {
        foreach (ComboBoxItem item in PlacementPlayoffBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), placementPlayoff.ToString(), StringComparison.Ordinal))
            {
                PlacementPlayoffBox.SelectedItem = item;
                return;
            }
        }

        throw new InvalidOperationException("缺少名次附加赛选项配置。");
    }

    private static int GetPdfTileValue(TextBox textBox, string name)
    {
        if (!int.TryParse(textBox.Text.Trim(), out var value) || value < 1 || value > 50)
        {
            throw new DrawValidationException($"{name}必须是 1 到 50 之间的整数。");
        }

        return value;
    }

    private void UpdatePreviewBadges(DrawResult? result = null)
    {
        var participantCount = result?.Audit.ParticipantCount ?? _participants.Count;
        var groupCountText = result?.Groups.Count.ToString() ?? GroupCountBox.Text.Trim();

        ParticipantPillText.Text = $"参赛单位 {participantCount} 个";
        SeedPillText.Text = $"随机数种子：{SeedBox.Text}";
        ParticipantCountText.Text = participantCount.ToString();
        EventKindStatText.Text = GetEventKindDisplay(GetEventKind());
        GroupCountStatText.Text = string.IsNullOrWhiteSpace(groupCountText) ? "-" : groupCountText;
    }

    private static string GetEventKindDisplay(EventKind eventKind)
    {
        return eventKind switch
        {
            EventKind.Singles => "单打",
            EventKind.Doubles => "双打",
            EventKind.Team => "团体",
            _ => eventKind.ToString()
        };
    }

    private void SetStatus(
        string message,
        StatusKind statusKind,
        IReadOnlyList<ParticipantImportWarning>? warnings = null)
    {
        var statusMessage = warnings is { Count: > 0 }
            ? $"{message} {BuildWarningStatusText(warnings)}"
            : message;
        StatusText.Text = statusMessage;

        if (statusKind == StatusKind.Error)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.DarkRed;
            StatusDot.Fill = System.Windows.Media.Brushes.IndianRed;
            return;
        }

        if (statusKind == StatusKind.Warning)
        {
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 91, 0));
            StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
            return;
        }

        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(90, 75, 126));
        StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 183, 122));
    }

    private static string BuildWarningStatusText(IReadOnlyList<ParticipantImportWarning> warnings)
    {
        if (warnings.Count == 1)
        {
            return $"{warnings[0].Summary}，可继续预览抽签。";
        }

        var duplicateCount = warnings.Count(warning => warning.Kind == ParticipantImportWarningKind.DuplicatePlayerName);
        var unrankedSeedCount = warnings.Count(warning => warning.Kind == ParticipantImportWarningKind.UnrankedSeed);
        var parts = new List<string>();

        if (duplicateCount > 0)
        {
            parts.Add($"同名选手 {duplicateCount} 组");
        }

        if (unrankedSeedCount > 0)
        {
            parts.Add($"种子未编号 {unrankedSeedCount} 个");
        }

        return $"发现名单提醒：{string.Join("，", parts)}，可继续预览抽签。";
    }

    private void SetStatus(string message, bool isError = false)
    {
        SetStatus(message, isError ? StatusKind.Error : StatusKind.Normal);
    }

    private sealed record ResultRow(string GroupName, int Order, string Name, string SeedLabel);

    private sealed record ScheduleRow(
        int Order,
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
        bool IsUnscheduled,
        string Reason);

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

    private sealed record ScheduleDayRow(
        DateOnly DateValue,
        TimeOnly StartTime,
        TimeOnly EndTime,
        string Venue,
        IReadOnlyList<string> Courts)
    {
        public string Date => DateValue.ToString("yyyy-MM-dd");

        public string TimeRange => $"{StartTime:HH:mm}-{EndTime:HH:mm}";

        public string CourtSummary => $"{Courts.Count}片：" + string.Join("、", Courts.Take(6)) + (Courts.Count > 6 ? "…" : "");
    }

    private sealed record CrossEventPlayerSummaryRow(
        CrossEventPlayerMultiEntry Entry,
        string OrderText,
        string PlayerName,
        string EventCountText,
        string EventNamesText,
        string MatchCountText,
        string PendingMatchCountText,
        string SevereIssueCountText,
        string WarningIssueCountText,
        string ShortestRestText,
        string NextMatchText);

    private sealed record CrossEventPlayerAppearanceRow(
        string Status,
        string DayLabel,
        string TimeRange,
        string Court,
        string EventName,
        string Phase,
        string MatchName,
        string Side,
        string SideText,
        string OpponentText,
        string ConflictSummary);

    private sealed record CrossEventDropTarget(
        string DayLabel,
        TimeOnly StartTime,
        string Court);

    private sealed record ScheduleDropTarget(
        string DayLabel,
        TimeOnly StartTime,
        string Court);

    private enum StatusKind
    {
        Normal,
        Warning,
        Error
    }

    private enum CrossEventPlayerSortMode
    {
        Default,
        RestAscending,
        RestDescending
    }

    private enum ExportFormat
    {
        Excel,
        Jpeg,
        Png,
        A4Pdf,
        All
    }
}
