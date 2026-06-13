using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private IReadOnlyList<DrawParticipant> _participants = Array.Empty<DrawParticipant>();
    private IReadOnlyList<ParticipantImportWarning> _importWarnings = Array.Empty<ParticipantImportWarning>();
    private readonly ObservableCollection<ScheduleDayRow> _scheduleDays = [];
    private string? _loadedInputPath;
    private DrawResult? _latestResult;
    private DrawWorkflowResult? _latestWorkflowResult;
    private SchedulePlan? _latestSchedule;
    private string? _progressFilePath;
    private TournamentProgressState? _progressState;

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
            SetStatus($"{package.DayLabel} 首日材料包已导出：{FormatOutputPaths(package.OutputPaths)}");
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
                + $"并导出 {package.DayLabel} 材料包：{FormatOutputPaths(package.OutputPaths)}");
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
                + $"{package.DayLabel} 材料包已导出：{FormatOutputPaths(package.OutputPaths)}");
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

        if (duplicateWarnings.Count >= 2)
        {
            sections.Add("发现多组同名选手：\n" + FormatWarningList(duplicateWarnings));
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

    private enum StatusKind
    {
        Normal,
        Warning,
        Error
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
