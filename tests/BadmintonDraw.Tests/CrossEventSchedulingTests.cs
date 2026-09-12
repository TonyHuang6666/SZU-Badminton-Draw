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
    public void CrossEventConflictDetectorClassifiesSeverity()
    {
        var firstSource = CreateCrossEventSource(
            "男单",
            CreateCrossEventMatch(1, "男单1", "张三", "李四", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1", ["张三"], ["李四"]),
            CreateCrossEventMatch(2, "男单2", "王五", "赵六", new TimeOnly(15, 0), new TimeOnly(15, 30), "B2", ["王五"], ["赵六"]),
            CreateCrossEventMatch(3, "男单3", "孙七", "周八", new TimeOnly(17, 0), new TimeOnly(17, 30), "B3", ["孙七"], ["周八"]));
        var secondSource = CreateCrossEventSource(
            "混双",
            CreateCrossEventMatch(1, "混双1", "[张三 郑九]", "[钱十 吴一]", new TimeOnly(14, 10), new TimeOnly(14, 40), "C1", ["张三", "郑九"], ["钱十", "吴一"]),
            CreateCrossEventMatch(2, "混双2", "[王五 李二]", "[赵三 陈四]", new TimeOnly(15, 40), new TimeOnly(16, 10), "C2", ["王五", "李二"], ["赵三", "陈四"]),
            CreateCrossEventMatch(3, "混双3", "[孙七 胡五]", "[朱六 高八]", new TimeOnly(18, 0), new TimeOnly(18, 30), "C3", ["孙七", "胡五"], ["朱六", "高八"]));

        var report = new CrossEventConflictDetector().Analyze([firstSource, secondSource], minimumRestMinutes: 20);

        Assert.Equal(1, report.SevereCount);
        Assert.Equal(1, report.WarningCount);
        Assert.Equal(1, report.NoticeCount);
        Assert.Contains(report.Issues, issue =>
            issue.Severity == CrossEventConflictSeverity.Severe
            && issue.PlayerName == "张三"
            && issue.RestMinutes is null);
        Assert.Contains(report.Issues, issue =>
            issue.Severity == CrossEventConflictSeverity.Warning
            && issue.PlayerName == "王五"
            && issue.RestMinutes == 10);
        Assert.Contains(report.Issues, issue =>
            issue.Severity == CrossEventConflictSeverity.Notice
            && issue.PlayerName == "孙七"
            && issue.RestMinutes == 30);
    }

    [Fact]
    public void CrossEventConflictDetectorUsesStudentIdBeforeName()
    {
        var firstSource = CreateCrossEventSource(
            "男单",
            CreateCrossEventMatch(
                1,
                "男单1",
                "张三",
                "李四",
                new TimeOnly(14, 0),
                new TimeOnly(14, 30),
                "B1",
                ["张三"],
                ["李四"],
                [new CrossEventPlayerIdentity("张三", "20260001")],
                [new CrossEventPlayerIdentity("李四", "20260002")]));
        var differentStudentSource = CreateCrossEventSource(
            "混双",
            CreateCrossEventMatch(
                1,
                "混双1",
                "[张三 郑九]",
                "[钱十 吴一]",
                new TimeOnly(14, 10),
                new TimeOnly(14, 40),
                "C1",
                ["张三", "郑九"],
                ["钱十", "吴一"],
                [new CrossEventPlayerIdentity("张三", "20260099"), new CrossEventPlayerIdentity("郑九", "20260003")],
                [new CrossEventPlayerIdentity("钱十", "20260004"), new CrossEventPlayerIdentity("吴一", "20260005")]));
        var sameStudentSource = CreateCrossEventSource(
            "男双",
            CreateCrossEventMatch(
                1,
                "男双1",
                "[张三 王五]",
                "[赵六 孙七]",
                new TimeOnly(14, 10),
                new TimeOnly(14, 40),
                "D1",
                ["张三", "王五"],
                ["赵六", "孙七"],
                [new CrossEventPlayerIdentity("张三", "20260001"), new CrossEventPlayerIdentity("王五", "20260006")],
                [new CrossEventPlayerIdentity("赵六", "20260007"), new CrossEventPlayerIdentity("孙七", "20260008")]));

        var differentStudentReport = new CrossEventConflictDetector().Analyze([firstSource, differentStudentSource], minimumRestMinutes: 20);
        var sameStudentReport = new CrossEventConflictDetector().Analyze([firstSource, sameStudentSource], minimumRestMinutes: 20);

        Assert.Equal(0, differentStudentReport.SevereCount);
        Assert.DoesNotContain(differentStudentReport.Issues, issue => issue.PlayerName.StartsWith("张三", StringComparison.Ordinal));
        Assert.Equal(1, sameStudentReport.SevereCount);
        Assert.Contains(sameStudentReport.Issues, issue =>
            issue.Severity == CrossEventConflictSeverity.Severe
            && issue.PlayerName == "张三（20260001）");
    }

    [Fact]
    public void CrossEventConflictDetectorFlagsCrossEventDailyLoadOverLimit()
    {
        var firstSource = CreateCrossEventSource(
            "男单",
            Enumerable.Range(1, 4)
                .Select(index => CreateCrossEventMatch(
                    index,
                    $"男单{index}",
                    "张三",
                    $"男单对手{index}",
                    new TimeOnly(14, 0).AddMinutes((index - 1) * 20),
                    new TimeOnly(14, 20).AddMinutes((index - 1) * 20),
                    "B1",
                    ["张三"],
                    [$"男单对手{index}"]))
                .ToArray());
        var secondSource = CreateCrossEventSource(
            "男双",
            Enumerable.Range(1, 3)
                .Select(index => CreateCrossEventMatch(
                    index,
                    $"男双{index}",
                    "张三",
                    $"男双对手{index}",
                    new TimeOnly(16, 0).AddMinutes((index - 1) * 20),
                    new TimeOnly(16, 20).AddMinutes((index - 1) * 20),
                    "B2",
                    ["张三"],
                    [$"男双对手{index}"]))
                .ToArray());

        var report = new CrossEventConflictDetector().Analyze([firstSource, secondSource], minimumRestMinutes: 0);

        Assert.Equal(1, report.WarningCount);
        Assert.Contains(report.Issues, issue =>
            issue.Severity == CrossEventConflictSeverity.Warning
            && issue.PlayerName == "张三"
            && issue.Detail.Contains("跨项目累计 7 场", StringComparison.Ordinal)
            && issue.Detail.Contains("每日最多 6 场", StringComparison.Ordinal));
    }

    [Fact]
    public void CrossEventScheduleBoardReportsProbabilisticCrossEventDailyLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-load-forecast-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "男双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            var sharedPlayer = new DrawParticipant("张三", PrimaryName: "张三", PrimaryStudentId: "20260001");
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    [sharedPlayer, new DrawParticipant("李四", PrimaryName: "李四", PrimaryStudentId: "20260002")],
                    CreateTop16PlacementSchedule("男单", "张三", "20260001", new TimeOnly(14, 0), "B")));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "男双",
                    [sharedPlayer, new DrawParticipant("王五", PrimaryName: "王五", PrimaryStudentId: "20260003")],
                    CreateTop8PlacementSchedule("男双", "张三", "20260001", new TimeOnly(17, 0), "C")));

            var board = new CrossEventConflictWorkflow().LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 20);

            var issue = Assert.Single(board.Report.Issues, issue =>
                issue.Severity == CrossEventConflictSeverity.Notice
                && issue.PlayerName == "张三（20260001）"
                && issue.Detail.StartsWith("负荷推演", StringComparison.Ordinal));
            Assert.Contains("最高可能 7/6 场", issue.Detail);
            Assert.Contains("概率约 50%", issue.Detail);
            Assert.Contains("4场 50%", issue.Detail);
            Assert.Contains("7场 50%", issue.Detail);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }

        static SchedulePlan CreateTop16PlacementSchedule(
            string eventName,
            string playerName,
            string studentId,
            TimeOnly startTime,
            string courtPrefix)
        {
            var player = new CrossEventPlayerIdentity(playerName, studentId);
            return new SchedulePlan(
                [
                    new ScheduledMatch(1, "2026-06-13", startTime, startTime.AddMinutes(20), $"{courtPrefix}1", 1, "A组", "16进8", $"{eventName}16进8第1场", playerName, "李四", MatchId: "top16-m1", SideAPlayerIdentities: [player]),
                    new ScheduledMatch(2, "2026-06-13", startTime.AddMinutes(40), startTime.AddMinutes(60), $"{courtPrefix}1", 1, "A组", "8进4", $"{eventName}8进4第1场", $"{eventName}16进8第1场胜者", "王五", MatchId: "top16-m2", Dependencies: [Dependency("top16-m1", $"{eventName}16进8第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                    new ScheduledMatch(3, "2026-06-13", startTime.AddMinutes(80), startTime.AddMinutes(100), $"{courtPrefix}1", 1, "A组", "半决赛", $"{eventName}半决赛第1场", $"{eventName}8进4第1场胜者", "赵六", MatchId: "top16-m3", Dependencies: [Dependency("top16-m2", $"{eventName}8进4第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                    new ScheduledMatch(4, "2026-06-13", startTime.AddMinutes(120), startTime.AddMinutes(140), $"{courtPrefix}1", 1, "A组", "决赛", $"{eventName}决赛第1场", $"{eventName}半决赛第1场胜者", "钱七", MatchId: "top16-m4", Dependencies: [Dependency("top16-m3", $"{eventName}半决赛第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                    new ScheduledMatch(5, "2026-06-13", startTime.AddMinutes(120), startTime.AddMinutes(140), $"{courtPrefix}2", 1, "A组", "3/4名赛", $"{eventName}3/4名赛", $"{eventName}半决赛第1场负者", "孙八", MatchId: "top16-m5", Dependencies: [Dependency("top16-m3", $"{eventName}半决赛第1场", ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA)]),
                    new ScheduledMatch(6, "2026-06-13", startTime.AddMinutes(80), startTime.AddMinutes(100), $"{courtPrefix}3", 1, "A组", "5-8名半决赛", $"{eventName}5-8名半决赛第1场", $"{eventName}8进4第1场负者", "周九", MatchId: "top16-m6", Dependencies: [Dependency("top16-m2", $"{eventName}8进4第1场", ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA)]),
                    new ScheduledMatch(7, "2026-06-13", startTime.AddMinutes(120), startTime.AddMinutes(140), $"{courtPrefix}3", 1, "A组", "5/6名赛", $"{eventName}5/6名赛", $"{eventName}5-8名半决赛第1场胜者", "吴十", MatchId: "top16-m7", Dependencies: [Dependency("top16-m6", $"{eventName}5-8名半决赛第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                    new ScheduledMatch(8, "2026-06-13", startTime.AddMinutes(120), startTime.AddMinutes(140), $"{courtPrefix}4", 1, "A组", "7/8名赛", $"{eventName}7/8名赛", $"{eventName}5-8名半决赛第1场负者", "郑一", MatchId: "top16-m8", Dependencies: [Dependency("top16-m6", $"{eventName}5-8名半决赛第1场", ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA)])
                ],
                new ScheduleSettings(
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(20, 0), [$"{courtPrefix}1", $"{courtPrefix}2", $"{courtPrefix}3", $"{courtPrefix}4"])],
                    MatchMinutes: 20,
                    MaxMatchesPerEntrantPerDay: 4));
        }

        static SchedulePlan CreateTop8PlacementSchedule(
            string eventName,
            string playerName,
            string studentId,
            TimeOnly startTime,
            string courtPrefix)
        {
            var player = new CrossEventPlayerIdentity(playerName, studentId);
            return new SchedulePlan(
                [
                    new ScheduledMatch(1, "2026-06-13", startTime, startTime.AddMinutes(20), $"{courtPrefix}1", 1, "A组", "8进4", $"{eventName}8进4第1场", playerName, "李四", MatchId: "top8-m1", SideAPlayerIdentities: [player]),
                    new ScheduledMatch(2, "2026-06-13", startTime.AddMinutes(40), startTime.AddMinutes(60), $"{courtPrefix}1", 1, "A组", "半决赛", $"{eventName}半决赛第1场", $"{eventName}8进4第1场胜者", "王五", MatchId: "top8-m2", Dependencies: [Dependency("top8-m1", $"{eventName}8进4第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                    new ScheduledMatch(3, "2026-06-13", startTime.AddMinutes(80), startTime.AddMinutes(100), $"{courtPrefix}1", 1, "A组", "决赛", $"{eventName}决赛第1场", $"{eventName}半决赛第1场胜者", "赵六", MatchId: "top8-m3", Dependencies: [Dependency("top8-m2", $"{eventName}半决赛第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                    new ScheduledMatch(4, "2026-06-13", startTime.AddMinutes(80), startTime.AddMinutes(100), $"{courtPrefix}2", 1, "A组", "3/4名赛", $"{eventName}3/4名赛", $"{eventName}半决赛第1场负者", "钱七", MatchId: "top8-m4", Dependencies: [Dependency("top8-m2", $"{eventName}半决赛第1场", ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA)]),
                    new ScheduledMatch(5, "2026-06-13", startTime.AddMinutes(40), startTime.AddMinutes(60), $"{courtPrefix}3", 1, "A组", "5-8名半决赛", $"{eventName}5-8名半决赛第1场", $"{eventName}8进4第1场负者", "孙八", MatchId: "top8-m5", Dependencies: [Dependency("top8-m1", $"{eventName}8进4第1场", ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA)]),
                    new ScheduledMatch(6, "2026-06-13", startTime.AddMinutes(80), startTime.AddMinutes(100), $"{courtPrefix}3", 1, "A组", "5/6名赛", $"{eventName}5/6名赛", $"{eventName}5-8名半决赛第1场胜者", "周九", MatchId: "top8-m6", Dependencies: [Dependency("top8-m5", $"{eventName}5-8名半决赛第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                    new ScheduledMatch(7, "2026-06-13", startTime.AddMinutes(80), startTime.AddMinutes(100), $"{courtPrefix}4", 1, "A组", "7/8名赛", $"{eventName}7/8名赛", $"{eventName}5-8名半决赛第1场负者", "吴十", MatchId: "top8-m7", Dependencies: [Dependency("top8-m5", $"{eventName}5-8名半决赛第1场", ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA)])
                ],
                new ScheduleSettings(
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(20, 0), [$"{courtPrefix}1", $"{courtPrefix}2", $"{courtPrefix}3", $"{courtPrefix}4"])],
                    MatchMinutes: 20,
                    MaxMatchesPerEntrantPerDay: 4));
        }
    }

    [Fact]
    public void CrossEventConflictWorkflowExportsProgressReportWorkbook()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "混双.szbd");
        var reportPath = Path.Combine(directory, "多项目排程检查报告.xlsx");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    [new DrawParticipant("张三", PrimaryName: "张三"), new DrawParticipant("李四", PrimaryName: "李四")],
                    CreateSingleMatchSchedule("男单1", "张三", "李四", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1")));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "混双",
                    [
                        new DrawParticipant("[张三 郑九]", PrimaryName: "张三", PartnerName: "郑九"),
                        new DrawParticipant("[钱十 吴一]", PrimaryName: "钱十", PartnerName: "吴一")
                    ],
                    CreateSingleMatchSchedule("混双1", "[张三 郑九]", "[钱十 吴一]", new TimeOnly(14, 10), new TimeOnly(14, 40), "C1")));

            var result = new CrossEventConflictWorkflow().ExportProgressReport(
                [firstProgressPath, secondProgressPath],
                reportPath,
                minimumRestMinutes: 20);

            Assert.Equal(1, result.Report.SevereCount);
            AssertFileHeader(reportPath, [0x50, 0x4B, 0x03, 0x04]);
            using var workbook = new XLWorkbook(reportPath);
            Assert.Contains("检查总览", workbook.Worksheets.Select(sheet => sheet.Name));
            Assert.Contains("当前赛程卡片", workbook.Worksheets.Select(sheet => sheet.Name));
            Assert.Contains("严重冲突", workbook.Worksheets.Select(sheet => sheet.Name));
            Assert.Contains("输入赛事", workbook.Worksheets.Select(sheet => sheet.Name));
            Assert.Equal("多项目排程检查报告", workbook.Worksheet("检查总览").Cell(1, 1).GetString());
            Assert.Equal("严重冲突", workbook.Worksheet("严重冲突").Cell(2, 1).GetString());
            Assert.Equal("张三", workbook.Worksheet("严重冲突").Cell(2, 2).GetString());
            Assert.Equal("需调整", workbook.Worksheet("当前赛程卡片").Cell(2, 1).GetString());
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventConflictWorkflowReadsStudentIdsFromProgressParticipants()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-ids-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var differentStudentProgressPath = Path.Combine(directory, "混双-不同张三.szbd");
        var sameStudentProgressPath = Path.Combine(directory, "男双-同一张三.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    [
                        new DrawParticipant("张三", PrimaryName: "张三", PrimaryStudentId: "20260001"),
                        new DrawParticipant("李四", PrimaryName: "李四", PrimaryStudentId: "20260002")
                    ],
                    CreateSingleMatchSchedule("男单1", "张三", "李四", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1")));
            store.Create(
                differentStudentProgressPath,
                CreateManualProgressSnapshot(
                    "混双",
                    [
                        new DrawParticipant("[张三 郑九]", PrimaryName: "张三", PartnerName: "郑九", PrimaryStudentId: "20260099", PartnerStudentId: "20260003"),
                        new DrawParticipant("[钱十 吴一]", PrimaryName: "钱十", PartnerName: "吴一", PrimaryStudentId: "20260004", PartnerStudentId: "20260005")
                    ],
                    CreateSingleMatchSchedule("混双1", "[张三 郑九]", "[钱十 吴一]", new TimeOnly(14, 10), new TimeOnly(14, 40), "C1")));
            store.Create(
                sameStudentProgressPath,
                CreateManualProgressSnapshot(
                    "男双",
                    [
                        new DrawParticipant("[张三 王五]", PrimaryName: "张三", PartnerName: "王五", PrimaryStudentId: "20260001", PartnerStudentId: "20260006"),
                        new DrawParticipant("[赵六 孙七]", PrimaryName: "赵六", PartnerName: "孙七", PrimaryStudentId: "20260007", PartnerStudentId: "20260008")
                    ],
                    CreateSingleMatchSchedule("男双1", "[张三 王五]", "[赵六 孙七]", new TimeOnly(14, 10), new TimeOnly(14, 40), "D1")));

            var workflow = new CrossEventConflictWorkflow();
            var differentStudentReport = workflow.AnalyzeProgressFiles([firstProgressPath, differentStudentProgressPath], minimumRestMinutes: 20);
            var sameStudentReport = workflow.AnalyzeProgressFiles([firstProgressPath, sameStudentProgressPath], minimumRestMinutes: 20);

            Assert.Equal(0, differentStudentReport.SevereCount);
            Assert.DoesNotContain(differentStudentReport.Issues, issue => issue.PlayerName.StartsWith("张三", StringComparison.Ordinal));
            Assert.Equal(1, sameStudentReport.SevereCount);
            Assert.Contains(sameStudentReport.Issues, issue =>
                issue.Severity == CrossEventConflictSeverity.Severe
                && issue.PlayerName == "张三（20260001）");
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventScheduleBoardMovesAndSavesAdjustedMatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-board-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "混双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    [new DrawParticipant("张三", PrimaryName: "张三"), new DrawParticipant("李四", PrimaryName: "李四")],
                    CreateSingleMatchSchedule("男单1", "张三", "李四", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1")));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "混双",
                    [
                        new DrawParticipant("[张三 郑九]", PrimaryName: "张三", PartnerName: "郑九"),
                        new DrawParticipant("[钱十 吴一]", PrimaryName: "钱十", PartnerName: "吴一")
                    ],
                    CreateSingleMatchSchedule("混双1", "[张三 郑九]", "[钱十 吴一]", new TimeOnly(14, 10), new TimeOnly(14, 40), "C1") with
                    {
                        Settings = new ScheduleSettings(
                            [
                                new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), ["C1"]),
                                new ScheduleDaySettings(new DateOnly(2026, 6, 14), new TimeOnly(14, 0), new TimeOnly(18, 0), ["C1"])
                            ],
                            MatchMinutes: 30,
                            MaxMatchesPerEntrantPerDay: 2)
                    }));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 20);
            var mixedDoublesKey = board.Items.Single(item => item.EventName == "混双").Key;
            var originalPlayer = board.PlayerDetails.Single(entry => entry.PlayerName == "张三");

            Assert.Equal(2, originalPlayer.EventCount);
            Assert.Equal(2, originalPlayer.MatchCount);
            Assert.Equal(1, originalPlayer.SevereIssueCount);
            Assert.Equal(0, originalPlayer.WarningIssueCount);
            Assert.Contains(
                originalPlayer.Appearances,
                appearance => appearance.EventName == "男单"
                              && appearance.ConflictSeverity == CrossEventConflictSeverity.Severe);

            var blockedValidation = workflow.ValidateScheduleItemMove(
                board,
                mixedDoublesKey,
                "2026-06-13",
                new TimeOnly(14, 0),
                "B1");
            Assert.False(blockedValidation.CanDrop);
            Assert.Equal(ScheduleBoardMoveValidationSeverity.Blocked, blockedValidation.Severity);
            Assert.Contains("不可放置", blockedValidation.Message);

            var allowedValidation = workflow.ValidateScheduleItemMove(
                board,
                mixedDoublesKey,
                "2026-06-14",
                new TimeOnly(14, 0),
                "C1");
            Assert.True(allowedValidation.CanDrop);
            Assert.Equal(ScheduleBoardMoveValidationSeverity.Allowed, allowedValidation.Severity);

            var adjusted = workflow.MoveScheduleItem(
                board,
                mixedDoublesKey,
                "2026-06-14",
                new TimeOnly(14, 0),
                "C1");
            var saveResult = workflow.SaveScheduleBoard(adjusted);
            var reopened = store.Read(secondProgressPath);
            var adjustedPlayer = adjusted.PlayerDetails.Single(entry => entry.PlayerName == "张三");

            Assert.Equal(0, adjusted.BlockingConflictItemCount);
            Assert.True(adjusted.HasUnsavedChanges);
            Assert.Equal(0, adjustedPlayer.SevereIssueCount);
            Assert.Equal(0, adjustedPlayer.WarningIssueCount);
            Assert.True(adjustedPlayer.ShortestRestMinutes is null or >= 20);
            Assert.Contains(
                adjustedPlayer.Appearances,
                appearance => appearance.EventName == "混双"
                              && appearance.DayLabel == "2026-06-14"
                              && appearance.StartTime == new TimeOnly(14, 0)
                              && appearance.ConflictSeverity is null);
            Assert.Equal(2, saveResult.UpdatedPaths.Count);
            Assert.Equal("2026-06-14", reopened.Snapshot.Schedule.Matches.Single().DayLabel);
            Assert.Equal(new TimeOnly(14, 0), reopened.Snapshot.Schedule.Matches.Single().StartTime);
            Assert.Equal(new TimeOnly(14, 30), reopened.Snapshot.Schedule.Matches.Single().EndTime);
            Assert.Contains("C1", reopened.Snapshot.Schedule.Settings.Days.Single(day => day.DayLabel == "2026-06-14").Courts);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventScheduleBoardAutoAdjustsSimpleConflict()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-auto-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "混双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    [new DrawParticipant("张三", PrimaryName: "张三"), new DrawParticipant("李四", PrimaryName: "李四")],
                    CreateSingleMatchSchedule("男单1", "张三", "李四", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1")));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "混双",
                    [
                        new DrawParticipant("[张三 郑九]", PrimaryName: "张三", PartnerName: "郑九"),
                        new DrawParticipant("[钱十 吴一]", PrimaryName: "钱十", PartnerName: "吴一")
                    ],
                    CreateSingleMatchSchedule("混双1", "[张三 郑九]", "[钱十 吴一]", new TimeOnly(14, 10), new TimeOnly(14, 40), "C1")));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 20);
            var adjusted = workflow.AutoAdjustScheduleBoard(board);

            Assert.True(adjusted.MovedCount > 0);
            Assert.Equal(0, adjusted.RemainingBlockingConflictItemCount);
            Assert.Empty(adjusted.Messages);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventAutoAdjustRespectsRefereeCountWhenCourtsAreAbundant()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-referees-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "男双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            var day = new ScheduleDaySettings(
                new DateOnly(2026, 6, 13),
                new TimeOnly(14, 0),
                new TimeOnly(16, 0),
                ["B1", "B2", "B3", "B4"]);
            var scheduleSettings = new ScheduleSettings([day], MatchMinutes: 20, MaxMatchesPerEntrantPerDay: 10);
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    CreateLooseParticipants("单", 4),
                    new SchedulePlan(
                        [
                            new ScheduledMatch(1, day.DayLabel, new TimeOnly(14, 0), new TimeOnly(14, 20), "B1", 1, "A组", "首轮赛", "男单1", "单选手1", "单选手2"),
                            new ScheduledMatch(2, day.DayLabel, new TimeOnly(14, 0), new TimeOnly(14, 20), "B2", 1, "A组", "首轮赛", "男单2", "单选手3", "单选手4")
                        ],
                        scheduleSettings)));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "男双",
                    CreateLooseParticipants("双", 4),
                    new SchedulePlan(
                        [
                            new ScheduledMatch(1, day.DayLabel, new TimeOnly(14, 0), new TimeOnly(14, 20), "B3", 1, "A组", "首轮赛", "男双1", "双选手1", "双选手2"),
                            new ScheduledMatch(2, day.DayLabel, new TimeOnly(14, 0), new TimeOnly(14, 20), "B4", 1, "A组", "首轮赛", "男双2", "双选手3", "双选手4")
                        ],
                        scheduleSettings)));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 0);
            var options = workflow.CreateSchedulingOptions(board, CrossEventSchedulingStrategy.BalancedRelaxed) with
            {
                RefereeCount = 1
            };
            var adjusted = workflow.AutoAdjustScheduleBoard(board, options).Board;

            Assert.Equal(1, adjusted.SchedulingOptions!.RefereeCount);
            foreach (var item in adjusted.Items)
            {
                var overlapping = adjusted.Items.Count(other =>
                    string.Equals(other.DayLabel, item.DayLabel, StringComparison.Ordinal)
                    && other.StartTime < item.EndTime
                    && item.StartTime < other.EndTime);
                Assert.True(overlapping <= 1);
            }
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventAutoAdjustAvoidsUnavailableCourtWindows()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-unavailable-court-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "男双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            var day = new ScheduleDaySettings(
                new DateOnly(2026, 6, 13),
                new TimeOnly(14, 0),
                new TimeOnly(16, 0),
                ["B1", "B2"],
                UnavailableCourtWindows:
                [
                    new ScheduleCourtAvailabilityBlock(new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1"])
                ]);
            var scheduleSettings = new ScheduleSettings([day], MatchMinutes: 20, MaxMatchesPerEntrantPerDay: 10);
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    CreateLooseParticipants("单", 4),
                    new SchedulePlan(
                        [
                            new ScheduledMatch(1, day.DayLabel, new TimeOnly(14, 0), new TimeOnly(14, 20), "B1", 1, "A组", "首轮赛", "男单1", "单选手1", "单选手2"),
                            new ScheduledMatch(2, day.DayLabel, new TimeOnly(14, 20), new TimeOnly(14, 40), "B1", 1, "A组", "首轮赛", "男单2", "单选手3", "单选手4")
                        ],
                        scheduleSettings)));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "男双",
                    CreateLooseParticipants("双", 4),
                    new SchedulePlan(
                        [
                            new ScheduledMatch(1, day.DayLabel, new TimeOnly(14, 40), new TimeOnly(15, 0), "B1", 1, "A组", "首轮赛", "男双1", "双选手1", "双选手2"),
                            new ScheduledMatch(2, day.DayLabel, new TimeOnly(15, 0), new TimeOnly(15, 20), "B1", 1, "A组", "首轮赛", "男双2", "双选手3", "双选手4")
                        ],
                        scheduleSettings)));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 0);
            var adjusted = workflow.AutoAdjustScheduleBoard(
                board,
                workflow.CreateSchedulingOptions(board, CrossEventSchedulingStrategy.BalancedRelaxed)).Board;

            Assert.NotNull(adjusted.QualityReport);
            Assert.All(
                adjusted.Items,
                item => Assert.False(
                    string.Equals(item.Court, "B1", StringComparison.OrdinalIgnoreCase)
                    && item.StartTime < new TimeOnly(16, 0)
                    && new TimeOnly(14, 0) < item.EndTime));
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventAutoAdjustRespectsTimeVaryingRefereeCapacity()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-referee-windows-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "男双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            var day = new ScheduleDaySettings(
                new DateOnly(2026, 6, 13),
                new TimeOnly(14, 0),
                new TimeOnly(16, 0),
                ["B1", "B2", "B3", "B4"],
                RefereeCapacityWindows:
                [
                    new ScheduleRefereeCapacityWindow(new TimeOnly(14, 0), new TimeOnly(15, 0), 1)
                ]);
            var scheduleSettings = new ScheduleSettings([day], MatchMinutes: 20, MaxMatchesPerEntrantPerDay: 10, RefereeCount: 4);
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    CreateLooseParticipants("单", 4),
                    new SchedulePlan(
                        [
                            new ScheduledMatch(1, day.DayLabel, new TimeOnly(14, 0), new TimeOnly(14, 20), "B1", 1, "A组", "首轮赛", "男单1", "单选手1", "单选手2"),
                            new ScheduledMatch(2, day.DayLabel, new TimeOnly(14, 0), new TimeOnly(14, 20), "B2", 1, "A组", "首轮赛", "男单2", "单选手3", "单选手4")
                        ],
                        scheduleSettings)));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "男双",
                    CreateLooseParticipants("双", 4),
                    new SchedulePlan(
                        [
                            new ScheduledMatch(1, day.DayLabel, new TimeOnly(14, 0), new TimeOnly(14, 20), "B3", 1, "A组", "首轮赛", "男双1", "双选手1", "双选手2"),
                            new ScheduledMatch(2, day.DayLabel, new TimeOnly(14, 0), new TimeOnly(14, 20), "B4", 1, "A组", "首轮赛", "男双2", "双选手3", "双选手4")
                        ],
                        scheduleSettings)));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 0);
            var adjusted = workflow.AutoAdjustScheduleBoard(
                board,
                workflow.CreateSchedulingOptions(board, CrossEventSchedulingStrategy.BalancedRelaxed) with
                {
                    RefereeCount = 4
                }).Board;

            foreach (var item in adjusted.Items.Where(item => item.StartTime < new TimeOnly(15, 0) && new TimeOnly(14, 0) < item.EndTime))
            {
                var overlapping = adjusted.Items.Count(other =>
                    string.Equals(other.DayLabel, item.DayLabel, StringComparison.Ordinal)
                    && other.StartTime < item.EndTime
                    && item.StartTime < other.EndTime);
                Assert.True(overlapping <= 1);
            }
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventAutoAdjustLimitsCrossEventPlayerToSixMatchesPerDay()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-daily-load-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "男双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            var days = new[]
            {
                new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "B2"]),
                new ScheduleDaySettings(new DateOnly(2026, 6, 14), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "B2"])
            };
            var scheduleSettings = new ScheduleSettings(days, MatchMinutes: 20, MaxMatchesPerEntrantPerDay: 10);
            var sharedPlayer = new DrawParticipant("张三", PrimaryName: "张三", PrimaryStudentId: "20260001");
            var firstOpponents = Enumerable.Range(1, 4)
                .Select(index => new DrawParticipant($"男单对手{index}", PrimaryName: $"男单对手{index}", PrimaryStudentId: $"2026010{index}"))
                .ToList();
            var secondOpponents = Enumerable.Range(1, 3)
                .Select(index => new DrawParticipant($"男双对手{index}", PrimaryName: $"男双对手{index}", PrimaryStudentId: $"2026020{index}"))
                .ToList();
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    [sharedPlayer, .. firstOpponents],
                    new SchedulePlan(
                        Enumerable.Range(1, 4)
                            .Select(index => new ScheduledMatch(
                                index,
                                "2026-06-13",
                                new TimeOnly(14, 0).AddMinutes((index - 1) * 20),
                                new TimeOnly(14, 20).AddMinutes((index - 1) * 20),
                                "B1",
                                1,
                                "A组",
                                "首轮赛",
                                $"男单{index}",
                                "张三",
                                $"男单对手{index}"))
                            .ToList(),
                        scheduleSettings)));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "男双",
                    [sharedPlayer, .. secondOpponents],
                    new SchedulePlan(
                        Enumerable.Range(1, 3)
                            .Select(index => new ScheduledMatch(
                                index,
                                "2026-06-13",
                                new TimeOnly(16, 0).AddMinutes((index - 1) * 20),
                                new TimeOnly(16, 20).AddMinutes((index - 1) * 20),
                                "B2",
                                1,
                                "A组",
                                "首轮赛",
                                $"男双{index}",
                                "张三",
                                $"男双对手{index}"))
                            .ToList(),
                        scheduleSettings)));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 0);
            var adjusted = workflow.AutoAdjustScheduleBoard(
                board,
                workflow.CreateSchedulingOptions(board, CrossEventSchedulingStrategy.Compact)).Board;
            var dailyCounts = adjusted.Items
                .Where(item => item.SideAPlayerIdentities
                    .Concat(item.SideBPlayerIdentities)
                    .Any(player => player.IdentityKey == "student:20260001"))
                .GroupBy(item => item.DayLabel, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            Assert.All(dailyCounts.Values, count => Assert.True(count <= CrossEventScheduleRules.MaxPlayerMatchesPerDay));
            Assert.Contains("2026-06-14", dailyCounts.Keys);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventScheduleBoardCountsCourtOverlapInReportAndBlockingItems()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-court-overlap-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "男双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            store.Create(
                firstProgressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    [new DrawParticipant("张三", PrimaryName: "张三"), new DrawParticipant("李四", PrimaryName: "李四")],
                    CreateSingleMatchSchedule("男单1", "张三", "李四", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1")));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "男双",
                    [
                        new DrawParticipant("[王五 赵六]", PrimaryName: "王五", PartnerName: "赵六"),
                        new DrawParticipant("[孙七 周八]", PrimaryName: "孙七", PartnerName: "周八")
                    ],
                    CreateSingleMatchSchedule("男双1", "[王五 赵六]", "[孙七 周八]", new TimeOnly(14, 10), new TimeOnly(14, 40), "B1")));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 20);
            var directReport = workflow.AnalyzeProgressFiles([firstProgressPath, secondProgressPath], minimumRestMinutes: 20);

            Assert.Equal(2, board.BlockingConflictItemCount);
            Assert.Equal(1, board.Report.SevereCount);
            Assert.Equal(1, directReport.SevereCount);
            Assert.Contains(board.Report.Issues, issue => issue.Detail.Contains("同一场地时间重叠", StringComparison.Ordinal));
            Assert.All(board.Items, item => Assert.Equal(CrossEventConflictSeverity.Severe, item.ConflictSeverity));
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventBalancedAutoAdjustKeepsFinalsOnLastDay()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-balanced-{Guid.NewGuid():N}");
        var singlesPath = Path.Combine(directory, "男单.szbd");
        var doublesPath = Path.Combine(directory, "男双.szbd");
        var mixedPath = Path.Combine(directory, "混双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            store.Create(
                singlesPath,
                CreateManualProgressSnapshot(
                    "男单",
                    [new DrawParticipant("张三", PrimaryName: "张三"), new DrawParticipant("李四", PrimaryName: "李四")],
                    CreateThreeDaySingleMatchSchedule("男单决赛", "张三", "李四", "决赛", "B1")));
            store.Create(
                doublesPath,
                CreateManualProgressSnapshot(
                    "男双",
                    [
                        new DrawParticipant("[王五 赵六]", PrimaryName: "王五", PartnerName: "赵六"),
                        new DrawParticipant("[孙七 周八]", PrimaryName: "孙七", PartnerName: "周八")
                    ],
                    CreateThreeDaySingleMatchSchedule("男双决赛", "[王五 赵六]", "[孙七 周八]", "决赛", "B2")));
            store.Create(
                mixedPath,
                CreateManualProgressSnapshot(
                    "混双",
                    [
                        new DrawParticipant("[钱九 吴十]", PrimaryName: "钱九", PartnerName: "吴十"),
                        new DrawParticipant("[郑一 冯二]", PrimaryName: "郑一", PartnerName: "冯二")
                    ],
                    CreateThreeDaySingleMatchSchedule("混双决赛", "[钱九 吴十]", "[郑一 冯二]", "决赛", "B3")));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([singlesPath, doublesPath, mixedPath], minimumRestMinutes: 20);
            var options = workflow.CreateSchedulingOptions(board, CrossEventSchedulingStrategy.BalancedRelaxed);
            var adjusted = workflow.AutoAdjustScheduleBoard(board, options);
            var finalsFriendly = workflow.AutoAdjustScheduleBoard(
                board,
                workflow.CreateSchedulingOptions(board, CrossEventSchedulingStrategy.FinalsDayFriendly));

            Assert.All(
                adjusted.Board.Items.Where(item => item.MatchName.Contains("决赛", StringComparison.Ordinal)),
                item => Assert.Equal("2026-06-15", item.DayLabel));
            Assert.Equal(0, finalsFriendly.RemainingBlockingConflictItemCount);
            Assert.Equal(CrossEventSchedulingStrategy.BalancedRelaxed, adjusted.Board.SchedulingOptions?.Strategy);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventAutoAdjustKeepsMinimumRestForRepeatedPlayerInUnresolvedSameEventMatches()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-same-event-rest-{Guid.NewGuid():N}");
        var doublesPath = Path.Combine(directory, "男双.szbd");
        var blockerPath = Path.Combine(directory, "已完成项目.szbd");
        var blockerRecordPath = Path.Combine(directory, "已完成项目记录表.xlsx");

        try
        {
            Directory.CreateDirectory(directory);
            var day = new ScheduleDaySettings(
                new DateOnly(2026, 6, 13),
                new TimeOnly(14, 0),
                new TimeOnly(17, 20),
                ["B1", "B2"]);
            var settings = new ScheduleSettings([day], MatchMinutes: 20, MaxMatchesPerEntrantPerDay: 6);
            var repeatedPlayer = new CrossEventPlayerIdentity("张三");
            var firstSchedule = new SchedulePlan(
                [
                    new ScheduledMatch(
                        1,
                        day.DayLabel,
                        new TimeOnly(14, 0),
                        new TimeOnly(14, 20),
                        "B1",
                        1,
                        "A组",
                        "首轮赛",
                        "A组首轮赛1",
                        "[张三 李四]",
                        "[王五 赵六]",
                        MatchId: "a1",
                        SideAPlayerIdentities: [repeatedPlayer, new CrossEventPlayerIdentity("李四")],
                        SideBPlayerIdentities: [new CrossEventPlayerIdentity("王五"), new CrossEventPlayerIdentity("赵六")]),
                    new ScheduledMatch(
                        2,
                        day.DayLabel,
                        new TimeOnly(15, 0),
                        new TimeOnly(15, 20),
                        "B1",
                        2,
                        "B组",
                        "首轮赛",
                        "B组首轮赛1",
                        "[张三 郑七]",
                        "[钱八 孙九]",
                        MatchId: "b1",
                        SideAPlayerIdentities: [repeatedPlayer, new CrossEventPlayerIdentity("郑七")],
                        SideBPlayerIdentities: [new CrossEventPlayerIdentity("钱八"), new CrossEventPlayerIdentity("孙九")]),
                    new ScheduledMatch(
                        3,
                        day.DayLabel,
                        new TimeOnly(16, 0),
                        new TimeOnly(16, 20),
                        "B1",
                        1,
                        "A组",
                        "决赛",
                        "A组决赛1",
                        "A组首轮赛1胜者",
                        "[周十 吴一]",
                        MatchId: "a2",
                        Dependencies: [Dependency("a1", "A组首轮赛1", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)],
                        SideAPlayerIdentities:
                        [
                            repeatedPlayer,
                            new CrossEventPlayerIdentity("李四"),
                            new CrossEventPlayerIdentity("王五"),
                            new CrossEventPlayerIdentity("赵六")
                        ],
                        SideBPlayerIdentities: [new CrossEventPlayerIdentity("周十"), new CrossEventPlayerIdentity("吴一")]),
                    new ScheduledMatch(
                        4,
                        day.DayLabel,
                        new TimeOnly(16, 40),
                        new TimeOnly(17, 0),
                        "B1",
                        2,
                        "B组",
                        "决赛",
                        "B组决赛1",
                        "B组首轮赛1胜者",
                        "[冯二 陈三]",
                        MatchId: "b2",
                        Dependencies: [Dependency("b1", "B组首轮赛1", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)],
                        SideAPlayerIdentities:
                        [
                            repeatedPlayer,
                            new CrossEventPlayerIdentity("郑七"),
                            new CrossEventPlayerIdentity("钱八"),
                            new CrossEventPlayerIdentity("孙九")
                        ],
                        SideBPlayerIdentities: [new CrossEventPlayerIdentity("冯二"), new CrossEventPlayerIdentity("陈三")])
                ],
                settings);
            var blockerSchedule = new SchedulePlan(
                [
                    new ScheduledMatch(
                        1,
                        day.DayLabel,
                        new TimeOnly(16, 40),
                        new TimeOnly(17, 0),
                        "B1",
                        1,
                        "A组",
                        "首轮赛",
                        "已完成项目1",
                        "甲",
                        "乙"),
                    new ScheduledMatch(
                        2,
                        day.DayLabel,
                        new TimeOnly(16, 40),
                        new TimeOnly(17, 0),
                        "B2",
                        1,
                        "A组",
                        "首轮赛",
                        "已完成项目2",
                        "丙",
                        "丁")
                ],
                settings);
            var store = new TournamentProgressStore();
            store.Create(
                doublesPath,
                CreateManualProgressSnapshot(
                    "男双",
                    [
                        new DrawParticipant("[张三 李四]", PrimaryName: "张三", PartnerName: "李四"),
                        new DrawParticipant("[王五 赵六]", PrimaryName: "王五", PartnerName: "赵六"),
                        new DrawParticipant("[张三 郑七]", PrimaryName: "张三", PartnerName: "郑七"),
                        new DrawParticipant("[钱八 孙九]", PrimaryName: "钱八", PartnerName: "孙九"),
                        new DrawParticipant("[周十 吴一]", PrimaryName: "周十", PartnerName: "吴一"),
                        new DrawParticipant("[冯二 陈三]", PrimaryName: "冯二", PartnerName: "陈三")
                    ],
                    firstSchedule));
            var blockerState = store.Create(
                blockerPath,
                CreateManualProgressSnapshot(
                    "已完成项目",
                    [new DrawParticipant("甲"), new DrawParticipant("乙"), new DrawParticipant("丙"), new DrawParticipant("丁")],
                    blockerSchedule));
            new ScheduleExcelWriter().WriteMatchRecord(
                blockerRecordPath,
                blockerSchedule,
                day.DayLabel,
                tournamentId: blockerState.Snapshot.TournamentId);
            FillMatchRecordWinners(blockerRecordPath, winnerOptionColumn: 15);
            store.Import(blockerPath, [blockerRecordPath]);

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([doublesPath, blockerPath], minimumRestMinutes: 30);
            var adjustment = workflow.AutoAdjustScheduleBoard(
                board,
                workflow.CreateSchedulingOptions(board, CrossEventSchedulingStrategy.BalancedRelaxed));
            var adjusted = adjustment.Board;
            var firstFinal = adjusted.Items.Single(item => item.EventName == "男双" && item.MatchName == "A组决赛1");
            var secondFinal = adjusted.Items.Single(item => item.EventName == "男双" && item.MatchName == "B组决赛1");
            var firstStartMinutes = (int)firstFinal.StartTime.ToTimeSpan().TotalMinutes;
            var firstEndMinutes = (int)firstFinal.EndTime.ToTimeSpan().TotalMinutes;
            var secondStartMinutes = (int)secondFinal.StartTime.ToTimeSpan().TotalMinutes;
            var secondEndMinutes = (int)secondFinal.EndTime.ToTimeSpan().TotalMinutes;
            var restMinutes = firstEndMinutes <= secondStartMinutes
                ? secondStartMinutes - firstEndMinutes
                : secondEndMinutes <= firstStartMinutes
                    ? firstStartMinutes - secondEndMinutes
                    : -Math.Min(firstEndMinutes, secondEndMinutes) + Math.Max(firstStartMinutes, secondStartMinutes);

            Assert.True(restMinutes >= 30, $"同一选手可能参加的两场未决比赛仅间隔 {restMinutes} 分钟。");
            Assert.Equal(0, adjustment.RemainingBlockingConflictItemCount);

            workflow.SaveScheduleBoard(adjusted);
            var reopenedSchedule = store.Read(doublesPath).Snapshot.Schedule;
            var reopenedReport = new ScheduleConstraintAnalyzer().Analyze(reopenedSchedule);
            Assert.DoesNotContain(
                reopenedReport.Issues,
                issue => issue.Severity is ScheduleConstraintSeverity.Severe or ScheduleConstraintSeverity.Warning);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventAutoAdjustAllowsMutuallyExclusiveWinnerAndLoserBranchesToOverlap()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-exclusive-paths-{Guid.NewGuid():N}");
        var progressPath = Path.Combine(directory, "男单.szbd");
        var otherProgressPath = Path.Combine(directory, "混双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var day = new ScheduleDaySettings(
                new DateOnly(2026, 6, 13),
                new TimeOnly(14, 0),
                new TimeOnly(15, 0),
                ["B1", "B2"]);
            var settings = new ScheduleSettings([day], MatchMinutes: 20, MaxMatchesPerEntrantPerDay: 6);
            var firstPlayer = new CrossEventPlayerIdentity("张三");
            var secondPlayer = new CrossEventPlayerIdentity("李四");
            var schedule = new SchedulePlan(
                [
                    new ScheduledMatch(
                        1,
                        day.DayLabel,
                        new TimeOnly(14, 0),
                        new TimeOnly(14, 20),
                        "B1",
                        1,
                        "A组",
                        "首轮赛",
                        "A组首轮赛1",
                        "张三",
                        "李四",
                        MatchId: "a1",
                        SideAPlayerIdentities: [firstPlayer],
                        SideBPlayerIdentities: [secondPlayer]),
                    new ScheduledMatch(
                        2,
                        day.DayLabel,
                        new TimeOnly(14, 40),
                        new TimeOnly(15, 0),
                        "B1",
                        1,
                        "A组",
                        "胜者组",
                        "胜者后续赛",
                        "A组首轮赛1胜者",
                        "王五",
                        MatchId: "winner",
                        Dependencies: [Dependency("a1", "A组首轮赛1", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)],
                        SideAPlayerIdentities: [firstPlayer, secondPlayer],
                        SideBPlayerIdentities: [new CrossEventPlayerIdentity("王五")]),
                    new ScheduledMatch(
                        3,
                        day.DayLabel,
                        new TimeOnly(14, 40),
                        new TimeOnly(15, 0),
                        "B2",
                        1,
                        "A组",
                        "负者组",
                        "负者后续赛",
                        "A组首轮赛1负者",
                        "赵六",
                        MatchId: "loser",
                        Dependencies: [Dependency("a1", "A组首轮赛1", ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA)],
                        SideAPlayerIdentities: [firstPlayer, secondPlayer],
                        SideBPlayerIdentities: [new CrossEventPlayerIdentity("赵六")])
                ],
                settings);
            var store = new TournamentProgressStore();
            store.Create(
                progressPath,
                CreateManualProgressSnapshot(
                    "男单",
                    [new DrawParticipant("张三"), new DrawParticipant("李四"), new DrawParticipant("王五"), new DrawParticipant("赵六")],
                    schedule));
            store.Create(
                otherProgressPath,
                CreateManualProgressSnapshot(
                    "混双",
                    [new DrawParticipant("钱七"), new DrawParticipant("孙八")],
                    new SchedulePlan(
                        [new ScheduledMatch(1, day.DayLabel, new TimeOnly(14, 0), new TimeOnly(14, 20), "B2", 1, "A组", "首轮赛", "混双首轮赛1", "钱七", "孙八")],
                        settings)));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([progressPath, otherProgressPath], minimumRestMinutes: 20);
            var adjusted = workflow.AutoAdjustScheduleBoard(
                board,
                workflow.CreateSchedulingOptions(board, CrossEventSchedulingStrategy.Compact)).Board;
            var winnerBranch = adjusted.Items.Single(item => item.MatchName == "胜者后续赛");
            var loserBranch = adjusted.Items.Single(item => item.MatchName == "负者后续赛");

            Assert.Equal(winnerBranch.DayLabel, loserBranch.DayLabel);
            Assert.Equal(winnerBranch.StartTime, loserBranch.StartTime);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventCustomDayLoadTargetsRebalanceFromSameBaseBoard()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-load-targets-{Guid.NewGuid():N}");
        var firstPath = Path.Combine(directory, "男单.szbd");
        var secondPath = Path.Combine(directory, "男双.szbd");
        var thirdPath = Path.Combine(directory, "混双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new TournamentProgressStore();
            store.Create(
                firstPath,
                CreateManualProgressSnapshot(
                    "男单",
                    CreateLooseParticipants("男单", 12),
                    CreateThreeDayLooseSchedule("男单", 6, "B1")));
            store.Create(
                secondPath,
                CreateManualProgressSnapshot(
                    "男双",
                    CreateLooseParticipants("男双", 12),
                    CreateThreeDayLooseSchedule("男双", 6, "B2")));
            store.Create(
                thirdPath,
                CreateManualProgressSnapshot(
                    "混双",
                    CreateLooseParticipants("混双", 12),
                    CreateThreeDayLooseSchedule("混双", 6, "B3")));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstPath, secondPath, thirdPath], minimumRestMinutes: 20);
            var balanced = workflow.CreateSchedulingOptions(board, CrossEventSchedulingStrategy.BalancedRelaxed);
            var lowFirstDay = balanced with
            {
                Strategy = CrossEventSchedulingStrategy.Custom,
                DayLoadTargets =
                [
                    new CrossEventDayLoadTarget("2026-06-13", 0.20, 0.35),
                    new CrossEventDayLoadTarget("2026-06-14", 0.90, 1.00),
                    new CrossEventDayLoadTarget("2026-06-15", 0.90, 1.00)
                ]
            };
            var highFirstDay = balanced with
            {
                Strategy = CrossEventSchedulingStrategy.Custom,
                DayLoadTargets =
                [
                    new CrossEventDayLoadTarget("2026-06-13", 0.95, 1.00),
                    new CrossEventDayLoadTarget("2026-06-14", 0.35, 0.50),
                    new CrossEventDayLoadTarget("2026-06-15", 0.35, 0.50)
                ]
            };

            var lowResult = workflow.AutoAdjustScheduleBoard(board, lowFirstDay);
            var highResult = workflow.AutoAdjustScheduleBoard(board, highFirstDay);
            var lowFirstDayCount = lowResult.Board.Items.Count(item => item.DayLabel == "2026-06-13");
            var highFirstDayCount = highResult.Board.Items.Count(item => item.DayLabel == "2026-06-13");

            Assert.Equal(0, lowResult.RemainingBlockingConflictItemCount);
            Assert.Equal(0, highResult.RemainingBlockingConflictItemCount);
            Assert.True(highFirstDayCount >= lowFirstDayCount);
            Assert.Equal(CrossEventSchedulingStrategy.Custom, highResult.Board.SchedulingOptions?.Strategy);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventScheduleBoardAutoAdjustsGlobalPoolAndMovesDependentMatches()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-global-auto-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "男双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var firstSchedule = new SchedulePlan(
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
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1"])],
                    MatchMinutes: 30,
                    MaxMatchesPerEntrantPerDay: 2));
            var secondSchedule = new SchedulePlan(
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
                        "男双首轮赛1",
                        "[赵六 钱七]",
                        "[孙八 周九]",
                        MatchId: "d1")
                ],
                new ScheduleSettings(
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1"])],
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
                    firstSchedule));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "男双",
                    [
                        new DrawParticipant("[赵六 钱七]", PrimaryName: "赵六", PartnerName: "钱七"),
                        new DrawParticipant("[孙八 周九]", PrimaryName: "孙八", PartnerName: "周九")
                    ],
                    secondSchedule));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 20);
            var adjusted = workflow.AutoAdjustScheduleBoard(board);
            var singlesFinal = adjusted.Board.Sources
                .Single(source => source.EventName == "男单")
                .Matches
                .Single(match => match.MatchName == "A组决赛1");
            var doublesMatch = adjusted.Board.Sources
                .Single(source => source.EventName == "男双")
                .Matches
                .Single(match => match.MatchName == "男双首轮赛1");

            Assert.True(board.BlockingConflictItemCount > 0);
            Assert.Equal(0, adjusted.RemainingBlockingConflictItemCount);
            Assert.True(adjusted.MovedCount >= 2);
            Assert.Equal(new TimeOnly(14, 30), doublesMatch.StartTime);
            Assert.Equal(new TimeOnly(15, 0), singlesFinal.StartTime);
            Assert.Empty(ScheduleDependencyGraph.Build(CrossEventConflictWorkflow.BuildMergedSchedulePlan(adjusted.Board)).FindOrderViolations());
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void CrossEventScheduleBoardBuildsCascadeMovePreviewForSameEventDependencies()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-cascade-preview-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "男双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var firstSchedule = new SchedulePlan(
                [
                    new ScheduledMatch(
                        1,
                        "2026-06-13",
                        new TimeOnly(14, 0),
                        new TimeOnly(14, 20),
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
                        new TimeOnly(14, 40),
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
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1", "B2"])],
                    MatchMinutes: 20,
                    MaxMatchesPerEntrantPerDay: 2));
            var secondSchedule = new SchedulePlan(
                [
                    new ScheduledMatch(
                        1,
                        "2026-06-13",
                        new TimeOnly(14, 45),
                        new TimeOnly(15, 5),
                        "B2",
                        1,
                        "A组",
                        "首轮赛",
                        "男双首轮赛1",
                        "[张三 钱七]",
                        "[孙八 周九]",
                        MatchId: "d1")
                ],
                new ScheduleSettings(
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1", "B2"])],
                    MatchMinutes: 20,
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
                    firstSchedule));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "男双",
                    [
                        new DrawParticipant("[张三 钱七]", PrimaryName: "张三", PartnerName: "钱七"),
                        new DrawParticipant("[孙八 周九]", PrimaryName: "孙八", PartnerName: "周九")
                    ],
                    secondSchedule));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 20);
            var firstMatchKey = board.Items.Single(item => item.EventName == "男单" && item.MatchName == "A组首轮赛1").Key;

            var preview = workflow.BuildScheduleItemCascadeMovePreview(
                board,
                firstMatchKey,
                "2026-06-13",
                new TimeOnly(14, 20),
                "B2");

            Assert.True(preview.HasAffectedMatches);
            var affected = Assert.Single(preview.AffectedMatches);
            Assert.Equal("男单", affected.EventName);
            Assert.Equal("A组决赛1", affected.MatchName);
            Assert.Equal(1, affected.Depth);
            Assert.Equal(0, affected.RestMinutes);
            Assert.DoesNotContain(preview.AffectedMatches, item => item.MatchName == "男双首轮赛1");
            var crossImpact = Assert.Single(preview.CrossEventImpacts);
            Assert.Equal(CrossEventConflictSeverity.Warning, crossImpact.Severity);
            Assert.Equal("张三", crossImpact.PlayerName);
            Assert.Equal("男双", crossImpact.EventName);
            Assert.Equal("男双首轮赛1", crossImpact.MatchName);
            Assert.Equal(5, crossImpact.RestMinutes);
            Assert.Contains("低于最小休息间隔", crossImpact.Detail);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }
}
