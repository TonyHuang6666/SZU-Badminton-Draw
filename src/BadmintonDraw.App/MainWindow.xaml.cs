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
    private readonly ParticipantTemplateWriter _templateWriter = new();
    private IReadOnlyList<DrawParticipant> _participants = Array.Empty<DrawParticipant>();
    private DrawResult? _latestResult;

    public MainWindow()
    {
        InitializeComponent();
        SeedBox.Text = GenerateSeed();
        UpdateEventKindForMode();
    }

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Excel 文件 (*.xlsx)|*.xlsx|所有文件 (*.*)|*.*",
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
        TryGenerate();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGenerate())
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Excel 文件 (*.xlsx)|*.xlsx",
            FileName = $"抽签结果_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
            Title = "保存抽签结果"
        };

        if (dialog.ShowDialog(this) == true && _latestResult is not null)
        {
            _writer.Write(dialog.FileName, _latestResult, _participants);
            SetStatus($"已导出：{dialog.FileName}");
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
            _participants = _reader.ReadParticipants(InputPathBox.Text, GetEventKind());
            SummaryText.Text = $"已导入 {_participants.Count} 个参赛单位，其中 {_participants.Count(participant => participant.IsSeed)} 个种子。";
            SetStatus("名单导入成功，可以预览或导出。");
            return true;
        }
        catch (Exception ex) when (ex is ExcelImportException or DrawValidationException or IOException)
        {
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

            if (_participants.Count == 0 && !TryLoadParticipants())
            {
                return false;
            }

            if (!int.TryParse(GroupCountBox.Text.Trim(), out var groupCount))
            {
                throw new DrawValidationException("小组数必须是数字。");
            }

            var settings = new DrawSettings(
                GetCompetitionMode(),
                GetEventKind(),
                groupCount,
                SeedBox.Text);

            _participants = _reader.ReadParticipants(InputPathBox.Text, settings.EventKind);
            _latestResult = _drawService.Generate(_participants, settings);

            GroupsGrid.ItemsSource = ToRows(_latestResult.Groups);
            RoundOneGrid.ItemsSource = ToRows(_latestResult.RoundOneGroups);
            ByeGrid.ItemsSource = ToRows(_latestResult.ByeGroups);
            SummaryText.Text = $"已生成 {_latestResult.Groups.Count} 个小组，参赛单位 {_latestResult.Audit.ParticipantCount} 个，随机种子：{_latestResult.Audit.RandomSeed}";
            SetStatus("抽签预览已生成。");
            return true;
        }
        catch (Exception ex) when (ex is ExcelImportException or DrawValidationException or IOException or InvalidOperationException)
        {
            SetStatus(ex.Message, isError: true);
            return false;
        }
    }

    private CompetitionMode GetCompetitionMode()
    {
        return Enum.Parse<CompetitionMode>(GetSelectedTag(CompetitionModeBox));
    }

    private EventKind GetEventKind()
    {
        return Enum.Parse<EventKind>(GetSelectedTag(EventKindBox));
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

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? System.Windows.Media.Brushes.DarkRed
            : System.Windows.Media.Brushes.DarkSlateBlue;
    }

    private sealed record ResultRow(string GroupName, int Order, string Name, string SeedLabel);
}
