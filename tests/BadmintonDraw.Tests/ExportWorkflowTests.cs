using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using BadmintonDraw.Workflows;
using ClosedXML.Excel;
using SkiaSharp;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed partial class DrawWorkflowTests
{
    [Fact]
    public void CrossEventWorkflowExportsMergedMaterialsWhenNoSevereConflict()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-merged-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "混双.szbd");
        var outputDirectory = Path.Combine(directory, "output");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    [new DrawParticipant("张三", PrimaryName: "张三"), new DrawParticipant("李四", PrimaryName: "李四")],
                    CreateSingleMatchSchedule("第1场", "张三", "李四", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1")));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "混双",
                    [
                        new DrawParticipant("[王五 赵六]", PrimaryName: "王五", PartnerName: "赵六"),
                        new DrawParticipant("[钱七 孙八]", PrimaryName: "钱七", PartnerName: "孙八")
                    ],
                    CreateSingleMatchSchedule("第1场", "[王五 赵六]", "[钱七 孙八]", new TimeOnly(15, 0), new TimeOnly(15, 30), "C1")));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 20);
            var result = workflow.ExportMergedScheduleMaterials(board, outputDirectory);

            Assert.Equal(0, board.Report.SevereCount);
            Assert.Equal(["2026-06-13"], result.DayLabels);
            Assert.Equal(6, result.OutputPaths.Count);
            Assert.NotEqual(outputDirectory, result.OutputDirectory);
            Assert.True(Directory.Exists(result.OutputDirectory));
            Assert.All(result.OutputPaths, path => Assert.StartsWith(result.OutputDirectory, path, StringComparison.Ordinal));
            Assert.Contains(result.Schedule.Matches, match => match.MatchName == "男单 · 第1场");
            Assert.Contains(result.Schedule.Matches, match => match.MatchName == "混双 · 第1场");
            Assert.All(result.OutputPaths, path => Assert.True(File.Exists(path), path));
            Assert.Contains(result.OutputPaths, path => path.EndsWith("多项目排程检查报告.xlsx", StringComparison.Ordinal));
            Assert.Contains(result.OutputPaths, path => path.EndsWith("合并赛程记录表.xlsx", StringComparison.Ordinal));
            Assert.Contains(result.OutputPaths, path => path.EndsWith("合并赛程安排表.pdf", StringComparison.Ordinal));
            Assert.Contains(result.OutputPaths, path => path.EndsWith("合并单场比赛计分表.pdf", StringComparison.Ordinal));
            var manifestPath = Assert.Single(result.OutputPaths, path => path.EndsWith("合并材料包说明.txt", StringComparison.Ordinal));
            Assert.Contains("多项目合并材料包", File.ReadAllText(manifestPath));
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventWorkflowRejectsMergedMaterialsWithSevereConflict()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-merged-conflict-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "混双.szbd");
        var outputDirectory = Path.Combine(directory, "output");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    [new DrawParticipant("张三", PrimaryName: "张三"), new DrawParticipant("李四", PrimaryName: "李四")],
                    CreateSingleMatchSchedule("第1场", "张三", "李四", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1")));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "混双",
                    [
                        new DrawParticipant("[张三 郑九]", PrimaryName: "张三", PartnerName: "郑九"),
                        new DrawParticipant("[钱十 吴一]", PrimaryName: "钱十", PartnerName: "吴一")
                    ],
                    CreateSingleMatchSchedule("第1场", "[张三 郑九]", "[钱十 吴一]", new TimeOnly(14, 10), new TimeOnly(14, 40), "C1")));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 20);
            var exception = Assert.Throws<DrawValidationException>(() => workflow.ExportMergedScheduleMaterials(board, outputDirectory));

            Assert.Equal(1, board.Report.SevereCount);
            Assert.Contains("严重冲突", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventMergedRecordKeepsWinnerReferenceFormulas()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-formula-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "混双.szbd");
        var outputDirectory = Path.Combine(directory, "output");

        try
        {
            Directory.CreateDirectory(directory);
            var singlesSchedule = new SchedulePlan(
                [
                    new ScheduledMatch(
                        1,
                        "2026-06-13",
                        new TimeOnly(14, 0),
                        new TimeOnly(14, 30),
                        "B1",
                        1,
                        "A组",
                        "首轮赛",
                        "A组首轮赛1",
                        "张三",
                        "李四",
                        MatchId: "s1"),
                    new ScheduledMatch(
                        2,
                        "2026-06-13",
                        new TimeOnly(14, 30),
                        new TimeOnly(15, 0),
                        "B1",
                        1,
                        "A组",
                        "决赛",
                        "A组决赛1",
                        "A组首轮赛1胜者",
                        "王五",
                        MatchId: "s2",
                        Dependencies: [Dependency("s1", "A组首轮赛1", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)])
                ],
                new ScheduleSettings(
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "C1"])],
                    MatchMinutes: 30,
                    MaxMatchesPerEntrantPerDay: 2));
            var store = new TournamentProgressStore();
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    [
                        new DrawParticipant("张三", PrimaryName: "张三"),
                        new DrawParticipant("李四", PrimaryName: "李四"),
                        new DrawParticipant("王五", PrimaryName: "王五")
                    ],
                    singlesSchedule));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "混双",
                    [
                        new DrawParticipant("[钱七 孙八]", PrimaryName: "钱七", PartnerName: "孙八"),
                        new DrawParticipant("[周九 吴十]", PrimaryName: "周九", PartnerName: "吴十")
                    ],
                    CreateSingleMatchSchedule("混双1", "[钱七 孙八]", "[周九 吴十]", new TimeOnly(15, 30), new TimeOnly(16, 0), "C1")));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 20);
            var result = workflow.ExportMergedScheduleMaterials(board, outputDirectory);
            var mergedFinal = result.Schedule.Matches.Single(match => match.MatchName == "男单 · A组决赛1");
            var recordPath = result.OutputPaths.Single(path => path.EndsWith("合并赛程记录表.xlsx", StringComparison.Ordinal));

            Assert.Equal("男单 · A组首轮赛1胜者", mergedFinal.SideA);

            using var workbook = new XLWorkbook(recordPath);
            var sheet = workbook.Worksheet("对阵记录表");
            var finalRow = sheet.RowsUsed()
                .Single(row => row.Cell(14).GetString() == "男单 · A组决赛1")
                .RowNumber();

            Assert.Contains("$L$", sheet.Cell(finalRow, 15).FormulaA1, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SUBSTITUTE", sheet.Cell(finalRow, 6).FormulaA1, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventMergedSchedulePrefixesOutcomeReferencesForDuplicateMatchNames()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-duplicate-references-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "混双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var singlesSchedule = new SchedulePlan(
                [
                    new ScheduledMatch(1, "2026-06-13", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1", 1, "A组", "首轮赛", "第1场", "张三", "李四", MatchId: "singles-1"),
                    new ScheduledMatch(
                        2,
                        "2026-06-13",
                        new TimeOnly(14, 30),
                        new TimeOnly(15, 0),
                        "B1",
                        1,
                        "A组",
                        "决赛",
                        "决赛1",
                        "第1场胜者",
                        "王五",
                        MatchId: "singles-2",
                        Dependencies: [Dependency("singles-1", "第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)])
                ],
                new ScheduleSettings(
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "C1"])],
                    MatchMinutes: 30,
                    MaxMatchesPerEntrantPerDay: 2));
            var mixedSchedule = new SchedulePlan(
                [
                    new ScheduledMatch(1, "2026-06-13", new TimeOnly(15, 0), new TimeOnly(15, 30), "C1", 1, "A组", "首轮赛", "第1场", "[赵六 钱七]", "[孙八 周九]", MatchId: "mixed-1"),
                    new ScheduledMatch(
                        2,
                        "2026-06-13",
                        new TimeOnly(15, 30),
                        new TimeOnly(16, 0),
                        "C1",
                        1,
                        "A组",
                        "决赛",
                        "决赛1",
                        "第1场胜者",
                        "[吴十 郑一]",
                        MatchId: "mixed-2",
                        Dependencies: [Dependency("mixed-1", "第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)])
                ],
                new ScheduleSettings(
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "C1"])],
                    MatchMinutes: 30,
                    MaxMatchesPerEntrantPerDay: 2));
            var store = new TournamentProgressStore();
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    [
                        new DrawParticipant("张三", PrimaryName: "张三"),
                        new DrawParticipant("李四", PrimaryName: "李四"),
                        new DrawParticipant("王五", PrimaryName: "王五")
                    ],
                    singlesSchedule));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "混双",
                    [
                        new DrawParticipant("[赵六 钱七]", PrimaryName: "赵六", PartnerName: "钱七"),
                        new DrawParticipant("[孙八 周九]", PrimaryName: "孙八", PartnerName: "周九"),
                        new DrawParticipant("[吴十 郑一]", PrimaryName: "吴十", PartnerName: "郑一")
                    ],
                    mixedSchedule));

            var board = new CrossEventConflictWorkflow().LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 20);
            var mergedSchedule = CrossEventConflictWorkflow.BuildMergedSchedulePlan(board);
            var singlesFinal = mergedSchedule.Matches.Single(match => match.MatchName == "男单 · 决赛1");
            var mixedFinal = mergedSchedule.Matches.Single(match => match.MatchName == "混双 · 决赛1");

            Assert.Equal(0, board.Report.SevereCount);
            Assert.Equal("男单 · 第1场胜者", singlesFinal.SideA);
            Assert.Equal("混双 · 第1场胜者", mixedFinal.SideA);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void WorkflowDefaultFileNamesKeepCompetitionMetadata()
    {
        var participants = CreateParticipants(32);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 8,
            seed: "SZUBA-20260611-1234",
            mode: CompetitionMode.SinglesKnockout,
            eventKind: EventKind.Doubles,
            knockoutGoal: KnockoutGoal.Champion,
            placementPlayoff: PlacementPlayoff.ThirdToEighth));

        var drawName = DrawWorkflow.BuildDefaultDrawFileName(
            result,
            "深大羽协虚拟双打参赛名单_32人.xlsx",
            WorkflowExportFormat.All);
        var scheduleName = ScheduleWorkflow.BuildDefaultScheduleFileName(
            result,
            "深大羽协虚拟双打参赛名单_32人.xlsx",
            WorkflowExportFormat.Excel);

        Assert.Contains("深大羽协虚拟双打", drawName);
        Assert.Contains("淘汰赛", drawName);
        Assert.Contains("双打32对", drawName);
        Assert.Contains("决出冠军", drawName);
        Assert.Contains("排3-8名", drawName);
        Assert.Contains("seed1234", drawName);
        Assert.EndsWith(".xlsx", drawName);
        Assert.Contains("赛程表", scheduleName);
        Assert.Contains("排3-8名", scheduleName);
    }

    [Fact]
    public void ScheduleExcelWriterExportsDetailAndGridSheets()
    {
        var participants = CreateParticipants(6);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 2,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(["A1", "A2"], new TimeOnly(14, 0), new TimeOnly(18, 0), MatchMinutes: 30));
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-schedule-{Guid.NewGuid():N}.xlsx");

        try
        {
            new ScheduleExcelWriter().Write(outputPath, schedule);

            using var workbook = new XLWorkbook(outputPath);
            Assert.Contains(workbook.Worksheets, worksheet => worksheet.Name == "赛程明细");
            Assert.Contains(workbook.Worksheets, worksheet => worksheet.Name == "时间场地网格");
            Assert.Contains(workbook.Worksheets, worksheet => worksheet.Name == "对阵记录表");
            Assert.Contains("比赛赛程明细表", workbook.Worksheet("赛程明细").Cell(1, 1).GetString());
            Assert.Equal("时间", workbook.Worksheet("时间场地网格").Cell(2, 1).GetString());
            var recordSheet = workbook.Worksheet("对阵记录表");
            Assert.Equal("对阵数据", recordSheet.Cell(4, 6).GetString());
            Assert.Equal("比分", recordSheet.Cell(4, 9).GetString());
            Assert.Equal("用时", recordSheet.Cell(4, 10).GetString());
            Assert.Equal("胜方", recordSheet.Cell(4, 12).GetString());
            Assert.Equal("示例", recordSheet.Cell(5, 1).GetString());
            Assert.Equal("15-10, 15-12", recordSheet.Cell(5, 9).GetString());
            Assert.Equal("vs", recordSheet.Cell(6, 7).GetString());
            Assert.True(string.IsNullOrWhiteSpace(recordSheet.Cell(6, 9).GetString()));
            Assert.True(string.IsNullOrWhiteSpace(recordSheet.Cell(6, 10).GetString()));
            Assert.True(string.IsNullOrWhiteSpace(recordSheet.Cell(6, 12).GetString()));
            var recordLastRow = schedule.Matches.Count + 5;
            Assert.Equal(
                schedule.Matches.Count,
                recordSheet.Range(6, 14, recordLastRow, 14).Cells().Count(cell => !string.IsNullOrWhiteSpace(cell.GetString())));
            Assert.Contains(
                "胜者",
                string.Join('\n', recordSheet.Range(6, 6, recordLastRow, 8).Cells().Select(cell => cell.GetString())),
                StringComparison.Ordinal);
            var hiddenFormulaText = string.Join(
                '\n',
                recordSheet.Range(6, 15, recordLastRow, 16).Cells().Select(cell => cell.FormulaA1));
            Assert.Contains("$L$", hiddenFormulaText, StringComparison.OrdinalIgnoreCase);
            Assert.True(recordSheet.Column(14).IsHidden);
            Assert.True(recordSheet.Column(15).IsHidden);
            Assert.True(recordSheet.Column(16).IsHidden);
            Assert.Contains("$O$6:$P$6", recordSheet.Cell(6, 12).GetDataValidation().Value, StringComparison.OrdinalIgnoreCase);
            var gridSheet = workbook.Worksheet("时间场地网格");
            var gridText = string.Join('\n', gridSheet.CellsUsed().Select(cell => cell.GetString()));
            var playInCell = gridSheet.CellsUsed()
                .First(cell => cell.GetString().Contains("首轮赛", StringComparison.Ordinal));
            var mainDrawCell = gridSheet.CellsUsed()
                .First(cell => Regex.IsMatch(cell.GetString(), @"\d+进\d+"));

            Assert.NotEqual(
                playInCell.Style.Fill.BackgroundColor.Color.ToArgb(),
                mainDrawCell.Style.Fill.BackgroundColor.Color.ToArgb());
            Assert.True(playInCell.WorksheetRow().Height >= 70);
            Assert.Matches(@"\d{2}:\d{2}-\d{2}:\d{2} A\d胜", gridText);
            Assert.DoesNotContain("首轮赛1胜者", gridText, StringComparison.Ordinal);
            Assert.Contains(
                "首轮赛1胜者",
                string.Join('\n', workbook.Worksheet("赛程明细").CellsUsed().Select(cell => cell.GetString())));
            Assert.Contains(
                "A1",
                string.Join('\n', workbook.Worksheet("赛程参数").CellsUsed().Select(cell => cell.GetString())));
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void DrawWorkflowGeneratesAndExportsExcel()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"badminton-workflow-input-{Guid.NewGuid():N}.xlsx");
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-workflow-output-{Guid.NewGuid():N}.xlsx");

        try
        {
            WriteParticipantRowsWorkbook(
                inputPath,
                new ParticipantWorkbookRow("张三", TeamName: "学院A"),
                new ParticipantWorkbookRow("李四", TeamName: "学院B"),
                new ParticipantWorkbookRow("王五", TeamName: "学院C"),
                new ParticipantWorkbookRow("赵六", TeamName: "学院D"));

            var workflow = new DrawWorkflow();
            var result = workflow.Generate(new DrawWorkflowRequest(
                inputPath,
                CompetitionMode.SinglesKnockout,
                EventKind.Singles,
                GroupCount: 1,
                RandomSeed: "workflow-seed",
                KnockoutGoal: KnockoutGoal.Champion,
                PlacementPlayoff: PlacementPlayoff.None));
            workflow.ExportExcel(outputPath, result);

            Assert.Equal(4, result.Result.Audit.ParticipantCount);
            Assert.Empty(result.WarningMessages);
            AssertFileHeader(outputPath, [0x50, 0x4B, 0x03, 0x04]);
        }
        finally
        {
            DeleteIfExists(inputPath);
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void VisualWriterFallsBackToInstalledFontForChineseText()
    {
        var typeface = DrawResultVisualWriter.ResolveTypefaceForText(
            "Definitely Missing Font",
            isBold: false,
            "14:00-14:20\n深大羽协赛程安排表\nA组128进64第1场");

        Assert.True(DrawResultVisualWriter.HasEmbeddedExportTypeface);
        Assert.Contains("Noto", typeface.FamilyName, StringComparison.OrdinalIgnoreCase);
        Assert.True(typeface.ContainsGlyphs("深大羽协赛程安排表A组128进64第1场"));
    }

    [Fact]
    public void WorkflowsExportAllScheduleAndTimedBracketFormats()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout,
            knockoutGoal: KnockoutGoal.Champion));
        var workflowResult = new DrawWorkflowResult(result, participants, [], []);
        var scheduleWorkflow = new ScheduleWorkflow();
        var schedule = scheduleWorkflow.Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1", "A2"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1", "A2"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"badminton-workflow-export-matrix-{Guid.NewGuid():N}");
        var scheduleBasePath = Path.Combine(outputDirectory, "赛程表.xlsx");
        var timedBracketBasePath = Path.Combine(outputDirectory, "赛程表_带比赛时间和场地对阵表.xlsx");

        try
        {
            Directory.CreateDirectory(outputDirectory);

            var schedulePaths = scheduleWorkflow.ExportFiles(
                scheduleBasePath,
                WorkflowExportFormat.All,
                schedule);
            var timedBracketPaths = scheduleWorkflow.ExportTimedBracketFiles(
                scheduleBasePath,
                WorkflowExportFormat.All,
                workflowResult,
                schedule,
                new DrawResultVisualOptions(PdfRows: 1, PdfColumns: 2));

            AssertWorkflowExportSet(schedulePaths, scheduleBasePath, expectedPdfPages: schedule.DayCount);
            AssertWorkflowExportSet(timedBracketPaths, timedBracketBasePath, expectedPdfPages: 2);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }
}
