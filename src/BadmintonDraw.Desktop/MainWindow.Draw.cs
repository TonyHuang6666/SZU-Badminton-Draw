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
            Content = "继续",
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
}
