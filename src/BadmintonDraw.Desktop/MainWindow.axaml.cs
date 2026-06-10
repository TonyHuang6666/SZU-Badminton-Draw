using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BadmintonDraw.Core;
using BadmintonDraw.Workflows;

namespace BadmintonDraw.Desktop;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType ExcelFileType = new("Excel 文件")
    {
        Patterns = ["*.xlsx"],
        MimeTypes = ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"]
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
    private DrawWorkflowResult? _latestWorkflowResult;
    private DrawResult? _latestResult;
    private SchedulePlan? _latestSchedule;
    private string? _loadedInputPath;

    public MainWindow()
    {
        InitializeComponent();
        SeedBox.Text = DrawWorkflow.GenerateSeed();
        ScheduleDateBox.Text = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
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
        if (!TryGenerate() || _latestResult is null)
        {
            return;
        }

        var suggestedName = DrawWorkflow.BuildDefaultDrawExcelFileName(_latestResult, _loadedInputPath ?? InputPathBox.Text);
        var path = await PickSavePath("保存抽签结果", suggestedName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _drawWorkflow.ExportExcel(path, _latestWorkflowResult!);
            SetStatus($"已导出 Excel：{path}");
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

        var suggestedName = ScheduleWorkflow.BuildDefaultScheduleExcelFileName(_latestResult, _loadedInputPath ?? InputPathBox.Text);
        var path = await PickSavePath("保存赛程表", suggestedName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _scheduleWorkflow.ExportExcel(path, _latestSchedule);
            SetStatus($"赛程表已导出：{path}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DrawValidationException)
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

            var settings = ScheduleWorkflow.BuildSettings(BuildScheduleRequest());
            _latestSchedule = _scheduleWorkflow.Generate(_latestResult, settings);
            ScheduleSummaryText.Text = _latestSchedule.IsComplete
                ? $"已生成 {_latestSchedule.Matches.Count} 场，预计 {_latestSchedule.DayCount} 个比赛日。"
                : $"已安排 {_latestSchedule.Matches.Count} 场，未安排 {_latestSchedule.UnscheduledMatches.Count} 场，共 {_latestSchedule.TotalMatchCount} 场。";
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

    private ScheduleWorkflowRequest BuildScheduleRequest()
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

        return new ScheduleWorkflowRequest(
            date,
            start,
            end,
            ScheduleCourtsBox.Text ?? "",
            ParsePositiveInt(ScheduleMatchMinutesBox.Text, "每场分钟"),
            ParsePositiveInt(ScheduleMaxMatchesBox.Text, "每日最多场"));
    }

    private async System.Threading.Tasks.Task<string?> PickSavePath(string title, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "xlsx",
            FileTypeChoices = [ExcelFileType]
        });
        return file?.TryGetLocalPath();
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
