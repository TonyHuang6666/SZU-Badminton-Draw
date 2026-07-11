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
    public void ScheduleServiceAddsPlacementPlayoffMatches()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(
            participants,
            CreateSettings(
                groupCount: 1,
                mode: CompetitionMode.SinglesKnockout,
                knockoutGoal: KnockoutGoal.Champion,
                placementPlayoff: PlacementPlayoff.ThirdToEighth));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(18, 0), ["A1", "A2", "A3", "A4"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(18, 0), ["A1", "A2", "A3", "A4"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 6));
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-placement-schedule-{Guid.NewGuid():N}.xlsx");

        try
        {
            Assert.Equal(12, schedule.Matches.Count);
            Assert.Contains(schedule.Matches, match => match.MatchName == "3/4名赛"
                && match.SideA == "A组半决赛第1场负者"
                && match.SideB == "A组半决赛第2场负者");
            Assert.Contains(schedule.Matches, match => match.MatchName == "5/6名赛"
                && match.SideA == "5-8名半决赛第1场胜者"
                && match.SideB == "5-8名半决赛第2场胜者");
            Assert.Contains(schedule.Matches, match => match.MatchName == "7/8名赛"
                && match.SideA == "5-8名半决赛第1场负者"
                && match.SideB == "5-8名半决赛第2场负者");

            new ScheduleExcelWriter().Write(outputPath, schedule);

            using var workbook = new XLWorkbook(outputPath);
            var gridText = string.Join('\n', workbook.Worksheet("时间场地网格").CellsUsed().Select(cell => cell.GetString()));
            Assert.Contains("3/4名赛", gridText);
            Assert.Contains("负", gridText);
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void ScheduleServiceAllowsMutuallyExclusivePlacementFinalsOnSameDay()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(
            participants,
            CreateSettings(
                groupCount: 1,
                mode: CompetitionMode.SinglesKnockout,
                knockoutGoal: KnockoutGoal.Champion,
                placementPlayoff: PlacementPlayoff.ThirdToEighth));

        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(14, 30), ["A1", "A2", "A3", "A4"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 20), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1", "A2", "A3", "A4"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));

        Assert.True(schedule.IsComplete);
        Assert.Equal(12, schedule.Matches.Count);
        Assert.Empty(schedule.UnscheduledMatches);
        Assert.Contains(schedule.Matches, match => match.MatchName == "5/6名赛"
            && match.DayLabel == "2026-06-20"
            && match.TimeRange == "14:30-15:00");
        Assert.Contains(schedule.Matches, match => match.MatchName == "7/8名赛"
            && match.DayLabel == "2026-06-20"
            && match.TimeRange == "14:30-15:00");
    }

    [Fact]
    public void ScheduleServiceCompletesLargePlacementPlayoffWhenFinalDayHasEnoughTime()
    {
        var participants = CreateParticipants(159);
        var result = new DrawService().Generate(
            participants,
            CreateSettings(
                groupCount: 8,
                mode: CompetitionMode.SinglesKnockout,
                eventKind: EventKind.Doubles,
                knockoutGoal: KnockoutGoal.Champion,
                placementPlayoff: PlacementPlayoff.ThirdToEighth));
        var days = new[]
            {
                new DateOnly(2026, 6, 6),
                new DateOnly(2026, 6, 7),
                new DateOnly(2026, 6, 13),
                new DateOnly(2026, 6, 14),
                new DateOnly(2026, 6, 20)
            }
            .Select(day => new ScheduleDaySettings(
                day,
                new TimeOnly(14, 0),
                new TimeOnly(18, 0),
                Enumerable.Range(1, 16).Select(index => $"B{index}").ToList()))
            .ToList();

        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(days, MatchMinutes: 30, MaxMatchesPerEntrantPerDay: 2));

        Assert.True(schedule.IsComplete);
        Assert.Equal(163, schedule.TotalMatchCount);
        Assert.Equal(163, schedule.Matches.Count);
        Assert.Contains(schedule.Matches, match => match.MatchName == "5/6名赛");
        Assert.Contains(schedule.Matches, match => match.MatchName == "7/8名赛");
    }

    [Fact]
    public void ScheduleServiceUsesBoundaryTimingForKnockoutStages()
    {
        var participants = CreateParticipants(16);
        var result = new DrawService().Generate(
            participants,
            CreateSettings(
                groupCount: 1,
                mode: CompetitionMode.SinglesKnockout,
                knockoutGoal: KnockoutGoal.Champion));

        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(
                        new DateOnly(2026, 6, 6),
                        new TimeOnly(14, 0),
                        new TimeOnly(16, 0),
                        Enumerable.Range(1, 8).Select(index => $"A{index}").ToList()),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2,
                KnockoutTimingBoundaryEntrants: 8,
                BeforeBoundaryTiming: new ScheduleTimingSettings(MatchMinutes: 20, MaxMatchesPerEntrantPerDay: 3)));

        Assert.True(schedule.IsComplete);
        Assert.Contains(schedule.Matches, match => match.Phase == "16进8"
            && match.TimeRange == "14:00-14:20");
        Assert.Contains(schedule.Matches, match => match.Phase == "8进4"
            && match.TimeRange == "14:20-14:50");
    }

    [Fact]
    public void ScheduleServiceDoesNotAddBoundaryDailyLimitsOnSameDay()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(
            participants,
            CreateSettings(
                groupCount: 1,
                mode: CompetitionMode.SinglesKnockout,
                knockoutGoal: KnockoutGoal.Champion));

        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 14), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 15), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 1,
                KnockoutTimingBoundaryEntrants: 4,
                BeforeBoundaryTiming: new ScheduleTimingSettings(MatchMinutes: 20, MaxMatchesPerEntrantPerDay: 1)));

        Assert.True(schedule.IsComplete);
        Assert.All(
            schedule.Matches.Where(match => match.Phase == "半决赛"),
            match => Assert.NotEqual("2026-06-13", match.DayLabel));
        Assert.Contains(schedule.Matches, match => match.Phase == "半决赛" && match.DayLabel == "2026-06-14");
        Assert.Contains(schedule.Matches, match => match.Phase == "决赛" && match.DayLabel == "2026-06-15");
    }

    [Fact]
    public void ScheduleServiceClearsEarlierKnockoutStagesBeforeAdvancingDeeply()
    {
        var participants = CreateParticipants(159);
        var result = new DrawService().Generate(
            participants,
            CreateSettings(
                groupCount: 8,
                mode: CompetitionMode.SinglesKnockout,
                eventKind: EventKind.Doubles,
                knockoutGoal: KnockoutGoal.OneQualifierPerGroup));

        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(
                        new DateOnly(2026, 6, 6),
                        new TimeOnly(14, 0),
                        new TimeOnly(18, 0),
                        Enumerable.Range(1, 16).Select(index => $"B{index}").ToList())
                ],
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 6));

        Assert.True(schedule.IsComplete);

        var lastPlayInEnd = schedule.Matches
            .Where(match => match.Phase == "首轮赛")
            .Max(match => match.EndTime);
        var firstRoundOf64Start = schedule.Matches
            .Where(match => match.Phase == "64进32")
            .Min(match => match.StartTime);

        Assert.True(lastPlayInEnd <= firstRoundOf64Start);
    }

    [Fact]
    public void ScheduleServiceAvoidsUnavailableCourtWindows()
    {
        var participants = CreateParticipants(4);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout,
            knockoutGoal: KnockoutGoal.Champion));
        var blockedWindow = new ScheduleCourtAvailabilityBlock(new TimeOnly(14, 0), new TimeOnly(15, 0), ["B1"]);

        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(
                        new DateOnly(2026, 6, 13),
                        new TimeOnly(14, 0),
                        new TimeOnly(16, 0),
                        ["B1", "B2"],
                        UnavailableCourtWindows: [blockedWindow])
                ],
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 4,
                RefereeCount: 2));

        Assert.True(schedule.IsComplete);
        Assert.DoesNotContain(schedule.Matches, match =>
            string.Equals(match.Court, "B1", StringComparison.OrdinalIgnoreCase)
            && match.StartTime < blockedWindow.EndTime
            && blockedWindow.StartTime < match.EndTime);
        Assert.NotNull(schedule.QualityReport);
    }

    [Fact]
    public void ScheduleServiceRespectsTimeVaryingRefereeCapacity()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout,
            knockoutGoal: KnockoutGoal.Champion));
        var constrainedStart = new TimeOnly(14, 0);
        var constrainedEnd = new TimeOnly(15, 0);

        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(
                        new DateOnly(2026, 6, 13),
                        constrainedStart,
                        new TimeOnly(17, 0),
                        ["B1", "B2", "B3", "B4"],
                        RefereeCapacityWindows: [new ScheduleRefereeCapacityWindow(constrainedStart, constrainedEnd, 1)])
                ],
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 4,
                RefereeCount: 4));

        Assert.True(schedule.IsComplete);
        foreach (var match in schedule.Matches.Where(match => match.StartTime < constrainedEnd && constrainedStart < match.EndTime))
        {
            var overlapping = schedule.Matches.Count(other =>
                other.StartTime < match.EndTime
                && match.StartTime < other.EndTime
                && other.StartTime < constrainedEnd
                && constrainedStart < other.EndTime);
            Assert.True(overlapping <= 1);
        }
    }

    [Fact]
    public void ScheduleServiceKeepsPlacementPlayoffsNoLaterThanChampionFinal()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(
            participants,
            CreateSettings(
                groupCount: 1,
                mode: CompetitionMode.SinglesKnockout,
                knockoutGoal: KnockoutGoal.Champion,
                placementPlayoff: PlacementPlayoff.ThirdToEighth));

        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(14, 30), ["A1", "A2", "A3", "A4"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 20), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1", "A2", "A3"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 21), new TimeOnly(14, 0), new TimeOnly(14, 30), ["A1", "A2", "A3"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));

        Assert.True(schedule.IsComplete);

        var championFinal = schedule.Matches.Single(match => match.Note == "胜者为冠军");
        var championFinalDate = DateOnly.Parse(championFinal.DayLabel);
        var lastPlacementDate = schedule.Matches
            .Where(match => match.GroupName == PlacementPlayoffLabels.GroupName)
            .Select(match => DateOnly.Parse(match.DayLabel))
            .Max();

        Assert.Equal(new DateOnly(2026, 6, 21), championFinalDate);
        Assert.True(lastPlacementDate <= championFinalDate);
    }


    [Fact]
    public void ScheduleServiceAssignsKnockoutMatchesToCourtsAndTimes()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1", "A2"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));

        Assert.Equal(7, schedule.Matches.Count);
        Assert.Equal(2, schedule.DayCount);
        Assert.Equal("2026-06-06", schedule.Matches[0].DayLabel);
        Assert.Equal("2026-06-07", schedule.Matches[^1].DayLabel);
        Assert.Equal("A1", schedule.Matches[0].Court);
        Assert.Equal("A2", schedule.Matches[1].Court);
        Assert.Equal("14:00-14:30", schedule.Matches[0].TimeRange);
        Assert.Equal("14:30-15:00", schedule.Matches[2].TimeRange);
        Assert.Contains(schedule.Matches, match => match.Phase == "决赛");
        Assert.Contains(schedule.Matches, match => match.SideA.Contains("胜者", StringComparison.Ordinal));
        Assert.True(schedule.Matches.Single(match => match.Phase == "决赛").StartTime >= new TimeOnly(14, 0));
    }

    [Fact]
    public void ScheduleServiceExportsFullKnockoutPlaceholderTreeForLargeGroupedDraw()
    {
        var participants = CreateParticipants(159);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 8,
            mode: CompetitionMode.SinglesKnockout,
            eventKind: EventKind.Doubles,
            knockoutGoal: KnockoutGoal.OneQualifierPerGroup));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(18, 0), Enumerable.Range(1, 32).Select(index => $"场地{index}").ToList()),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(18, 0), Enumerable.Range(1, 32).Select(index => $"场地{index}").ToList()),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), Enumerable.Range(1, 16).Select(index => $"场地{index}").ToList()),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 14), new TimeOnly(14, 0), new TimeOnly(18, 0), Enumerable.Range(1, 8).Select(index => $"场地{index}").ToList())
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));

        Assert.Equal(151, schedule.Matches.Count);
        Assert.Contains(schedule.Matches, match => match.Phase == "首轮赛");
        Assert.Contains(schedule.Matches, match => match.Phase == "128进64");
        Assert.Contains(schedule.Matches, match => match.Phase == "64进32");
        Assert.Contains(schedule.Matches, match => match.Phase == "32进16");
        Assert.Contains(schedule.Matches, match => match.Phase == "16进8");
        Assert.Contains(schedule.Matches, match => match.MatchName.StartsWith("A组128进64", StringComparison.Ordinal));
        Assert.Contains(schedule.Matches, match => match.MatchName.StartsWith("A组16进8", StringComparison.Ordinal));
        Assert.DoesNotContain(schedule.Matches, match => match.MatchName.Contains("A组8进4", StringComparison.Ordinal));
        Assert.DoesNotContain(schedule.Matches, match => match.MatchName.Contains("A组半决赛", StringComparison.Ordinal));
        Assert.DoesNotContain(schedule.Matches, match => match.MatchName.Contains("A组决赛", StringComparison.Ordinal));
        Assert.Contains(schedule.Matches, match => match.Note == "胜者获得本组出线名额");
        Assert.Equal("2026-06-06", schedule.Matches[0].DayLabel);
        Assert.DoesNotContain(schedule.Matches, match => match.Phase == "决赛");
    }

    [Fact]
    public void ScheduleServiceUsesBracketHeaderPhasesForGroupedChampionDraw()
    {
        var participants = CreateParticipants(159);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 8,
            mode: CompetitionMode.SinglesKnockout,
            eventKind: EventKind.Doubles,
            knockoutGoal: KnockoutGoal.Champion));
        var days = Enumerable.Range(0, 8)
            .Select(index => new ScheduleDaySettings(
                new DateOnly(2026, 6, 6).AddDays(index),
                new TimeOnly(14, 0),
                new TimeOnly(18, 0),
                Enumerable.Range(1, 64).Select(court => $"场地{court}").ToList()))
            .ToList();
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(days, MatchMinutes: 30, MaxMatchesPerEntrantPerDay: 2));

        Assert.Equal(158, schedule.Matches.Count);
        Assert.Contains(schedule.Matches, match => match.Phase == "128进64");
        Assert.Contains(schedule.Matches, match => match.Phase == "64进32");
        Assert.Contains(schedule.Matches, match => match.Phase == "32进16");
        Assert.Contains(schedule.Matches, match => match.Phase == "16进8");
        Assert.Contains(schedule.Matches, match => match.Phase == "8进4");
        Assert.Contains(schedule.Matches, match => match.Phase == "4进2");
        Assert.Contains(schedule.Matches, match => match.Phase == "决赛");
        Assert.Contains(schedule.Matches, match => match.MatchName.StartsWith("总决赛8进4", StringComparison.Ordinal));
        Assert.Contains(schedule.Matches, match => match.MatchName.StartsWith("总决赛4进2", StringComparison.Ordinal));
        Assert.DoesNotContain(schedule.Matches, match => match.MatchName.Contains("总决赛半决赛", StringComparison.Ordinal));
    }

    [Fact]
    public void ScheduleServiceReturnsPartialPreviewWhenKnockoutResourcesAreInsufficient()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));

        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1", "A2"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));

        Assert.False(schedule.IsComplete);
        Assert.Equal(4, schedule.Matches.Count);
        Assert.Equal(3, schedule.UnscheduledMatches.Count);
        Assert.Equal(7, schedule.TotalMatchCount);
        Assert.Contains(schedule.UnscheduledMatches, match => match.Phase == "决赛");
        Assert.All(schedule.UnscheduledMatches, match => Assert.Contains("安排", match.Reason));
    }

    [Fact]
    public void ScheduleExcelWriterRejectsPartialSchedule()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1", "A2"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-partial-schedule-{Guid.NewGuid():N}.xlsx");

        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                new ScheduleExcelWriter().Write(outputPath, schedule));

            Assert.Contains("不支持导出不完整赛程", error.Message);
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void ScheduleServicePrioritizesSameUnitRoundRobinMatches()
    {
        var participants = new List<DrawParticipant>
        {
            new("计算机一队", TeamName: "计算机与软件学院"),
            new("计算机二队", TeamName: "计算机与软件学院"),
            new("管理学院", TeamName: "管理学院"),
            new("经济学院", TeamName: "经济学院")
        };
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesRoundRobin,
            eventKind: EventKind.Singles));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(18, 0), ["C1"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(18, 0), ["C1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));

        Assert.Equal(6, schedule.Matches.Count);
        Assert.True(schedule.Matches[0].SameUnit);
        Assert.Equal("同单位优先", schedule.Matches[0].Note);
    }

    [Fact]
    public void ScheduleWorkflowExpandsCourtRanges()
    {
        var courts = ScheduleWorkflow.ParseCourts("B1-B3 C1-C2 D4-5 B2");

        Assert.Equal(["B1", "B2", "B3", "C1", "C2", "D4", "D5"], courts);
    }

    [Fact]
    public void ScheduleWorkflowExpandsCrossPrefixCourtRanges()
    {
        var courts = ScheduleWorkflow.ParseCourts("B1-C8");

        Assert.Equal(16, courts.Count);
        Assert.Equal("B1", courts[0]);
        Assert.Equal("B8", courts[7]);
        Assert.Equal("C1", courts[8]);
        Assert.Equal("C8", courts[15]);
    }

    [Fact]
    public void ScheduleWorkflowBuildsMultiDaySplitTimingSettings()
    {
        var settings = ScheduleWorkflow.BuildSettings(
            [
                new ScheduleDayWorkflowRequest(new DateOnly(2026, 6, 10), new TimeOnly(14, 0), new TimeOnly(18, 0), "运动广场东馆羽毛球场", "B1-B2"),
                new ScheduleDayWorkflowRequest(new DateOnly(2026, 6, 11), new TimeOnly(14, 0), new TimeOnly(18, 0), "运动广场东馆羽毛球场", "C1-C2")
            ],
            matchMinutes: 30,
            maxMatchesPerEntrantPerDay: 2,
            knockoutTimingBoundaryEntrants: 8,
            beforeBoundaryMatchMinutes: 20,
            beforeBoundaryMaxMatchesPerEntrantPerDay: 3);

        Assert.Equal(2, settings.Days.Count);
        Assert.True(settings.HasKnockoutTimingSplit);
        Assert.Equal(20, settings.BeforeBoundaryTiming!.MatchMinutes);
        Assert.Equal(3, settings.BeforeBoundaryTiming.MaxMatchesPerEntrantPerDay);
        Assert.Equal(["B1", "B2"], settings.Days[0].Courts);
        Assert.Equal(["C1", "C2"], settings.Days[1].Courts);
    }

    [Fact]
    public void ScheduleWorkflowPassesUnavailableCourtWindowsIntoScheduleSettings()
    {
        var block = new ScheduleCourtAvailabilityBlock(new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1"]);
        var settings = ScheduleWorkflow.BuildSettings(
            [
                new ScheduleDayWorkflowRequest(
                    new DateOnly(2026, 6, 13),
                    new TimeOnly(14, 0),
                    new TimeOnly(16, 0),
                    "运动广场东馆羽毛球场",
                    "B1-B2",
                    [block])
            ],
            matchMinutes: 20,
            maxMatchesPerEntrantPerDay: 4,
            refereeCount: 2);

        Assert.Single(settings.Days);
        Assert.Same(block, settings.Days[0].UnavailableCourtWindows!.Single());
        Assert.Contains("资源 1 条", new ScheduleDayWorkflowRequest(
            new DateOnly(2026, 6, 13),
            new TimeOnly(14, 0),
            new TimeOnly(16, 0),
            "运动广场东馆羽毛球场",
            "B1-B2",
            [block]).CourtSummary);

        var participants = CreateParticipants(4);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout,
            knockoutGoal: KnockoutGoal.Champion));
        var schedule = new ScheduleService().Generate(result, settings);

        Assert.True(schedule.IsComplete);
        Assert.DoesNotContain(schedule.Matches, match =>
            string.Equals(match.Court, "B1", StringComparison.OrdinalIgnoreCase)
            && match.StartTime < block.EndTime
            && block.StartTime < match.EndTime);
    }

    [Fact]
    public void SingleEventAutoSchedulingStrategySpreadsLoadWhenBalancedRelaxed()
    {
        var participants = CreateParticipants(32);
        var result = new DrawService().Generate(
            participants,
            CreateSettings(
                groupCount: 1,
                mode: CompetitionMode.SinglesKnockout,
                knockoutGoal: KnockoutGoal.Champion));
        var days = new[]
        {
            new ScheduleDayWorkflowRequest(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), "运动广场东馆羽毛球场", "B1"),
            new ScheduleDayWorkflowRequest(new DateOnly(2026, 6, 14), new TimeOnly(14, 0), new TimeOnly(18, 0), "运动广场东馆羽毛球场", "B1"),
            new ScheduleDayWorkflowRequest(new DateOnly(2026, 6, 15), new TimeOnly(14, 0), new TimeOnly(18, 0), "运动广场东馆羽毛球场", "B1")
        };
        var compact = new ScheduleService().Generate(
            result,
            ScheduleWorkflow.BuildSettings(
                days,
                matchMinutes: 20,
                maxMatchesPerEntrantPerDay: 10,
                autoSchedulingStrategy: ScheduleAutoSchedulingStrategy.Compact));
        var balanced = new ScheduleService().Generate(
            result,
            ScheduleWorkflow.BuildSettings(
                days,
                matchMinutes: 20,
                maxMatchesPerEntrantPerDay: 10,
                autoSchedulingStrategy: ScheduleAutoSchedulingStrategy.BalancedRelaxed));

        var compactFirstDay = compact.Matches.Count(match => match.DayLabel == "2026-06-13");
        var balancedFirstDay = balanced.Matches.Count(match => match.DayLabel == "2026-06-13");
        var compactLastDay = compact.Matches.Count(match => match.DayLabel == "2026-06-15");
        var balancedLastDay = balanced.Matches.Count(match => match.DayLabel == "2026-06-15");

        Assert.True(compact.IsComplete);
        Assert.True(balanced.IsComplete);
        Assert.True(balancedFirstDay < compactFirstDay);
        Assert.True(balancedLastDay > compactLastDay);
    }

    [Fact]
    public void SingleEventCustomDayLoadTargetsRebalanceScheduleAcrossDays()
    {
        var participants = CreateParticipants(64);
        var result = new DrawService().Generate(
            participants,
            CreateSettings(
                groupCount: 1,
                mode: CompetitionMode.SinglesKnockout,
                knockoutGoal: KnockoutGoal.Champion));
        var days = new[]
        {
            new ScheduleDayWorkflowRequest(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), "运动广场东馆羽毛球场", "B1-B2"),
            new ScheduleDayWorkflowRequest(new DateOnly(2026, 6, 14), new TimeOnly(14, 0), new TimeOnly(18, 0), "运动广场东馆羽毛球场", "B1-B2"),
            new ScheduleDayWorkflowRequest(new DateOnly(2026, 6, 15), new TimeOnly(14, 0), new TimeOnly(18, 0), "运动广场东馆羽毛球场", "B1-B2")
        };
        var baseSettings = ScheduleWorkflow.BuildSettings(
            days,
            matchMinutes: 20,
            maxMatchesPerEntrantPerDay: 10,
            autoSchedulingStrategy: ScheduleAutoSchedulingStrategy.Custom);
        var frontLoaded = new ScheduleService().Generate(
            result,
            baseSettings with
            {
                DayLoadTargets =
                [
                    new ScheduleDayLoadTarget("2026-06-13", 1.0, 1.0),
                    new ScheduleDayLoadTarget("2026-06-14", 1.0, 1.0),
                    new ScheduleDayLoadTarget("2026-06-15", 0.75, 0.9)
                ],
                SynchronizeStageWaves = true,
                StageWaveTargets =
                [
                    new ScheduleStageWaveTarget("2026-06-13", 0.70),
                    new ScheduleStageWaveTarget("2026-06-14", 0.90),
                    new ScheduleStageWaveTarget("2026-06-15", 1.0)
                ]
            });
        var backLoaded = new ScheduleService().Generate(
            result,
            baseSettings with
            {
                DayLoadTargets =
                [
                    new ScheduleDayLoadTarget("2026-06-13", 0.20, 0.35),
                    new ScheduleDayLoadTarget("2026-06-14", 1.0, 1.0),
                    new ScheduleDayLoadTarget("2026-06-15", 1.0, 1.0)
                ],
                SynchronizeStageWaves = true,
                StageWaveTargets =
                [
                    new ScheduleStageWaveTarget("2026-06-13", 0.30),
                    new ScheduleStageWaveTarget("2026-06-14", 0.75),
                    new ScheduleStageWaveTarget("2026-06-15", 1.0)
                ]
            });

        var frontFirstDay = frontLoaded.Matches.Count(match => match.DayLabel == "2026-06-13");
        var backFirstDay = backLoaded.Matches.Count(match => match.DayLabel == "2026-06-13");

        Assert.True(frontLoaded.IsComplete);
        Assert.True(backLoaded.IsComplete);
        Assert.Equal(ScheduleAutoSchedulingStrategy.Custom, frontLoaded.Settings.AutoSchedulingStrategy);
        Assert.Equal(ScheduleAutoSchedulingStrategy.Custom, backLoaded.Settings.AutoSchedulingStrategy);
        Assert.True(frontFirstDay > backFirstDay);
        Assert.Empty(ScheduleDependencyGraph.Build(frontLoaded).FindOrderViolations());
        Assert.Empty(ScheduleDependencyGraph.Build(backLoaded).FindOrderViolations());
    }

    [Fact]
    public void SingleEventBalancedRelaxedSpreadsMatchesWithinEachDayWhenCapacityIsAbundant()
    {
        var participants = CreateParticipants(159);
        var result = new DrawService().Generate(
            participants,
            CreateSettings(
                groupCount: 8,
                mode: CompetitionMode.SinglesKnockout,
                knockoutGoal: KnockoutGoal.Champion,
                placementPlayoff: PlacementPlayoff.ThirdToEighth));
        var courts = Enumerable.Range(1, 8)
            .Select(index => $"B{index}")
            .Concat(Enumerable.Range(1, 8).Select(index => $"C{index}"))
            .ToList();
        var days = new[]
            {
                new DateOnly(2026, 6, 16),
                new DateOnly(2026, 6, 17),
                new DateOnly(2026, 6, 18),
                new DateOnly(2026, 6, 22)
            }
            .Select(day => new ScheduleDaySettings(
                day,
                new TimeOnly(14, 0),
                new TimeOnly(18, 0),
                courts))
            .ToList();

        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                days,
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 3,
                RefereeCount: 12)
            {
                AutoSchedulingStrategy = ScheduleAutoSchedulingStrategy.BalancedRelaxed
            });

        Assert.True(schedule.IsComplete);
        Assert.Empty(ScheduleDependencyGraph.Build(schedule).FindOrderViolations());
        Assert.All(
            schedule.Matches
                .GroupBy(match => (match.DayLabel, match.StartTime))
                .Select(group => group.Count()),
            count => Assert.True(count <= 12));
        var firstDayWaveCounts = schedule.Matches
            .Where(match => match.DayLabel == "2026-06-16")
            .GroupBy(match => match.StartTime)
            .Select(group => group.Count())
            .ToList();
        Assert.True(firstDayWaveCounts.Count <= 6);
        Assert.True(firstDayWaveCounts.Max() >= 10);
        var firstDayWaves = schedule.Matches
            .Where(match => match.DayLabel == "2026-06-16")
            .GroupBy(match => match.StartTime)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                Start = group.Key,
                End = group.Max(match => match.EndTime),
                DurationMinutes = group.Max(match => match.DurationMinutes)
            })
            .ToList();
        var gaps = firstDayWaves
            .Zip(firstDayWaves.Skip(1))
            .Select(pair => new
            {
                GapMinutes = (int)(pair.Second.Start - pair.First.End).TotalMinutes,
                pair.First.DurationMinutes
            })
            .ToList();
        Assert.Contains(gaps, gap => gap.GapMinutes > 0);
        Assert.All(gaps, gap => Assert.True(gap.GapMinutes <= gap.DurationMinutes));
    }

    [Fact]
    public void SingleEventScheduleRespectsRefereeCountWhenCourtsAreAbundant()
    {
        var participants = CreateParticipants(16);
        var result = new DrawService().Generate(
            participants,
            CreateSettings(
                groupCount: 1,
                mode: CompetitionMode.SinglesKnockout,
                knockoutGoal: KnockoutGoal.Champion));
        var schedule = new ScheduleService().Generate(
            result,
            ScheduleWorkflow.BuildSettings(
                [
                    new ScheduleDayWorkflowRequest(
                        new DateOnly(2026, 6, 13),
                        new TimeOnly(14, 0),
                        new TimeOnly(18, 0),
                        "运动广场东馆羽毛球场",
                        "B1-B8")
                ],
                matchMinutes: 20,
                maxMatchesPerEntrantPerDay: 10,
                autoSchedulingStrategy: ScheduleAutoSchedulingStrategy.Compact,
                refereeCount: 2));

        Assert.True(schedule.IsComplete);
        Assert.All(
            schedule.Matches
                .GroupBy(match => (match.DayLabel, match.StartTime))
                .Select(group => group.Count()),
            count => Assert.True(count <= 2));
    }
}
