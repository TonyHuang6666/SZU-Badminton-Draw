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
    public void ScheduleWorkflowMovesScheduledMatchAndRejectsOccupiedSlot()
    {
        var schedule = new SchedulePlan(
            [
                new ScheduledMatch(1, "2026-06-13", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1", 1, "A组", "首轮赛", "男单1", "张三", "李四"),
                new ScheduledMatch(2, "2026-06-13", new TimeOnly(14, 0), new TimeOnly(14, 30), "B2", 1, "A组", "首轮赛", "男单2", "王五", "赵六")
            ],
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1", "B2"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 14), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1", "B2"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));

        var allowedValidation = ScheduleWorkflow.ValidateScheduledMatchMove(
            schedule,
            "男单2",
            "2026-06-13",
            new TimeOnly(14, 30),
            "B1");
        Assert.True(allowedValidation.CanDrop);
        Assert.Equal(ScheduleBoardMoveValidationSeverity.Allowed, allowedValidation.Severity);

        var moved = ScheduleWorkflow.MoveScheduledMatch(
            schedule,
            "男单2",
            "2026-06-13",
            new TimeOnly(14, 30),
            "B1");
        var adjusted = moved.Matches.Single(match => match.MatchName == "男单2");

        Assert.Equal(new TimeOnly(14, 30), adjusted.StartTime);
        Assert.Equal(new TimeOnly(15, 0), adjusted.EndTime);
        Assert.Equal("B1", adjusted.Court);
        Assert.Equal([1, 2], moved.Matches.Select(match => match.Order).ToArray());
        var crossDay = ScheduleWorkflow.MoveScheduledMatch(
            schedule,
            "男单2",
            "2026-06-14",
            new TimeOnly(14, 0),
            "B1");
        var crossDayMatch = crossDay.Matches.Single(match => match.MatchName == "男单2");
        Assert.Equal("2026-06-14", crossDayMatch.DayLabel);
        Assert.Equal(new TimeOnly(14, 0), crossDayMatch.StartTime);
        Assert.Equal(new TimeOnly(14, 30), crossDayMatch.EndTime);
        Assert.Equal("B1", crossDayMatch.Court);
        var blockedValidation = ScheduleWorkflow.ValidateScheduledMatchMove(
            schedule,
            "男单2",
            "2026-06-13",
            new TimeOnly(14, 0),
            "B1");
        Assert.False(blockedValidation.CanDrop);
        Assert.Equal(ScheduleBoardMoveValidationSeverity.Blocked, blockedValidation.Severity);
        Assert.Contains("已有比赛", blockedValidation.Message);
        Assert.Throws<DrawValidationException>(() => ScheduleWorkflow.MoveScheduledMatch(
            schedule,
            "男单2",
            "2026-06-13",
            new TimeOnly(14, 0),
            "B1"));
    }

    [Fact]
    public void ScheduleWorkflowRejectsMoveIntoUnavailableCourtWindow()
    {
        var schedule = new SchedulePlan(
            [
                new ScheduledMatch(1, "2026-06-13", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1", 1, "A组", "首轮赛", "男单1", "张三", "李四"),
                new ScheduledMatch(2, "2026-06-13", new TimeOnly(14, 0), new TimeOnly(14, 30), "B2", 1, "A组", "首轮赛", "男单2", "王五", "赵六")
            ],
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(
                        new DateOnly(2026, 6, 13),
                        new TimeOnly(14, 0),
                        new TimeOnly(16, 0),
                        ["B1", "B2"],
                        UnavailableCourtWindows:
                        [
                            new ScheduleCourtAvailabilityBlock(new TimeOnly(14, 30), new TimeOnly(15, 0), ["B1"])
                        ])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));

        var validation = ScheduleWorkflow.ValidateScheduledMatchMove(
            schedule,
            "男单2",
            "2026-06-13",
            new TimeOnly(14, 30),
            "B1");

        Assert.False(validation.CanDrop);
        Assert.Equal(ScheduleBoardMoveValidationSeverity.Blocked, validation.Severity);
        Assert.Contains("不可用", validation.Message);
        var exception = Assert.Throws<DrawValidationException>(() => ScheduleWorkflow.MoveScheduledMatch(
            schedule,
            "男单2",
            "2026-06-13",
            new TimeOnly(14, 30),
            "B1"));
        Assert.Contains("不可用", exception.Message);
    }

    [Fact]
    public void ScheduleWorkflowBuildsSharedBoardViewForSingleEvent()
    {
        var schedule = new SchedulePlan(
            [
                new ScheduledMatch(1, "2026-06-13", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1", 1, "A组", "首轮赛", "男单1", "张三", "李四"),
                new ScheduledMatch(2, "2026-06-13", new TimeOnly(14, 0), new TimeOnly(14, 30), "B2", 1, "A组", "首轮赛", "男单2", "王五", "赵六")
            ],
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1", "B2"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));

        var view = ScheduleWorkflow.BuildScheduleBoardView(
            schedule,
            new HashSet<string>(StringComparer.Ordinal) { "男单2" });
        var lockedItem = Assert.Single(view.GetItems("2026-06-13", "B2", new TimeOnly(14, 0)));

        Assert.Equal(ScheduleBoardKind.SingleEvent, view.Kind);
        Assert.Equal(["2026-06-13"], view.DayLabels);
        Assert.Equal("男单2", lockedItem.Key);
        Assert.True(lockedItem.IsLocked);
        Assert.Equal(ScheduleBoardDrag.BuildSingleEventPayload("男单2"), lockedItem.DragPayload);
        Assert.Contains("已完成", lockedItem.Subtitle);
        Assert.True(ScheduleBoardDrag.TryParseSingleEventPayload(lockedItem.DragPayload, out var matchName));
        Assert.Equal("男单2", matchName);
        Assert.Equal(ScheduleBoardLayout.WindowMinZoom, ScheduleBoardLayout.ClampWindowZoom(0.1));
    }

    [Fact]
    public void CrossEventWorkflowBuildsSharedBoardViewForCrossEvent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-board-view-{Guid.NewGuid():N}");
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

            var board = new CrossEventConflictWorkflow().LoadScheduleBoard(
                [firstProgressPath, secondProgressPath],
                minimumRestMinutes: 20);
            var view = CrossEventConflictWorkflow.BuildScheduleBoardView(board);
            var mixedDoublesItem = view.Items.Single(item => item.SortText == "混双");

            Assert.Equal(ScheduleBoardKind.CrossEvent, view.Kind);
            Assert.Equal(board.Days.Select(day => day.DayLabel).ToArray(), view.DayLabels);
            Assert.Equal(mixedDoublesItem.Key, mixedDoublesItem.DragPayload);
            Assert.False(ScheduleBoardDrag.TryParseSingleEventPayload(mixedDoublesItem.DragPayload, out _));
            Assert.True(mixedDoublesItem.IsBlocking);
            Assert.Contains("张三", mixedDoublesItem.SideText);
            Assert.NotEmpty(mixedDoublesItem.DetailText);
            Assert.NotEmpty(mixedDoublesItem.Tooltip);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void ScheduleWorkflowRejectsDependencyOrderViolationWhenMoving()
    {
        var schedule = new SchedulePlan(
            [
                new ScheduledMatch(
                    1,
                    "2026-06-13",
                    new TimeOnly(14, 0),
                    new TimeOnly(14, 30),
                    "B1",
                    1,
                    "A组",
                    "16进8",
                    "A组16进8第1场",
                    "张三",
                    "李四",
                    MatchId: "m1"),
                new ScheduledMatch(
                    2,
                    "2026-06-13",
                    new TimeOnly(14, 30),
                    new TimeOnly(15, 0),
                    "B2",
                    1,
                    "A组",
                    "8进4",
                    "A组8进4第1场",
                    "A组16进8第1场胜者",
                    "王五",
                    MatchId: "m2",
                    Dependencies: [Dependency("m1", "A组16进8第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)])
            ],
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1", "B2"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));

        var exception = Assert.Throws<DrawValidationException>(() => ScheduleWorkflow.MoveScheduledMatch(
            schedule,
            "A组8进4第1场",
            "2026-06-13",
            new TimeOnly(14, 0),
            "B2"));
        Assert.Contains("赛程顺序错误", exception.Message);
        Assert.Contains("前序场次结束前开始", exception.Message);
    }

    [Fact]
    public void ScheduleWorkflowWarnsWhenMoveCreatesShortDependencyRest()
    {
        var schedule = new SchedulePlan(
            [
                new ScheduledMatch(
                    1,
                    "2026-06-13",
                    new TimeOnly(14, 0),
                    new TimeOnly(14, 30),
                    "B1",
                    1,
                    "A组",
                    "16进8",
                    "A组16进8第1场",
                    "张三",
                    "李四",
                    MatchId: "m1"),
                new ScheduledMatch(
                    2,
                    "2026-06-13",
                    new TimeOnly(15, 0),
                    new TimeOnly(15, 30),
                    "B2",
                    1,
                    "A组",
                    "8进4",
                    "A组8进4第1场",
                    "A组16进8第1场胜者",
                    "王五",
                    MatchId: "m2",
                    Dependencies: [Dependency("m1", "A组16进8第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)])
            ],
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1", "B2"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));

        var validation = ScheduleWorkflow.ValidateScheduledMatchMove(
            schedule,
            "A组8进4第1场",
            "2026-06-13",
            new TimeOnly(14, 30),
            "B2");

        Assert.True(validation.CanDrop);
        Assert.Equal(ScheduleBoardMoveValidationSeverity.Warning, validation.Severity);
        Assert.Contains("场次接续风险", validation.Message);
    }

    [Fact]
    public void ScheduleWorkflowBuildsCascadeMovePreviewFromDependencyTree()
    {
        var schedule = new SchedulePlan(
            [
                new ScheduledMatch(
                    1,
                    "2026-06-13",
                    new TimeOnly(14, 0),
                    new TimeOnly(14, 20),
                    "B1",
                    1,
                    "A组",
                    "128进64",
                    "A组128进64第1场",
                    "张三",
                    "李四",
                    MatchId: "m1"),
                new ScheduledMatch(
                    2,
                    "2026-06-13",
                    new TimeOnly(14, 40),
                    new TimeOnly(15, 0),
                    "B1",
                    1,
                    "A组",
                    "64进32",
                    "A组64进32第1场",
                    "A组128进64第1场胜者",
                    "王五",
                    MatchId: "m2",
                    Dependencies: [Dependency("m1", "A组128进64第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                new ScheduledMatch(
                    3,
                    "2026-06-13",
                    new TimeOnly(15, 20),
                    new TimeOnly(15, 40),
                    "B1",
                    1,
                    "A组",
                    "32进16",
                    "A组32进16第1场",
                    "A组64进32第1场胜者",
                    "赵六",
                    MatchId: "m3",
                    Dependencies: [Dependency("m2", "A组64进32第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)])
            ],
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1", "B2"])],
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 2));

        var preview = ScheduleWorkflow.BuildScheduledMatchCascadeMovePreview(
            schedule,
            "A组128进64第1场",
            "2026-06-13",
            new TimeOnly(14, 20),
            "B2");

        Assert.True(preview.HasAffectedMatches);
        Assert.Equal("2026-06-13 14:20-14:40 · B2", preview.TargetText);
        Assert.Equal(new[] { "A组64进32第1场", "A组32进16第1场" }, preview.AffectedMatches.Select(item => item.MatchName));
        Assert.Equal(new[] { 1, 2 }, preview.AffectedMatches.Select(item => item.Depth));
        Assert.Equal(0, preview.AffectedMatches[0].RestMinutes);
        Assert.Equal(20, preview.AffectedMatches[1].RestMinutes);
        Assert.Contains("胜者", preview.AffectedMatches[0].DependencyText);
    }

    [Fact]
    public void ScheduleWorkflowCascadeMovesDependentMatchesToNearestLegalSlots()
    {
        var schedule = new SchedulePlan(
            [
                new ScheduledMatch(1, "2026-06-13", new TimeOnly(14, 0), new TimeOnly(14, 20), "B1", 1, "A组", "128进64", "A组128进64第1场", "张三", "李四", MatchId: "m1"),
                new ScheduledMatch(
                    2,
                    "2026-06-13",
                    new TimeOnly(14, 40),
                    new TimeOnly(15, 0),
                    "B1",
                    1,
                    "A组",
                    "64进32",
                    "A组64进32第1场",
                    "A组128进64第1场胜者",
                    "王五",
                    MatchId: "m2",
                    Dependencies: [Dependency("m1", "A组128进64第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                new ScheduledMatch(
                    3,
                    "2026-06-13",
                    new TimeOnly(15, 20),
                    new TimeOnly(15, 40),
                    "B1",
                    1,
                    "A组",
                    "32进16",
                    "A组32进16第1场",
                    "A组64进32第1场胜者",
                    "赵六",
                    MatchId: "m3",
                    Dependencies: [Dependency("m2", "A组64进32第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)])
            ],
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(17, 0), ["B1", "B2"])],
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 2));

        var result = ScheduleWorkflow.CascadeMoveScheduledMatch(
            schedule,
            "A组128进64第1场",
            "2026-06-13",
            new TimeOnly(14, 20),
            "B2");

        Assert.Equal(3, result.MovedMatches.Count);
        Assert.Equal(new TimeOnly(14, 20), result.Schedule.Matches.Single(match => match.MatchName == "A组128进64第1场").StartTime);
        Assert.Equal(new TimeOnly(15, 0), result.Schedule.Matches.Single(match => match.MatchName == "A组64进32第1场").StartTime);
        Assert.Equal(new TimeOnly(15, 40), result.Schedule.Matches.Single(match => match.MatchName == "A组32进16第1场").StartTime);
        Assert.Empty(ScheduleDependencyGraph.Build(result.Schedule).FindOrderViolations());
        Assert.DoesNotContain(
            new ScheduleConstraintAnalyzer().Analyze(result.Schedule).Issues,
            issue => issue.Severity == ScheduleConstraintSeverity.Severe);
    }

    [Fact]
    public void ScheduleWorkflowCascadeMoveRejectsCompletedDependentMatch()
    {
        var schedule = new SchedulePlan(
            [
                new ScheduledMatch(1, "2026-06-13", new TimeOnly(14, 0), new TimeOnly(14, 20), "B1", 1, "A组", "128进64", "A组128进64第1场", "张三", "李四", MatchId: "m1"),
                new ScheduledMatch(
                    2,
                    "2026-06-13",
                    new TimeOnly(14, 40),
                    new TimeOnly(15, 0),
                    "B1",
                    1,
                    "A组",
                    "64进32",
                    "A组64进32第1场",
                    "A组128进64第1场胜者",
                    "王五",
                    MatchId: "m2",
                    Dependencies: [Dependency("m1", "A组128进64第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)])
            ],
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(17, 0), ["B1", "B2"])],
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 2));

        var exception = Assert.Throws<DrawValidationException>(() => ScheduleWorkflow.CascadeMoveScheduledMatch(
            schedule,
            "A组128进64第1场",
            "2026-06-13",
            new TimeOnly(14, 20),
            "B2",
            new HashSet<string>(["A组64进32第1场"], StringComparer.Ordinal)));

        Assert.Contains("已有赛果", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossEventScheduleBoardCascadeMoveRespectsOtherEventPlayerRest()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-cascade-move-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "男双.szbd");

        try
        {
            Directory.CreateDirectory(directory);
            var singlesSchedule = new SchedulePlan(
                [
                    new ScheduledMatch(
                        1,
                        "2026-06-13",
                        new TimeOnly(14, 0),
                        new TimeOnly(14, 20),
                        "B1",
                        1,
                        "A组",
                        "128进64",
                        "A组128进64第1场",
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
                        "64进32",
                        "A组64进32第1场",
                        "A组128进64第1场胜者",
                        "王五",
                        MatchId: "s2",
                        Dependencies: [Dependency("s1", "A组128进64第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)])
                ],
                new ScheduleSettings(
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(17, 0), ["B1", "B2"])],
                    MatchMinutes: 20,
                    MaxMatchesPerEntrantPerDay: 2));
            var doublesSchedule = new SchedulePlan(
                [
                    new ScheduledMatch(
                        1,
                        "2026-06-13",
                        new TimeOnly(15, 0),
                        new TimeOnly(15, 20),
                        "B2",
                        1,
                        "A组",
                        "首轮赛",
                        "男双首轮赛1",
                        "[王五 赵六]",
                        "[钱七 孙八]",
                        MatchId: "d1")
                ],
                new ScheduleSettings(
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(17, 0), ["B1", "B2"])],
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
                    singlesSchedule));
            store.Create(
                secondProgressPath,
                CreateManualProgressSnapshot(
                    "男双",
                    [
                        new DrawParticipant("[王五 赵六]", PrimaryName: "王五", PartnerName: "赵六"),
                        new DrawParticipant("[钱七 孙八]", PrimaryName: "钱七", PartnerName: "孙八")
                    ],
                    doublesSchedule));

            var workflow = new CrossEventConflictWorkflow();
            var board = workflow.LoadScheduleBoard([firstProgressPath, secondProgressPath], minimumRestMinutes: 20);
            var firstMatchKey = board.Items.Single(item => item.EventName == "男单" && item.MatchName == "A组128进64第1场").Key;

            var result = workflow.CascadeMoveScheduleItem(
                board,
                firstMatchKey,
                "2026-06-13",
                new TimeOnly(14, 20),
                "B2");

            var adjustedDependent = result.Schedule.Items.Single(item => item.EventName == "男单" && item.MatchName == "A组64进32第1场");
            Assert.Equal(new TimeOnly(15, 40), adjustedDependent.StartTime);
            Assert.Equal(2, result.MovedMatches.Count);
            Assert.Equal(0, result.Schedule.Report.SevereCount);
            Assert.Equal(0, result.Schedule.Report.WarningCount);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }
}
