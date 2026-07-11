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
    public void ScheduleConstraintAnalyzerUsesFormalRestProfileForKeyMatches()
    {
        var schedule = new SchedulePlan(
            [
                new ScheduledMatch(1, "2026-06-13", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1", 1, "A组", "半决赛", "半决赛1", "张三", "李四"),
                new ScheduledMatch(2, "2026-06-13", new TimeOnly(15, 10), new TimeOnly(15, 40), "B1", 1, "A组", "决赛", "决赛1", "张三", "王五")
            ],
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 3)
            {
                ConstraintProfile = ScheduleConstraintProfile.Formal
            });

        var report = new ScheduleConstraintAnalyzer().Analyze(schedule);

        Assert.Equal(ScheduleConstraintProfile.Formal, report.Profile);
        Assert.Contains(report.Issues, issue =>
            issue.Type == ScheduleConstraintIssueType.ShortRest
            && issue.Severity == ScheduleConstraintSeverity.Warning
            && issue.PlayerName == "张三"
            && issue.Message.Contains("60 分钟", StringComparison.Ordinal));
    }

    [Fact]
    public void ScheduleConstraintAnalyzerFlagsManualOverlapAsSevere()
    {
        var schedule = new SchedulePlan(
            [
                new ScheduledMatch(1, "2026-06-13", new TimeOnly(14, 0), new TimeOnly(14, 30), "B1", 1, "A组", "首轮赛", "第1场", "[张三 李四]", "[王五 赵六]"),
                new ScheduledMatch(2, "2026-06-13", new TimeOnly(14, 20), new TimeOnly(14, 50), "B2", 1, "A组", "首轮赛", "第2场", "张三", "钱七")
            ],
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "B2"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 3));

        var report = new ScheduleConstraintAnalyzer().Analyze(schedule);

        Assert.Equal(1, report.SevereCount);
        Assert.Contains(report.Issues, issue =>
            issue.Type == ScheduleConstraintIssueType.ShortRest
            && issue.Severity == ScheduleConstraintSeverity.Severe
            && issue.PlayerName == "张三");
    }

    [Fact]
    public void ScheduleConstraintAnalyzerUsesStudentIdBeforeName()
    {
        static SchedulePlan CreateSchedule(CrossEventPlayerIdentity laterPlayer)
        {
            return new SchedulePlan(
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
                        "第1场",
                        "张三",
                        "李四",
                        SideAPlayerIdentities: [new CrossEventPlayerIdentity("张三", "20260001")],
                        SideBPlayerIdentities: [new CrossEventPlayerIdentity("李四", "20260002")]),
                    new ScheduledMatch(
                        2,
                        "2026-06-13",
                        new TimeOnly(14, 10),
                        new TimeOnly(14, 40),
                        "B2",
                        1,
                        "A组",
                        "首轮赛",
                        "第2场",
                        "张三",
                        "王五",
                        SideAPlayerIdentities: [laterPlayer],
                        SideBPlayerIdentities: [new CrossEventPlayerIdentity("王五", "20260003")])
                ],
                new ScheduleSettings(
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "B2"])],
                    MatchMinutes: 30,
                    MaxMatchesPerEntrantPerDay: 3));
        }

        var differentStudentReport = new ScheduleConstraintAnalyzer().Analyze(
            CreateSchedule(new CrossEventPlayerIdentity("张三", "20269999")));
        var sameStudentReport = new ScheduleConstraintAnalyzer().Analyze(
            CreateSchedule(new CrossEventPlayerIdentity("张三", "20260001")));

        Assert.Equal(0, differentStudentReport.SevereCount);
        Assert.DoesNotContain(differentStudentReport.Issues, issue => issue.PlayerName?.StartsWith("张三", StringComparison.Ordinal) == true);
        Assert.Equal(1, sameStudentReport.SevereCount);
        Assert.Contains(sameStudentReport.Issues, issue =>
            issue.Type == ScheduleConstraintIssueType.ShortRest
            && issue.Severity == ScheduleConstraintSeverity.Severe
            && issue.PlayerName == "张三（20260001）");
    }

    [Fact]
    public void ScheduleServiceCarriesStudentIdsIntoScheduledMatches()
    {
        var participants = new[]
        {
            new DrawParticipant("张三", PrimaryName: "张三", PrimaryStudentId: "20260001"),
            new DrawParticipant("李四", PrimaryName: "李四", PrimaryStudentId: "20260002")
        };
        var result = new DrawService().Generate(
            participants,
            CreateSettings(
                groupCount: 1,
                mode: CompetitionMode.SinglesKnockout,
                knockoutGoal: KnockoutGoal.Champion));

        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(15, 0), ["B1"])],
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 2));

        var match = Assert.Single(schedule.Matches);
        Assert.Equal("student:20260001", Assert.Single(match.SideAPlayerIdentities).IdentityKey);
        Assert.Equal("student:20260002", Assert.Single(match.SideBPlayerIdentities).IdentityKey);
    }

    [Fact]
    public void ScheduleConstraintAnalyzerGroupsWinnerPlaceholderRestByDependency()
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
                    new TimeOnly(14, 20),
                    new TimeOnly(14, 40),
                    "B2",
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
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "B2"])],
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 3));

        var report = new ScheduleConstraintAnalyzer().Analyze(schedule);

        var issue = Assert.Single(report.Issues, issue =>
            issue.Type == ScheduleConstraintIssueType.DependencyOrder
            && issue.Severity == ScheduleConstraintSeverity.Warning
            && issue.MatchName == "A组64进32第1场");
        Assert.Equal(ScheduleConstraintIssueScope.DirectDependency, issue.Scope);
        Assert.Null(issue.PlayerName);
        Assert.Contains("场次接续风险", issue.Message);
        Assert.Contains("A组128进64第1场 的胜者进入 A组64进32第1场", issue.Message);
        Assert.Contains("张三", issue.Message);
        Assert.Contains("李四", issue.Message);
        Assert.Equal(1, report.DirectDependencyCount);
    }

    [Fact]
    public void ScheduleConstraintAnalyzerFlagsDependencyOrderAsSevere()
    {
        var schedule = new SchedulePlan(
            [
                new ScheduledMatch(
                    1,
                    "2026-06-13",
                    new TimeOnly(14, 20),
                    new TimeOnly(14, 40),
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
                    new TimeOnly(14, 20),
                    new TimeOnly(14, 50),
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
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "B2"])],
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 3));

        var report = new ScheduleConstraintAnalyzer().Analyze(schedule);

        var issue = Assert.Single(report.Issues, issue =>
            issue.Type == ScheduleConstraintIssueType.DependencyOrder
            && issue.Severity == ScheduleConstraintSeverity.Severe
            && issue.MatchName == "A组8进4第1场");
        Assert.Equal(ScheduleConstraintIssueScope.DirectDependency, issue.Scope);
        Assert.Contains("赛程顺序错误", issue.Message);
        Assert.Contains("前序场次结束前开始", issue.Message);
        Assert.Equal(1, report.SevereCount);
    }

    [Fact]
    public void ScheduleConstraintAnalyzerUsesProfileProjectionDepth()
    {
        static SchedulePlan CreateSchedule(ScheduleConstraintProfile profile)
        {
            return new SchedulePlan(
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
                        new TimeOnly(15, 0),
                        new TimeOnly(15, 20),
                        "B2",
                        1,
                        "A组",
                        "32进16",
                        "A组32进16第1场",
                        "A组64进32第1场胜者",
                        "赵六",
                        MatchId: "m3",
                        Dependencies: [Dependency("m2", "A组64进32第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                    new ScheduledMatch(
                        4,
                        "2026-06-13",
                        new TimeOnly(15, 20),
                        new TimeOnly(15, 40),
                        "B3",
                        1,
                        "A组",
                        "16进8",
                        "A组16进8第1场",
                        "A组32进16第1场胜者",
                        "孙七",
                        MatchId: "m4",
                        Dependencies: [Dependency("m3", "A组32进16第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)])
                ],
                new ScheduleSettings(
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "B2"])],
                    MatchMinutes: 20,
                    MaxMatchesPerEntrantPerDay: 4)
                {
                    ConstraintProfile = profile
                });
        }

        var campusReport = new ScheduleConstraintAnalyzer().Analyze(CreateSchedule(ScheduleConstraintProfile.Campus));
        var campusNextRoundIssue = Assert.Single(campusReport.Issues, issue =>
            issue.Type == ScheduleConstraintIssueType.DependencyOrder
            && issue.MatchName == "A组16进8第1场");
        Assert.Contains("王五", campusNextRoundIssue.Message);
        Assert.Contains("赵六", campusNextRoundIssue.Message);
        Assert.DoesNotContain("张三", campusNextRoundIssue.Message);
        Assert.DoesNotContain("李四", campusNextRoundIssue.Message);

        var auditReport = new ScheduleConstraintAnalyzer().Analyze(CreateSchedule(ScheduleConstraintProfile.Audit));
        var auditNextRoundIssue = Assert.Single(auditReport.Issues, issue =>
            issue.Type == ScheduleConstraintIssueType.DependencyOrder
            && issue.MatchName == "A组16进8第1场");
        Assert.Contains("张三", auditNextRoundIssue.Message);
        Assert.Contains("李四", auditNextRoundIssue.Message);
        Assert.Contains("王五", auditNextRoundIssue.Message);
        Assert.Contains("赵六", auditNextRoundIssue.Message);
    }

    [Fact]
    public void ScheduleConstraintAnalyzerDoesNotTreatWinnerAndLoserBranchesAsCompatible()
    {
        var schedule = new SchedulePlan(
            [
                new ScheduledMatch(1, "2026-06-13", new TimeOnly(13, 0), new TimeOnly(13, 20), "B1", 1, "A组", "半决赛", "A组半决赛第1场", "张三", "李四", MatchId: "m1"),
                new ScheduledMatch(
                    2,
                    "2026-06-13",
                    new TimeOnly(14, 0),
                    new TimeOnly(14, 20),
                    "B1",
                    1,
                    "A组",
                    "决赛",
                    "A组决赛第1场",
                    "A组半决赛第1场胜者",
                    "王五",
                    MatchId: "m2",
                    Dependencies: [Dependency("m1", "A组半决赛第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                new ScheduledMatch(
                    3,
                    "2026-06-13",
                    new TimeOnly(14, 0),
                    new TimeOnly(14, 20),
                    "B2",
                    1,
                    "A组",
                    "3/4名赛",
                    "3/4名赛",
                    "A组半决赛第1场负者",
                    "赵六",
                    MatchId: "m3",
                    Dependencies: [Dependency("m1", "A组半决赛第1场", ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA)])
            ],
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(13, 0), new TimeOnly(18, 0), ["B1", "B2"])],
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 3));

        var report = new ScheduleConstraintAnalyzer().Analyze(schedule);

        Assert.DoesNotContain(report.Issues, issue =>
            issue.Type == ScheduleConstraintIssueType.ShortRest
            && issue.Severity == ScheduleConstraintSeverity.Severe
            && (issue.PlayerName == "张三" || issue.PlayerName == "李四"));
        Assert.DoesNotContain(report.Issues, issue =>
            issue.Type == ScheduleConstraintIssueType.DailyLoad
            && (issue.PlayerName == "张三" || issue.PlayerName == "李四"));
    }

    [Fact]
    public void ScheduleConstraintAnalyzerReportsProbabilisticLoadForPlacementPlayoffs()
    {
        var player = new CrossEventPlayerIdentity("张三", "20260001");
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
                    "16进8",
                    "A组16进8第1场",
                    "张三",
                    "李四",
                    MatchId: "m1",
                    SideAPlayerIdentities: [player]),
                new ScheduledMatch(
                    2,
                    "2026-06-13",
                    new TimeOnly(14, 40),
                    new TimeOnly(15, 0),
                    "B1",
                    1,
                    "A组",
                    "8进4",
                    "A组8进4第1场",
                    "A组16进8第1场胜者",
                    "王五",
                    MatchId: "m2",
                    Dependencies: [Dependency("m1", "A组16进8第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                new ScheduledMatch(
                    3,
                    "2026-06-13",
                    new TimeOnly(15, 20),
                    new TimeOnly(15, 40),
                    "B1",
                    1,
                    "A组",
                    "半决赛",
                    "A组半决赛第1场",
                    "A组8进4第1场胜者",
                    "赵六",
                    MatchId: "m3",
                    Dependencies: [Dependency("m2", "A组8进4第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                new ScheduledMatch(
                    4,
                    "2026-06-13",
                    new TimeOnly(16, 0),
                    new TimeOnly(16, 20),
                    "B1",
                    1,
                    "A组",
                    "决赛",
                    "A组决赛第1场",
                    "A组半决赛第1场胜者",
                    "钱七",
                    MatchId: "m4",
                    Dependencies: [Dependency("m3", "A组半决赛第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                new ScheduledMatch(
                    5,
                    "2026-06-13",
                    new TimeOnly(16, 0),
                    new TimeOnly(16, 20),
                    "B2",
                    1,
                    "A组",
                    "3/4名赛",
                    "A组3/4名赛",
                    "A组半决赛第1场负者",
                    "孙八",
                    MatchId: "m5",
                    Dependencies: [Dependency("m3", "A组半决赛第1场", ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA)]),
                new ScheduledMatch(
                    6,
                    "2026-06-13",
                    new TimeOnly(15, 20),
                    new TimeOnly(15, 40),
                    "B3",
                    1,
                    "A组",
                    "5-8名半决赛",
                    "A组5-8名半决赛第1场",
                    "A组8进4第1场负者",
                    "周九",
                    MatchId: "m6",
                    Dependencies: [Dependency("m2", "A组8进4第1场", ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA)]),
                new ScheduledMatch(
                    7,
                    "2026-06-13",
                    new TimeOnly(16, 0),
                    new TimeOnly(16, 20),
                    "B3",
                    1,
                    "A组",
                    "5/6名赛",
                    "A组5/6名赛",
                    "A组5-8名半决赛第1场胜者",
                    "吴十",
                    MatchId: "m7",
                    Dependencies: [Dependency("m6", "A组5-8名半决赛第1场", ScheduleMatchDependencyOutcome.Winner, ScheduleMatchSide.SideA)]),
                new ScheduledMatch(
                    8,
                    "2026-06-13",
                    new TimeOnly(16, 0),
                    new TimeOnly(16, 20),
                    "B4",
                    1,
                    "A组",
                    "7/8名赛",
                    "A组7/8名赛",
                    "A组5-8名半决赛第1场负者",
                    "郑一",
                    MatchId: "m8",
                    Dependencies: [Dependency("m6", "A组5-8名半决赛第1场", ScheduleMatchDependencyOutcome.Loser, ScheduleMatchSide.SideA)])
            ],
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "B2", "B3", "B4"])],
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 4));

        var report = new ScheduleConstraintAnalyzer().Analyze(schedule);

        var issue = Assert.Single(report.Issues, issue =>
            issue.Type == ScheduleConstraintIssueType.DailyLoad
            && issue.PlayerName == "张三（20260001）");
        Assert.Equal(ScheduleConstraintIssueScope.Speculative, issue.Scope);
        Assert.Equal(ScheduleConstraintSeverity.Notice, issue.Severity);
        Assert.Contains("最高可能 4/4 场", issue.Message);
        Assert.Contains("概率约 50%", issue.Message);
        Assert.Contains("1场 50%", issue.Message);
        Assert.Contains("4场 50%", issue.Message);
    }
}
