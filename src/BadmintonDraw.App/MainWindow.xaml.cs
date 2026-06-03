using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using Microsoft.Win32;

namespace BadmintonDraw.App;

public partial class MainWindow : Window
{
    private readonly DrawService _drawService = new();
    private readonly ParticipantExcelReader _reader = new();
    private readonly DrawResultExcelWriter _writer = new();
    private readonly DrawResultVisualWriter _visualWriter = new();
    private readonly ParticipantTemplateWriter _templateWriter = new();
    private IReadOnlyList<DrawParticipant> _participants = Array.Empty<DrawParticipant>();
    private IReadOnlyList<ParticipantImportWarning> _importWarnings = Array.Empty<ParticipantImportWarning>();
    private string? _loadedInputPath;
    private DrawResult? _latestResult;

    public MainWindow()
    {
        InitializeComponent();
        SeedBox.Text = GenerateSeed();
        UpdateEventKindForMode();
        UpdateKnockoutGoalVisibility();
        UpdatePreviewBadges();
        UpdateExportOptionsVisibility();
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
            FileName = $"抽签结果_{DateTime.Now:yyyyMMdd_HHmm}{GetExportExtension(exportFormat)}",
            Title = "保存抽签结果"
        };

        if (dialog.ShowDialog(this) == true && _latestResult is not null)
        {
            var outputPath = Path.ChangeExtension(dialog.FileName, GetExportExtension(exportFormat));

            try
            {
                if (exportFormat == ExportFormat.Excel)
                {
                    _writer.Write(outputPath, _latestResult, _participants);
                }
                else
                {
                    ExportVisualResult(outputPath, exportFormat);
                }

                SetStatus($"已导出：{outputPath}");
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

    private void CompetitionModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateEventKindForMode();
            UpdateKnockoutGoalVisibility();
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
                KnockoutGoal: GetKnockoutGoal());

            ApplyImportResult(_reader.ReadParticipantsWithWarnings(InputPathBox.Text, settings.EventKind));
            _latestResult = _drawService.Generate(_participants, settings);

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

        return TryGetGroupCount(out var groupCount) && groupCount > 1 && IsPowerOfTwo(groupCount)
            ? Enum.Parse<KnockoutGoal>(GetSelectedTag(KnockoutGoalBox))
            : KnockoutGoal.OneQualifierPerGroup;
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

    private static string GenerateSeed()
    {
        return $"SZUBA-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(1000, 9999)}";
    }

    private ExportFormat GetExportFormat()
    {
        return ExportFormatBox.SelectedItem is ComboBoxItem item && item.Tag is not null
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

    private void ExportVisualResult(string outputPath, ExportFormat exportFormat)
    {
        if (_latestResult is null)
        {
            throw new InvalidOperationException("请先预览抽签。");
        }

        var tempExcelPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-export-{Guid.NewGuid():N}.xlsx");
        try
        {
            _writer.Write(tempExcelPath, _latestResult, _participants);
            var options = exportFormat == ExportFormat.A4Pdf && _latestResult.Settings.IsKnockout
                ? new DrawResultVisualOptions(GetPdfTileValue(PdfRowsBox, "PDF 行数"), GetPdfTileValue(PdfColumnsBox, "PDF 列数"))
                : new DrawResultVisualOptions();
            _visualWriter.Write(outputPath, tempExcelPath, "对阵表", ToVisualFormat(exportFormat), options);
        }
        finally
        {
            if (File.Exists(tempExcelPath))
            {
                File.Delete(tempExcelPath);
            }
        }
    }

    private void UpdateExportOptionsVisibility()
    {
        A4PdfOptionsPanel.Visibility = _latestResult is not null
            && GetExportFormat() == ExportFormat.A4Pdf
            && _latestResult.Settings.IsKnockout
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateKnockoutGoalVisibility()
    {
        var showGoalOptions = GetCompetitionMode() is CompetitionMode.SinglesKnockout or CompetitionMode.TeamKnockout
            && TryGetGroupCount(out var groupCount)
            && groupCount > 1
            && IsPowerOfTwo(groupCount);

        KnockoutGoalPanel.Visibility = showGoalOptions ? Visibility.Visible : Visibility.Collapsed;
        if (!showGoalOptions)
        {
            SelectKnockoutGoal(KnockoutGoal.OneQualifierPerGroup);
        }
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
        A4Pdf
    }
}
