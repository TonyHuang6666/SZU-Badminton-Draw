using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BadmintonDraw.Core;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Desktop;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType ExcelFileType = new("Excel 文件")
    {
        Patterns = ["*.xlsx"],
        MimeTypes = ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"]
    };

    private readonly DrawService _drawService = new();
    private readonly ParticipantExcelReader _reader = new();
    private readonly DrawResultExcelWriter _writer = new();
    private readonly ParticipantTemplateWriter _templateWriter = new();

    private IReadOnlyList<DrawParticipant> _participants = [];
    private IReadOnlyList<ParticipantImportWarning> _importWarnings = [];
    private DrawResult? _latestResult;
    private string? _loadedInputPath;

    public MainWindow()
    {
        InitializeComponent();
        SeedBox.Text = GenerateSeed();
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
            _templateWriter.Write(path);
            SetStatus($"已生成名单模板：{path}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    private void GenerateSeed_Click(object? sender, RoutedEventArgs e)
    {
        SeedBox.Text = GenerateSeed();
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

        var suggestedName = BuildDefaultExportFileName(_latestResult, _loadedInputPath ?? InputPathBox.Text);
        var path = await PickSavePath("保存抽签结果", suggestedName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _writer.Write(path, _latestResult, _participants);
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

            var detectedEventKind = _reader.DetectEventKind(inputPath, GetEventKind());
            ApplyDetectedEventKind(detectedEventKind);
            var importResult = _reader.ReadParticipantsWithWarnings(inputPath, detectedEventKind);
            _participants = importResult.Participants;
            _importWarnings = importResult.Warnings;
            _loadedInputPath = inputPath;
            _latestResult = null;
            ParticipantCountText.Text = _participants.Count.ToString();
            EventKindStatText.Text = GetEventKindDisplay(detectedEventKind);
            PreviewStateText.Text = "待预览";
            SummaryText.Text = $"已导入 {_participants.Count} 个参赛单位";
            WarningText.Text = FormatWarnings(_importWarnings);
            SetStatus(_importWarnings.Count > 0
                ? $"名单已导入，但有 {_importWarnings.Count} 条提醒。确认无误后可以预览抽签。"
                : "名单已导入，可以预览抽签。",
                _importWarnings.Count > 0);
            return true;
        }
        catch (Exception ex) when (ex is ExcelImportException or DrawValidationException or IOException)
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

            var settings = new DrawSettings(
                GetCompetitionMode(),
                GetEventKind(),
                groupCount,
                SeedBox.Text ?? "",
                KnockoutGoal: GetKnockoutGoal(),
                PlacementPlayoff: GetPlacementPlayoff());
            var importResult = _reader.ReadParticipantsWithWarnings(inputPath, settings.EventKind);
            _participants = importResult.Participants;
            _importWarnings = importResult.Warnings;
            _latestResult = _drawService.Generate(_participants, settings);

            ParticipantCountText.Text = _latestResult.Audit.ParticipantCount.ToString();
            GroupCountStatText.Text = _latestResult.Audit.GroupCount.ToString();
            EventKindStatText.Text = GetEventKindDisplay(settings.EventKind);
            PreviewStateText.Text = "已预览";
            SummaryText.Text = $"已生成 {_latestResult.Groups.Count} 个小组，随机种子 {_latestResult.Audit.RandomSeed}";
            WarningText.Text = FormatWarnings(_importWarnings);
            GroupsList.ItemsSource = FormatGroups(_latestResult.Groups);
            RoundOneList.ItemsSource = FormatGroups(_latestResult.RoundOneGroups);
            ByeList.ItemsSource = FormatGroups(_latestResult.ByeGroups);
            SetStatus(_importWarnings.Count > 0
                ? "抽签预览已生成；名单提醒请人工复核。"
                : "抽签预览已生成，可导出 Excel。",
                _importWarnings.Count > 0);
            return true;
        }
        catch (Exception ex) when (ex is ExcelImportException or IOException)
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
        _latestResult = null;
        _loadedInputPath = null;
        ParticipantCountText.Text = "-";
        GroupCountStatText.Text = "-";
        EventKindStatText.Text = "-";
        PreviewStateText.Text = "待预览";
        SummaryText.Text = "尚未生成抽签预览";
        WarningText.Text = "";
        GroupsList.ItemsSource = Array.Empty<string>();
        RoundOneList.ItemsSource = Array.Empty<string>();
        ByeList.ItemsSource = Array.Empty<string>();
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

    private static IReadOnlyList<string> FormatGroups(IReadOnlyList<DrawGroup> groups)
    {
        if (groups.Count == 0)
        {
            return ["无"];
        }

        return groups
            .Select(group => $"{BuildGroupName(group.Number)}（{group.Count}）：{string.Join("、", group.Participants.Select(FormatParticipant))}")
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

    private static string FormatWarnings(IReadOnlyList<ParticipantImportWarning> warnings)
    {
        return warnings.Count == 0
            ? ""
            : string.Join(Environment.NewLine, warnings.Take(5).Select(warning => $"{warning.Summary}：{warning.Detail}"));
    }

    private static string BuildDefaultExportFileName(DrawResult result, string? inputPath)
    {
        var eventName = GetEventKindDisplay(result.Settings.EventKind);
        var modeName = result.Settings.IsKnockout ? "淘汰赛" : "循环赛";
        var sourceName = string.IsNullOrWhiteSpace(inputPath)
            ? "深大羽协"
            : Path.GetFileNameWithoutExtension(inputPath);
        var stem = string.Join("_", new[]
        {
            SanitizeFileNamePart(sourceName),
            modeName,
            $"{eventName}{result.Audit.ParticipantCount}人",
            $"{result.Audit.GroupCount}组",
            DateTime.Now.ToString("yyyyMMdd_HHmm")
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return $"{stem}.xlsx";
    }

    private static string SanitizeFileNamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();
    }

    private static string BuildGroupName(int groupNumber)
    {
        if (groupNumber <= 0)
        {
            return "总决赛";
        }

        var columnName = "";
        var number = groupNumber;
        while (number > 0)
        {
            number--;
            columnName = (char)('A' + number % 26) + columnName;
            number /= 26;
        }

        return $"{columnName}组";
    }

    private static string GetEventKindDisplay(EventKind eventKind)
    {
        return eventKind switch
        {
            EventKind.Singles => "单打",
            EventKind.Team => "团体",
            _ => "双打"
        };
    }

    private static string GenerateSeed()
    {
        return $"SZUBA-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(1000, 9999)}";
    }

    private void SetStatus(string message, bool isWarning = false, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? Avalonia.Media.Brushes.DarkRed
            : isWarning
                ? Avalonia.Media.Brushes.DarkGoldenrod
                : Avalonia.Media.Brushes.DarkSlateBlue;
    }
}
