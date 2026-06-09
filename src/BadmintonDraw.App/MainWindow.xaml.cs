using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using Microsoft.Win32;

namespace BadmintonDraw.App;

public partial class MainWindow : Window
{
    private const string BracketSheetName = "对阵表";
    private const string ScheduleGridSheetName = "时间场地网格";

    private readonly DrawService _drawService = new();
    private readonly ParticipantExcelReader _reader = new();
    private readonly DrawResultExcelWriter _writer = new();
    private readonly DrawResultVisualWriter _visualWriter = new();
    private readonly ParticipantTemplateWriter _templateWriter = new();
    private readonly ScheduleService _scheduleService = new();
    private readonly ScheduleExcelWriter _scheduleWriter = new();
    private readonly MatchRecordReader _matchRecordReader = new();
    private IReadOnlyList<DrawParticipant> _participants = Array.Empty<DrawParticipant>();
    private IReadOnlyList<ParticipantImportWarning> _importWarnings = Array.Empty<ParticipantImportWarning>();
    private readonly ObservableCollection<ScheduleDayRow> _scheduleDays = [];
    private string? _loadedInputPath;
    private DrawResult? _latestResult;
    private SchedulePlan? _latestSchedule;

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
        if (!TryGenerate())
        {
            return;
        }

        var exportFormat = GetExportFormat();
        var dialog = new SaveFileDialog
        {
            Filter = GetDialogFilter(exportFormat),
            DefaultExt = GetExportExtension(exportFormat),
            AddExtension = true,
            FileName = BuildDefaultExportFileName(_latestResult!, _loadedInputPath ?? InputPathBox.Text, exportFormat),
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
            _templateWriter.Write(dialog.FileName);
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
        if (!TryGenerateSchedule())
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
            FileName = BuildDefaultScheduleFileName(_latestResult, _loadedInputPath ?? InputPathBox.Text, exportFormat),
            Title = "保存赛程表"
        };

        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                var schedulePaths = ExportScheduleFiles(dialog.FileName, exportFormat);
                if (ExportTimedBracketBox.IsChecked == true)
                {
                    var timedBracketPaths = ExportTimedBracketFiles(dialog.FileName, exportFormat);
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
    }

    private void ExportMatchRecord_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGenerateSchedule())
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

        var dayLabel = GetSelectedMatchRecordDayLabel(_latestSchedule);
        if (string.IsNullOrWhiteSpace(dayLabel))
        {
            SetStatus("当前赛程没有可导出的比赛日。", isError: true);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Excel 文件 (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            FileName = BuildDefaultMatchRecordFileName(dayLabel),
            Title = "保存赛程记录表"
        };

        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                _scheduleWriter.WriteMatchRecord(dialog.FileName, _latestSchedule, dayLabel);
                SetStatus($"赛程记录表已导出：{dialog.FileName}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or DrawValidationException)
            {
                SetStatus(ex.Message, isError: true);
            }
        }
    }

    private void ImportMatchRecordAndExportNext_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGenerateSchedule())
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
            Title = "选择已填写的赛程记录表"
        };

        if (importDialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var importResult = _matchRecordReader.Read(importDialog.FileName);
            if (importResult.Results.Count == 0)
            {
                SetStatus("记录表中没有读取到已填写胜方的比赛。", isError: true);
                return;
            }

            var nextDayLabel = GetNextMatchRecordDayLabel(_latestSchedule, importResult);
            if (string.IsNullOrWhiteSpace(nextDayLabel))
            {
                SetStatus("已读取比赛结果，但当前赛程没有下一比赛日可导出。", StatusKind.Warning);
                return;
            }

            var exportDialog = new SaveFileDialog
            {
                Filter = "Excel 文件 (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                AddExtension = true,
                FileName = BuildDefaultMatchRecordFileName(nextDayLabel),
                Title = "保存下一比赛日赛程记录表"
            };

            if (exportDialog.ShowDialog(this) == true)
            {
                _scheduleWriter.WriteMatchRecord(
                    exportDialog.FileName,
                    _latestSchedule,
                    nextDayLabel,
                    importResult.Results);
                SetStatus($"已读取 {importResult.Results.Count} 场结果，并导出下一比赛日记录表：{exportDialog.FileName}");
            }
        }
        catch (Exception ex) when (ex is ExcelImportException or IOException or InvalidOperationException or DrawValidationException)
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
            ApplyImportResult(_reader.ReadParticipantsWithWarnings(InputPathBox.Text, GetEventKind()));
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

            var settings = new DrawSettings(
                GetCompetitionMode(),
                GetEventKind(),
                groupCount,
                SeedBox.Text,
                KnockoutGoal: GetKnockoutGoal(),
                PlacementPlayoff: GetPlacementPlayoff());

            ApplyImportResult(_reader.ReadParticipantsWithWarnings(InputPathBox.Text, settings.EventKind));
            _latestResult = _drawService.Generate(_participants, settings);
            ClearSchedulePreview();
            UpdateScheduleTimingSplitVisibility();

            GroupsGrid.ItemsSource = ToRows(_latestResult.Groups);
            RoundOneGrid.ItemsSource = ToRows(_latestResult.RoundOneGroups);
            ByeGrid.ItemsSource = ToRows(_latestResult.ByeGroups);
            SummaryText.Text = $"已生成 {_latestResult.Groups.Count} 个小组";
            ParticipantCountText.Text = _latestResult.Audit.ParticipantCount.ToString();
            EventKindStatText.Text = GetEventKindDisplay(settings.EventKind);
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
            _latestSchedule = _scheduleService.Generate(_latestResult, settings);
            ScheduleGrid.ItemsSource = ToScheduleRows(_latestSchedule);
            ScheduleSummaryText.Text = _latestSchedule.IsComplete
                ? $"已生成 {_latestSchedule.Matches.Count} 场，预计 {_latestSchedule.DayCount} 个比赛日"
                : $"已安排 {_latestSchedule.Matches.Count} 场，未安排 {_latestSchedule.UnscheduledMatches.Count} 场，共 {_latestSchedule.TotalMatchCount} 场";
            ScheduleCapacityText.Text = BuildScheduleCapacityText(settings);
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
        ScheduleTimingSettings? beforeBoundaryTiming = null;
        if (boundaryEntrants > 0)
        {
            var beforeMatchMinutes = ParsePositiveScheduleInt(BeforeBoundaryMatchMinutesBox.Text, "分界线前单场比赛耗时");
            var beforeMaxMatchesPerDay = ParsePositiveScheduleInt(
                GetSelectedComboBoxText(BeforeBoundaryMaxMatchesPerDayBox),
                "分界线前单名选手每日最多场次");
            beforeBoundaryTiming = new ScheduleTimingSettings(beforeMatchMinutes, beforeMaxMatchesPerDay);
        }

        var days = _scheduleDays
            .OrderBy(day => day.DateValue)
            .Select(day => new ScheduleDaySettings(day.DateValue, day.StartTime, day.EndTime, day.Courts))
            .ToList();

        return new ScheduleSettings(
            days,
            matchMinutes,
            maxMatchesPerDay,
            boundaryEntrants > 0 ? boundaryEntrants : null,
            beforeBoundaryTiming);
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

        var courts = ParseCourts(ScheduleCourtsBox.Text);
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

    private static IReadOnlyList<string> ParseCourts(string value)
    {
        var courts = Regex.Split(value, @"[,\s，、;；]+")
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (courts.Count == 0)
        {
            throw new DrawValidationException("请填写至少一片可用场地。");
        }

        return courts;
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
        GroupsGrid.ItemsSource = null;
        RoundOneGrid.ItemsSource = null;
        ByeGrid.ItemsSource = null;
        ClearSchedulePreview();
        SummaryText.Text = "尚未导入名单。";
        PreviewStateText.Text = "待导入";
        UpdatePreviewBadges();
    }

    private void ApplyImportResult(ParticipantImportResult importResult)
    {
        _participants = importResult.Participants;
        _importWarnings = importResult.Warnings;
        _loadedInputPath = InputPathBox.Text;
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
        var detectedEventKind = _reader.DetectEventKind(InputPathBox.Text, originalEventKind);

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
        return $"SZUBA-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(1000, 9999)}";
    }

    private string? GetSelectedMatchRecordDayLabel(SchedulePlan plan)
    {
        if (ScheduleDaysGrid.SelectedItem is ScheduleDayRow selectedDay
            && plan.Matches.Any(match => match.DayLabel == selectedDay.Date))
        {
            return selectedDay.Date;
        }

        return plan.Matches
            .FirstOrDefault(HasExplicitScheduleSides)
            ?.DayLabel
            ?? plan.Matches.FirstOrDefault()?.DayLabel;
    }

    private static string? GetNextMatchRecordDayLabel(SchedulePlan plan, MatchRecordImportResult importResult)
    {
        var scheduleDays = plan.Matches
            .Select(match => match.DayLabel)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (scheduleDays.Count == 0 || importResult.DayLabels.Count == 0)
        {
            return null;
        }

        var importedDaySet = importResult.DayLabels.ToHashSet(StringComparer.Ordinal);
        var importedIndexes = scheduleDays
            .Select((day, index) => importedDaySet.Contains(day) ? index : -1)
            .Where(index => index >= 0)
            .ToList();
        if (importedIndexes.Count > 0)
        {
            var nextIndex = importedIndexes.Max() + 1;
            return nextIndex < scheduleDays.Count ? scheduleDays[nextIndex] : null;
        }

        var latestImportedDate = importResult.DayLabels
            .Select(day => DateOnly.TryParse(day, out var date) ? date : (DateOnly?)null)
            .Where(date => date.HasValue)
            .Select(date => date!.Value)
            .DefaultIfEmpty()
            .Max();
        if (latestImportedDate == default)
        {
            return null;
        }

        return scheduleDays.FirstOrDefault(day =>
            DateOnly.TryParse(day, out var date) && date > latestImportedDate);
    }

    private static bool HasExplicitScheduleSides(ScheduledMatch match)
    {
        return !IsOutcomeReference(match.SideA) && !IsOutcomeReference(match.SideB);
    }

    private static bool IsOutcomeReference(string side)
    {
        return side.EndsWith("胜者", StringComparison.Ordinal)
            || side.EndsWith("负者", StringComparison.Ordinal);
    }

    private static string BuildDefaultExportFileName(DrawResult result, string inputPath, ExportFormat format)
    {
        var parts = new List<string>
        {
            ExtractEventName(inputPath),
            GetCompetitionModeFileNamePart(result.Settings.CompetitionMode),
            GetEventScaleFileNamePart(result.Settings.EventKind, result.Audit.ParticipantCount),
            $"{result.Audit.GroupCount}组"
        };

        var knockoutGoalPart = GetKnockoutGoalFileNamePart(result.Settings);
        if (!string.IsNullOrWhiteSpace(knockoutGoalPart))
        {
            parts.Add(knockoutGoalPart);
        }

        var placementPlayoffPart = GetPlacementPlayoffFileNamePart(result.Settings);
        if (!string.IsNullOrWhiteSpace(placementPlayoffPart))
        {
            parts.Add(placementPlayoffPart);
        }

        parts.Add(result.Audit.GeneratedAt.LocalDateTime.ToString("yyyyMMdd_HHmm"));
        parts.Add($"seed{GetSeedTail(result.Audit.RandomSeed)}");

        var stem = string.Join("_", parts.Select(SanitizeFileNamePart).Where(part => !string.IsNullOrWhiteSpace(part)));
        stem = LimitFileNameLength(stem, maxLength: 150);

        return $"{stem}{GetExportExtension(format)}";
    }

    private static string BuildDefaultScheduleFileName(DrawResult result, string inputPath, ExportFormat format)
    {
        var parts = new List<string>
        {
            ExtractEventName(inputPath),
            "赛程表",
            GetCompetitionModeFileNamePart(result.Settings.CompetitionMode),
            GetEventScaleFileNamePart(result.Settings.EventKind, result.Audit.ParticipantCount),
            $"{result.Audit.GroupCount}组",
            GetPlacementPlayoffFileNamePart(result.Settings) ?? "",
            DateTime.Now.ToString("yyyyMMdd_HHmm")
        };

        var stem = string.Join("_", parts.Select(SanitizeFileNamePart).Where(part => !string.IsNullOrWhiteSpace(part)));
        stem = LimitFileNameLength(stem, maxLength: 150);

        return $"{stem}{GetExportExtension(format)}";
    }

    private static string BuildDefaultMatchRecordFileName(string dayLabel)
    {
        var stem = DateOnly.TryParse(dayLabel, out var date)
            ? $"{date.Month}月{date.Day}日赛程记录表"
            : $"{dayLabel}赛程记录表";

        return $"{SanitizeFileNamePart(stem)}.xlsx";
    }

    private static string BuildTimedBracketPath(string scheduleOutputPath, ExportFormat format)
    {
        var directory = Path.GetDirectoryName(scheduleOutputPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(scheduleOutputPath);
        return Path.ChangeExtension(
            Path.Combine(directory, $"{stem}_带比赛时间和场地对阵表"),
            GetExportExtension(format));
    }

    private static string ExtractEventName(string inputPath)
    {
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return "抽签结果";
        }

        var normalized = stem.Trim();
        normalized = Regex.Replace(normalized, @"\s*[-—–]\s*副本$", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"副本$", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"参赛名单模板|参赛名单|名单模板|名单|模板|抽签结果", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\d+\s*组\s*种子", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\d+\s*[人对队组]\b?", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"[_\-\s（）()]+$", "");
        normalized = Regex.Replace(normalized, @"^[_\-\s（）()]+", "");
        normalized = Regex.Replace(normalized, @"[_\-\s]+", "_");

        return string.IsNullOrWhiteSpace(normalized) ? "抽签结果" : normalized;
    }

    private static string GetCompetitionModeFileNamePart(CompetitionMode competitionMode)
    {
        return competitionMode is CompetitionMode.SinglesRoundRobin or CompetitionMode.TeamRoundRobin
            ? "循环赛"
            : "淘汰赛";
    }

    private static string GetEventScaleFileNamePart(EventKind eventKind, int participantCount)
    {
        return eventKind switch
        {
            EventKind.Doubles => $"双打{participantCount}对",
            EventKind.Team => $"团体{participantCount}队",
            _ => $"单打{participantCount}人"
        };
    }

    private static string? GetKnockoutGoalFileNamePart(DrawSettings settings)
    {
        if (!settings.IsKnockout)
        {
            return null;
        }

        if (settings.GroupCount == 1 || settings.KnockoutGoal == KnockoutGoal.Champion)
        {
            return "决出冠军";
        }

        return "每组出线";
    }

    private static string? GetPlacementPlayoffFileNamePart(DrawSettings settings)
    {
        return settings.PlacementPlayoff switch
        {
            PlacementPlayoff.ThirdPlace => "排3-4名",
            PlacementPlayoff.ThirdToEighth => "排3-8名",
            _ => null
        };
    }

    private static string GetSeedTail(string randomSeed)
    {
        var tail = randomSeed
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? randomSeed;
        tail = SanitizeFileNamePart(tail);

        if (string.IsNullOrWhiteSpace(tail))
        {
            return "custom";
        }

        return tail.Length <= 8 ? tail : tail[^8..];
    }

    private static string SanitizeFileNamePart(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var chars = value
            .Trim()
            .Select(ch => char.IsControl(ch) || invalidChars.Contains(ch) ? '_' : ch)
            .ToArray();
        var sanitized = new string(chars);
        sanitized = Regex.Replace(sanitized, @"_+", "_");
        sanitized = Regex.Replace(sanitized, @"\s+", "");
        return sanitized.Trim('_', '-', ' ');
    }

    private static string LimitFileNameLength(string stem, int maxLength)
    {
        if (stem.Length <= maxLength)
        {
            return stem;
        }

        return stem[..maxLength].TrimEnd('_', '-', ' ');
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

    private static DrawResultVisualFormat ToVisualFormat(ExportFormat format)
    {
        return format switch
        {
            ExportFormat.Png => DrawResultVisualFormat.Png,
            ExportFormat.Jpeg => DrawResultVisualFormat.Jpeg,
            ExportFormat.A4Pdf => DrawResultVisualFormat.A4Pdf,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Excel 导出不需要视觉格式。")
        };
    }

    private IReadOnlyList<string> ExportDrawResultFiles(string selectedPath, ExportFormat exportFormat)
    {
        if (_latestResult is null)
        {
            throw new InvalidOperationException("请先预览抽签。");
        }

        return ExportFromWorkbook(
            selectedPath,
            exportFormat,
            BracketSheetName,
            path => _writer.Write(path, _latestResult, _participants),
            GetDrawVisualOptions);
    }

    private IReadOnlyList<string> ExportScheduleFiles(string selectedPath, ExportFormat exportFormat)
    {
        if (_latestSchedule is null)
        {
            throw new InvalidOperationException("请先生成赛程预览。");
        }

        return ExportFromWorkbook(
            selectedPath,
            exportFormat,
            ScheduleGridSheetName,
            path => _scheduleWriter.Write(path, _latestSchedule),
            _ => new DrawResultVisualOptions());
    }

    private IReadOnlyList<string> ExportTimedBracketFiles(string scheduleSelectedPath, ExportFormat exportFormat)
    {
        if (_latestResult is null || _latestSchedule is null)
        {
            throw new InvalidOperationException("请先生成赛程预览。");
        }

        return ExportFromWorkbook(
            BuildTimedBracketPath(scheduleSelectedPath, exportFormat),
            exportFormat,
            BracketSheetName,
            path => _writer.Write(path, _latestResult, _participants, _latestSchedule),
            GetDrawVisualOptions);
    }

    private IReadOnlyList<string> ExportFromWorkbook(
        string selectedPath,
        ExportFormat selectedFormat,
        string visualSheetName,
        Action<string> writeWorkbook,
        Func<ExportFormat, DrawResultVisualOptions> getVisualOptions)
    {
        var formats = ExpandExportFormats(selectedFormat);
        var outputPaths = new List<string>();
        var tempExcelPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-export-{Guid.NewGuid():N}.xlsx");
        string? sourceExcelPath = null;
        try
        {
            if (formats.Contains(ExportFormat.Excel))
            {
                var excelPath = BuildOutputPath(selectedPath, ExportFormat.Excel);
                writeWorkbook(excelPath);
                sourceExcelPath = excelPath;
                outputPaths.Add(excelPath);
            }

            var visualFormats = formats.Where(format => format != ExportFormat.Excel).ToList();
            if (visualFormats.Count > 0)
            {
                if (sourceExcelPath is null)
                {
                    writeWorkbook(tempExcelPath);
                    sourceExcelPath = tempExcelPath;
                }

                foreach (var format in visualFormats)
                {
                    var outputPath = BuildOutputPath(selectedPath, format);
                    _visualWriter.Write(outputPath, sourceExcelPath, visualSheetName, ToVisualFormat(format), getVisualOptions(format));
                    outputPaths.Add(outputPath);
                }
            }

            return outputPaths;
        }
        finally
        {
            if (sourceExcelPath == tempExcelPath && File.Exists(tempExcelPath))
            {
                File.Delete(tempExcelPath);
            }
        }
    }

    private DrawResultVisualOptions GetDrawVisualOptions(ExportFormat format)
    {
        return format == ExportFormat.A4Pdf && _latestResult is not null && _latestResult.Settings.IsKnockout
            ? new DrawResultVisualOptions(GetPdfTileValue(PdfRowsBox, "PDF 行数"), GetPdfTileValue(PdfColumnsBox, "PDF 列数"))
            : new DrawResultVisualOptions();
    }

    private static IReadOnlyList<ExportFormat> ExpandExportFormats(ExportFormat format)
    {
        return format == ExportFormat.All
            ? [ExportFormat.Excel, ExportFormat.Jpeg, ExportFormat.Png, ExportFormat.A4Pdf]
            : [format];
    }

    private static string BuildOutputPath(string selectedPath, ExportFormat format)
    {
        return Path.ChangeExtension(selectedPath, GetExportExtension(format));
    }

    private static string FormatOutputPaths(IReadOnlyList<string> outputPaths)
    {
        return string.Join("；", outputPaths);
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
