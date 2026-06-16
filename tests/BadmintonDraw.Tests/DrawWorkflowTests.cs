using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using BadmintonDraw.Workflows;
using ClosedXML.Excel;
using SkiaSharp;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class DrawWorkflowTests
{
    [Fact]
    public void SameSeedCreatesSameResult()
    {
        var participants = CreateParticipants(12);
        var settings = CreateSettings(groupCount: 4, seed: "SZU-2026");
        var service = new DrawService();

        var first = service.Generate(participants, settings);
        var second = service.Generate(participants, settings);

        Assert.Equal(Signature(first.Groups), Signature(second.Groups));
    }

    [Fact]
    public void GroupsStayBalanced()
    {
        var participants = CreateParticipants(23);
        var result = new DrawService().Generate(participants, CreateSettings(groupCount: 4));
        var counts = result.Groups.Select(group => group.Count).ToArray();

        Assert.True(counts.Max() - counts.Min() <= 1);
    }

    [Fact]
    public void SeedRanksUseProtectedGroupPositions()
    {
        var participants = CreateParticipants(16).ToList();
        participants[0] = participants[0] with { IsSeed = true, SeedRank = 1 };
        participants[1] = participants[1] with { IsSeed = true, SeedRank = 2 };
        participants[2] = participants[2] with { IsSeed = true, SeedRank = 3 };
        participants[3] = participants[3] with { IsSeed = true, SeedRank = 4 };

        var result = new DrawService().Generate(participants, CreateSettings(groupCount: 4));

        Assert.Contains(result.Groups[0].Participants, participant => participant.SeedRank == 1);
        Assert.Contains(result.Groups[3].Participants, participant => participant.SeedRank == 2);
        Assert.Contains(result.Groups[1].Participants, participant => participant.SeedRank == 3);
        Assert.Contains(result.Groups[2].Participants, participant => participant.SeedRank == 4);
    }

    [Fact]
    public void DrawWorkflowGeneratesFromEditedParticipants()
    {
        var participants = CreateParticipants(16).ToList();
        participants[0] = participants[0] with { IsSeed = true, SeedRank = 1 };
        var request = new DrawWorkflowRequest(
            "名单已在界面导入",
            CompetitionMode.SinglesKnockout,
            EventKind.Singles,
            4,
            "seed-edited-participants",
            KnockoutGoal.OneQualifierPerGroup,
            PlacementPlayoff.None);

        var result = new DrawWorkflow().GenerateFromParticipants(request, participants);

        Assert.Contains(result.Result.Groups[0].Participants, participant => participant.SeedRank == 1);
        Assert.Equal(participants, result.Participants);
    }

    [Fact]
    public void MultipleSeedsInSameGroupUseProtectedBracketSlots()
    {
        var participants = CreateParticipants(16).ToList();
        participants[0] = participants[0] with { IsSeed = true, SeedRank = 1 };
        participants[1] = participants[1] with { IsSeed = true, SeedRank = 2 };
        participants[2] = participants[2] with { IsSeed = true, SeedRank = 3 };
        participants[3] = participants[3] with { IsSeed = true, SeedRank = 4 };
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));

        Assert.Equal(1, result.ByeGroups[0].Participants[0].SeedRank);
        Assert.Equal(2, result.ByeGroups[0].Participants[15].SeedRank);
        Assert.Equal(3, result.ByeGroups[0].Participants[4].SeedRank);
        Assert.Equal(4, result.ByeGroups[0].Participants[11].SeedRank);
    }

    [Fact]
    public void ExportedBracketSpacesMultipleSeedsWithinGroup()
    {
        var participants = CreateParticipants(16).ToList();
        participants[0] = participants[0] with { IsSeed = true, SeedRank = 1 };
        participants[1] = participants[1] with { IsSeed = true, SeedRank = 2 };
        participants[2] = participants[2] with { IsSeed = true, SeedRank = 3 };
        participants[3] = participants[3] with { IsSeed = true, SeedRank = 4 };
        var settings = CreateSettings(groupCount: 1, mode: CompetitionMode.SinglesKnockout);
        var result = new DrawService().Generate(participants, settings);
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-seed-slots-{Guid.NewGuid():N}.xlsx");

        try
        {
            new DrawResultExcelWriter().Write(outputPath, result, participants);

            using var workbook = new XLWorkbook(outputPath);
            var sheet = workbook.Worksheet("对阵表");
            Assert.Equal("选手01", sheet.Cell(6, 5).GetString());
            Assert.Equal("选手02", sheet.Cell(66, 5).GetString());
            Assert.Equal("选手03", sheet.Cell(22, 5).GetString());
            Assert.Equal("选手04", sheet.Cell(50, 5).GetString());
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Theory]
    [InlineData(16, "1,16,5,12")]
    [InlineData(32, "1,32,9,24,5,13,20,28")]
    [InlineData(64, "1,64,17,48,9,25,40,56,5,13,21,29,36,44,52,60")]
    [InlineData(128, "1,128,33,96,17,49,80,112,9,25,41,57,72,88,104,120")]
    [InlineData(256, "1,256,65,192,33,97,160,224,17,49,81,113,144,176,208,240,9,25,41,57,73,89,105,121,136,152,168,184,200,216,232,248")]
    public void OfficialSeedPositionTableIsUsed(int slotCount, string expectedPositions)
    {
        var expected = expectedPositions.Split(',').Select(int.Parse).ToArray();
        var actual = OfficialDrawRules.GetSeedPositionOrder(slotCount)
            .Take(expected.Length)
            .Select(position => position + 1)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SameUnitParticipantsAreSpreadAcrossGroupsWhenPossible()
    {
        var participants = new List<DrawParticipant>
        {
            new("甲01", TeamName: "计算机与软件学院"),
            new("甲02", TeamName: "计算机与软件学院"),
            new("乙01", TeamName: "管理学院"),
            new("乙02", TeamName: "经济学院"),
            new("乙03", TeamName: "法学院"),
            new("乙04", TeamName: "医学院"),
            new("乙05", TeamName: "传播学院"),
            new("乙06", TeamName: "体育学院")
        };

        var result = new DrawService().Generate(participants, CreateSettings(groupCount: 4));

        Assert.All(
            result.Groups,
            group => Assert.True(group.Participants.Count(participant => participant.TeamName == "计算机与软件学院") <= 1));
    }

    [Fact]
    public void KnockoutExportUsesFirstRoundTerminology()
    {
        var participants = CreateParticipants(5);
        var settings = CreateSettings(groupCount: 1, mode: CompetitionMode.SinglesKnockout);
        var result = new DrawService().Generate(participants, settings);
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-first-round-terminology-{Guid.NewGuid():N}.xlsx");

        try
        {
            new DrawResultExcelWriter().Write(outputPath, result, participants);

            using var workbook = new XLWorkbook(outputPath);
            var texts = workbook.Worksheet("对阵表").CellsUsed()
                .Select(cell => cell.GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();

            Assert.Contains(texts, text => text.Contains("首轮赛", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, text => text.Contains("附加赛", StringComparison.Ordinal));
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void SeedsAvoidPlayInWhenByeSlotsAreAvailable()
    {
        var participants = CreateParticipants(5).ToList();
        participants[0] = participants[0] with { IsSeed = true, SeedRank = 1 };
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));

        Assert.DoesNotContain(result.RoundOneGroups[0].Participants, participant => participant.IsSeed);
        Assert.Contains(result.ByeGroups[0].Participants, participant => participant.SeedRank == 1);
    }

    [Fact]
    public void ExtraSeedsEnterPlayInOnlyAfterByeSlotsAreFull()
    {
        var participants = CreateParticipants(7).ToList();
        participants[0] = participants[0] with { IsSeed = true, SeedRank = 1 };
        participants[1] = participants[1] with { IsSeed = true, SeedRank = 2 };
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));

        Assert.Single(result.ByeGroups[0].Participants, participant => participant.IsSeed);
        Assert.Single(result.RoundOneGroups[0].Participants, participant => participant.IsSeed);
    }

    [Fact]
    public void KnockoutUsesPerGroupRule()
    {
        var participants = CreateParticipants(7);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 2,
            mode: CompetitionMode.SinglesKnockout));

        var roundOneCounts = result.RoundOneGroups.Select(group => group.Count).Order().ToArray();
        var byeCounts = result.ByeGroups.Select(group => group.Count).Order().ToArray();

        Assert.Equal("0,2", string.Join(',', roundOneCounts));
        Assert.Equal("1,4", string.Join(',', byeCounts));
    }

    [Fact]
    public void PowerOfTwoChampionGoalExportsGroupedChampionBracket()
    {
        var participants = CreateParticipants(29);
        var settings = CreateSettings(
            groupCount: 8,
            mode: CompetitionMode.TeamKnockout,
            eventKind: EventKind.Team,
            knockoutGoal: KnockoutGoal.Champion);
        var result = new DrawService().Generate(participants, settings);
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-grouped-champion-{Guid.NewGuid():N}.xlsx");

        try
        {
            new DrawResultExcelWriter().Write(outputPath, result, participants);

            using var workbook = new XLWorkbook(outputPath);
            var sheet = workbook.Worksheet("对阵表");
            var headerValues = sheet.Row(4)
                .Cells(1, sheet.LastColumnUsed()!.ColumnNumber())
                .Select(cell => cell.GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();
            var usedTexts = sheet.CellsUsed()
                .Select(cell => cell.GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();

            Assert.Contains("出线", headerValues);
            Assert.Contains("8进4", headerValues);
            Assert.Contains("冠军", headerValues);
            Assert.Contains("26进13", headerValues);
            Assert.Contains("13进8", headerValues);
            Assert.DoesNotContain("3进1", headerValues);
            Assert.Equal(8, usedTexts.Count(text => Regex.IsMatch(text, @"^第\d+组出线$")));
            Assert.Contains(usedTexts, text => text == "冠军");
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void PowerOfTwoGroupCountCanStillExportOneQualifierPerGroup()
    {
        var participants = CreateParticipants(29);
        var settings = CreateSettings(
            groupCount: 8,
            mode: CompetitionMode.TeamKnockout,
            eventKind: EventKind.Team,
            knockoutGoal: KnockoutGoal.OneQualifierPerGroup);
        var result = new DrawService().Generate(participants, settings);
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-qualifiers-{Guid.NewGuid():N}.xlsx");

        try
        {
            new DrawResultExcelWriter().Write(outputPath, result, participants);

            using var workbook = new XLWorkbook(outputPath);
            var sheet = workbook.Worksheet("对阵表");
            var headerValues = sheet.Row(4)
                .Cells(1, sheet.LastColumnUsed()!.ColumnNumber())
                .Select(cell => cell.GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();
            var usedTexts = sheet.CellsUsed()
                .Select(cell => cell.GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();

            Assert.Contains("26进13", headerValues);
            Assert.Contains("13进8", headerValues);
            Assert.Contains("出线", headerValues);
            Assert.DoesNotContain("冠军", headerValues);
            Assert.DoesNotContain("4进2", headerValues);
            Assert.DoesNotContain("2进1", headerValues);
            Assert.DoesNotContain("3进1", headerValues);
            Assert.Equal(8, usedTexts.Count(text => Regex.IsMatch(text, @"^第\d+组出线$")));
            Assert.Equal(XLBorderStyleValues.Thin, sheet.Cell(6, 7).Style.Border.BottomBorder);
            Assert.Equal(XLBorderStyleValues.Thin, sheet.Cell(10, 7).Style.Border.TopBorder);
            var connectorRightBorders = sheet.RangeUsed(XLCellsUsedOptions.All)!.Cells()
                .Count(cell => string.IsNullOrWhiteSpace(cell.GetString())
                    && cell.Style.Border.RightBorder == XLBorderStyleValues.Thin);
            Assert.True(connectorRightBorders >= 3);
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void NonPowerOfTwoChampionGoalFallsBackToOneQualifierPerGroup()
    {
        var participants = CreateParticipants(29);
        var settings = CreateSettings(
            groupCount: 5,
            mode: CompetitionMode.TeamKnockout,
            eventKind: EventKind.Team,
            knockoutGoal: KnockoutGoal.Champion);
        var result = new DrawService().Generate(participants, settings);
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-non-power-qualifiers-{Guid.NewGuid():N}.xlsx");

        try
        {
            Assert.Equal(KnockoutGoal.OneQualifierPerGroup, result.Settings.KnockoutGoal);

            new DrawResultExcelWriter().Write(outputPath, result, participants);

            using var workbook = new XLWorkbook(outputPath);
            var sheet = workbook.Worksheet("对阵表");
            var usedTexts = sheet.CellsUsed()
                .Select(cell => cell.GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();

            Assert.Contains(usedTexts, text => text.Contains("5个小组出线名额", StringComparison.Ordinal));
            Assert.Equal(5, usedTexts.Count(text => Regex.IsMatch(text, @"^第\d+组出线$")));
            Assert.DoesNotContain("冠军", usedTexts);
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void PlacementPlayoffExportsAdditionalRankingMatches()
    {
        var participants = CreateParticipants(8);
        var settings = CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout,
            knockoutGoal: KnockoutGoal.Champion,
            placementPlayoff: PlacementPlayoff.ThirdToEighth);
        var result = new DrawService().Generate(participants, settings);
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-placement-playoff-{Guid.NewGuid():N}.xlsx");

        try
        {
            Assert.Equal(KnockoutGoal.Champion, result.Settings.KnockoutGoal);
            Assert.Equal(PlacementPlayoff.ThirdToEighth, result.Settings.PlacementPlayoff);

            new DrawResultExcelWriter().Write(outputPath, result, participants);

            using var workbook = new XLWorkbook(outputPath);
            var usedTexts = workbook.Worksheet("对阵表").CellsUsed()
                .Select(cell => cell.GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();

            Assert.Contains("名次附加赛", usedTexts);
            Assert.Contains("4强负者", usedTexts);
            Assert.Contains("8强负者", usedTexts);
            Assert.Contains("7,8名", usedTexts);
            Assert.Contains("5,6名", usedTexts);
            Assert.Contains(usedTexts, text => text.Contains("3/4名赛", StringComparison.Ordinal));
            Assert.Contains(usedTexts, text => text.Contains("5-8名半决赛第1场", StringComparison.Ordinal));
            Assert.Contains("A组半决赛第1场负者", usedTexts);
            Assert.Contains("A组8进4第4场负者", usedTexts);
            Assert.Contains(usedTexts, text => text.Contains("单淘汰赛只能产生第一、二名", StringComparison.Ordinal));
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

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
    public void SeedPlayersAreHighlightedInExportedWorkbook()
    {
        var participants = new List<DrawParticipant>
        {
            new("种子选手", IsSeed: true, SeedRank: 1),
            new("普通选手1"),
            new("普通选手2"),
            new("普通选手3")
        };
        var settings = CreateSettings(groupCount: 1, mode: CompetitionMode.SinglesKnockout);
        var result = new DrawService().Generate(participants, settings);
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-seed-style-{Guid.NewGuid():N}.xlsx");

        try
        {
            new DrawResultExcelWriter().Write(outputPath, result, participants);

            using var workbook = new XLWorkbook(outputPath);
            var bracketSeedCell = workbook.Worksheet("对阵表")
                .CellsUsed()
                .FirstOrDefault(cell => cell.GetString() == "种子选手");
            Assert.NotNull(bracketSeedCell);
            Assert.True(IsSeedFont(bracketSeedCell));

            var rosterSeedRow = workbook.Worksheet("原始名单").Row(2);
            Assert.Equal("种子选手", rosterSeedRow.Cell(1).GetString());
            Assert.True(IsSeedFont(rosterSeedRow.Cell(1)));
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void ExportedBracketUsesBlankCellBordersAsConnectors()
    {
        var participants = CreateParticipants(8);
        var settings = CreateSettings(groupCount: 1, mode: CompetitionMode.SinglesKnockout);
        var result = new DrawService().Generate(participants, settings);
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-connectors-{Guid.NewGuid():N}.xlsx");

        try
        {
            new DrawResultExcelWriter().Write(outputPath, result, participants);

            using var workbook = new XLWorkbook(outputPath);
            var sheet = workbook.Worksheet("对阵表");

            Assert.Equal(XLBorderStyleValues.Thin, sheet.Cell(6, 7).Style.Border.BottomBorder);
            Assert.Equal(XLBorderStyleValues.Thin, sheet.Cell(10, 7).Style.Border.TopBorder);
            Assert.Equal(XLBorderStyleValues.None, sheet.Cell(6, 8).Style.Border.RightBorder);
            Assert.Equal(XLBorderStyleValues.Thin, sheet.Cell(7, 8).Style.Border.RightBorder);
            Assert.Equal(XLBorderStyleValues.Thin, sheet.Cell(8, 8).Style.Border.RightBorder);
            Assert.Equal(XLBorderStyleValues.Thin, sheet.Cell(9, 8).Style.Border.RightBorder);
            Assert.Equal(XLBorderStyleValues.None, sheet.Cell(10, 8).Style.Border.RightBorder);
            Assert.Equal(XLBorderStyleValues.None, sheet.Cell(9, 8).Style.Border.TopBorder);
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void VisualWriterExportsImageAndPdfFormats()
    {
        var participants = CreateParticipants(8).ToList();
        participants[0] = participants[0] with { IsSeed = true, SeedRank = 1 };
        var settings = CreateSettings(groupCount: 2, mode: CompetitionMode.SinglesKnockout);
        var result = new DrawService().Generate(participants, settings);
        var workbookPath = Path.Combine(Path.GetTempPath(), $"badminton-bracket-source-{Guid.NewGuid():N}.xlsx");
        var writer = new DrawResultVisualWriter();
        var outputPaths = new[]
        {
            Path.Combine(Path.GetTempPath(), $"badminton-bracket-{Guid.NewGuid():N}.png"),
            Path.Combine(Path.GetTempPath(), $"badminton-bracket-{Guid.NewGuid():N}.jpg"),
            Path.Combine(Path.GetTempPath(), $"badminton-bracket-a4-{Guid.NewGuid():N}.pdf")
        };

        try
        {
            new DrawResultExcelWriter().Write(workbookPath, result, participants);

            writer.Write(outputPaths[0], workbookPath, "对阵表", DrawResultVisualFormat.Png);
            writer.Write(outputPaths[1], workbookPath, "对阵表", DrawResultVisualFormat.Jpeg);
            writer.Write(outputPaths[2], workbookPath, "对阵表", DrawResultVisualFormat.A4Pdf, new DrawResultVisualOptions(2, 2));

            AssertFileHeader(outputPaths[0], [0x89, 0x50, 0x4E, 0x47]);
            AssertFileHeader(outputPaths[1], [0xFF, 0xD8, 0xFF]);
            AssertFileHeader(outputPaths[2], [0x25, 0x50, 0x44, 0x46]);
            Assert.True(new FileInfo(outputPaths[0]).Length <= 20L * 1024L * 1024L);
            using var bitmap = SKBitmap.Decode(outputPaths[0]);
            Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
            AssertPdfUsesTextLayer(outputPaths[2]);
        }
        finally
        {
            DeleteIfExists(workbookPath);
            foreach (var outputPath in outputPaths)
            {
                DeleteIfExists(outputPath);
            }
        }
    }

    [Fact]
    public void VisualWriterKeepsBordersAboveAdjacentCellFills()
    {
        var workbookPath = Path.Combine(Path.GetTempPath(), $"badminton-border-source-{Guid.NewGuid():N}.xlsx");
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-border-{Guid.NewGuid():N}.png");

        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.Worksheets.Add("对阵表");
                sheet.Column(1).Width = 10;
                sheet.Column(2).Width = 10;
                sheet.Row(1).Height = 18;
                sheet.Cell(1, 1).Style.Border.RightBorder = XLBorderStyleValues.Thin;
                sheet.Cell(1, 1).Style.Border.RightBorderColor = XLColor.Black;
                sheet.Cell(1, 2).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFCC00");
                workbook.SaveAs(workbookPath);
            }

            new DrawResultVisualWriter().Write(outputPath, workbookPath, "对阵表", DrawResultVisualFormat.Png);

            using var bitmap = SKBitmap.Decode(outputPath);
            var logicalWidth = 18f * 2 + 10f * 8.3f * 2;
            var scale = bitmap.Width / logicalWidth;
            var sampleX = (int)Math.Round((18f + 10f * 8.3f) * scale);
            var sampleY = (int)Math.Round((18f + 18f * (96f / 72f) / 2f) * scale);
            var pixel = bitmap.GetPixel(sampleX, sampleY);

            Assert.True(pixel.Alpha > 240);
            Assert.True(pixel.Red < 32 && pixel.Green < 32 && pixel.Blue < 32);
        }
        finally
        {
            DeleteIfExists(workbookPath);
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void VisualWriterKeepsMergedRoundRobinTitleRightBorder()
    {
        var participants = CreateParticipants(29).ToList();
        var settings = CreateSettings(
            groupCount: 4,
            mode: CompetitionMode.TeamRoundRobin,
            eventKind: EventKind.Team);
        var result = new DrawService().Generate(participants, settings);
        var workbookPath = Path.Combine(Path.GetTempPath(), $"badminton-round-robin-border-source-{Guid.NewGuid():N}.xlsx");
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-round-robin-border-{Guid.NewGuid():N}.png");

        try
        {
            new DrawResultExcelWriter().Write(workbookPath, result, participants);
            new DrawResultVisualWriter().Write(outputPath, workbookPath, DrawResultVisualFormat.Png);

            using var workbook = new XLWorkbook(workbookPath);
            var sheet = workbook.Worksheets.First();
            var usedRange = sheet.RangeUsed(XLCellsUsedOptions.All)!;
            var lastColumn = usedRange.RangeAddress.LastAddress.ColumnNumber;
            var logicalWidth = 18f * 2 + Enumerable.Range(1, lastColumn)
                .Sum(column => (float)sheet.Column(column).Width * 8.3f);
            var titleMiddleY = 18f + (float)((sheet.Row(1).Height + sheet.Row(2).Height) * (96d / 72d) / 2d);

            using var bitmap = SKBitmap.Decode(outputPath);
            var scale = bitmap.Width / logicalWidth;
            var sampleX = Math.Clamp((int)Math.Round((logicalWidth - 18f) * scale), 0, bitmap.Width - 1);
            var sampleY = Math.Clamp((int)Math.Round(titleMiddleY * scale), 0, bitmap.Height - 1);
            var borderPixels = Enumerable.Range(-3, 7)
                .Select(offset => bitmap.GetPixel(Math.Clamp(sampleX + offset, 0, bitmap.Width - 1), sampleY));

            Assert.Contains(borderPixels, pixel =>
                pixel.Alpha > 240
                && pixel.Red < 180
                && pixel.Green < 180
                && pixel.Blue < 180);
        }
        finally
        {
            DeleteIfExists(workbookPath);
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void RoundRobinExportCreatesMatrixBracketSheet()
    {
        var participants = CreateParticipants(6).ToList();
        participants[0] = participants[0] with { IsSeed = true, SeedRank = 1 };
        var settings = CreateSettings(
            groupCount: 2,
            mode: CompetitionMode.TeamRoundRobin,
            eventKind: EventKind.Team);
        var result = new DrawService().Generate(participants, settings);
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-round-robin-{Guid.NewGuid():N}.xlsx");

        try
        {
            new DrawResultExcelWriter().Write(outputPath, result, participants);

            using var workbook = new XLWorkbook(outputPath);
            var sheet = workbook.Worksheet("对阵表");
            var firstGroup = result.Groups[0].Participants;
            var summaryColumn = firstGroup.Count + 2;

            Assert.DoesNotContain(workbook.Worksheets, worksheet => worksheet.Name == "总分组结果");
            Assert.Contains("团体循环赛对阵表", sheet.Cell(1, 1).GetString());
            Assert.DoesNotContain("同单位", sheet.Cell(4, 1).GetString(), StringComparison.Ordinal);
            Assert.Contains("赛程顺序按轮转法生成", sheet.Cell(4, 1).GetString(), StringComparison.Ordinal);
            Assert.Equal("A组", sheet.Cell(5, 1).GetString());
            Assert.Equal(firstGroup[0].DisplayName, sheet.Cell(5, 2).GetString());
            Assert.Equal(firstGroup[0].DisplayName, sheet.Cell(6, 1).GetString());
            Assert.Equal("—", sheet.Cell(6, 2).GetString());
            Assert.Equal("胜场", sheet.Cell(5, summaryColumn).GetString());
            Assert.Equal("净胜", sheet.Cell(5, summaryColumn + 1).GetString());
            Assert.Equal("名次", sheet.Cell(5, summaryColumn + 2).GetString());
            Assert.Contains(
                sheet.CellsUsed().Where(cell => cell.GetString() == "选手01"),
                IsSeedFont);
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void RoundRobinExportWritesScheduleAndPrioritizesSameUnitMatches()
    {
        var participants = new List<DrawParticipant>
        {
            new("计算机一队", TeamName: "计算机与软件学院"),
            new("计算机二队", TeamName: "计算机与软件学院"),
            new("管理学院", TeamName: "管理学院"),
            new("经济学院", TeamName: "经济学院")
        };
        var settings = CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesRoundRobin,
            eventKind: EventKind.Singles);
        var result = new DrawService().Generate(participants, settings);
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-round-robin-schedule-{Guid.NewGuid():N}.xlsx");

        try
        {
            new DrawResultExcelWriter().Write(outputPath, result, participants);

            using var workbook = new XLWorkbook(outputPath);
            var sheet = workbook.Worksheet("对阵表");
            var noteCell = sheet.CellsUsed()
                .FirstOrDefault(cell => cell.GetString() == "同单位优先");

            Assert.NotNull(noteCell);
            Assert.Equal("1", sheet.Cell(noteCell.Address.RowNumber, 1).GetString());
            Assert.Contains("赛程顺序", sheet.CellsUsed().Select(cell => cell.GetString()));
            Assert.Contains("第1场", sheet.CellsUsed().Select(cell => cell.GetString()));
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void RoundRobinExportKeepsLongTeamNamesReadable()
    {
        var participants = new List<DrawParticipant>
        {
            new("建筑与城市规划学院", TeamName: "建筑与城市规划学院"),
            new("深圳南特金融科技学院", TeamName: "深圳南特金融科技学院"),
            new("机电与控制工程学院", TeamName: "机电与控制工程学院"),
            new("化学与环境工程学院", TeamName: "化学与环境工程学院"),
            new("电子与信息工程学院", TeamName: "电子与信息工程学院"),
            new("生命与海洋科学学院", TeamName: "生命与海洋科学学院"),
            new("计算机与软件学院", TeamName: "计算机与软件学院"),
            new("政府管理学院", TeamName: "政府管理学院")
        };
        var settings = CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.TeamRoundRobin,
            eventKind: EventKind.Team);
        var result = new DrawService().Generate(participants, settings);
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-round-robin-long-names-{Guid.NewGuid():N}.xlsx");

        try
        {
            new DrawResultExcelWriter().Write(outputPath, result, participants);

            using var workbook = new XLWorkbook(outputPath);
            var sheet = workbook.Worksheet("对阵表");
            var scheduleTitle = sheet.CellsUsed()
                .First(cell => cell.GetString() == "赛程顺序");
            var firstScheduleDataRow = scheduleTitle.Address.RowNumber + 2;
            var opponentMerge = sheet.MergedRanges.FirstOrDefault(range =>
                range.RangeAddress.FirstAddress.RowNumber == firstScheduleDataRow
                && range.RangeAddress.FirstAddress.ColumnNumber == 3);

            Assert.True(sheet.Row(5).Height > 36);
            Assert.True(sheet.Row(6).Height > 30);
            Assert.True(sheet.Cell(5, 1).Style.Font.FontSize < 10);
            Assert.NotNull(opponentMerge);
            Assert.True(opponentMerge.RangeAddress.LastAddress.ColumnNumber > 4);
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void RoundRobinA4PdfExportsCoverAndOneLandscapePagePerGroup()
    {
        var participants = CreateParticipants(29).ToList();
        var settings = CreateSettings(
            groupCount: 4,
            mode: CompetitionMode.TeamRoundRobin,
            eventKind: EventKind.Team);
        var result = new DrawService().Generate(participants, settings);
        var workbookPath = Path.Combine(Path.GetTempPath(), $"badminton-round-robin-source-{Guid.NewGuid():N}.xlsx");
        var pdfPath = Path.Combine(Path.GetTempPath(), $"badminton-round-robin-a4-{Guid.NewGuid():N}.pdf");

        try
        {
            new DrawResultExcelWriter().Write(workbookPath, result, participants);
            new DrawResultVisualWriter().Write(
                pdfPath,
                workbookPath,
                "对阵表",
                DrawResultVisualFormat.A4Pdf,
                new DrawResultVisualOptions(9, 9));

            AssertFileHeader(pdfPath, [0x25, 0x50, 0x44, 0x46]);
            AssertPdfUsesTextLayer(pdfPath);
            Assert.Equal(5, CountPdfPages(pdfPath));
        }
        finally
        {
            DeleteIfExists(workbookPath);
            DeleteIfExists(pdfPath);
        }
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
    public void CrossEventConflictWorkflowExportsProgressReportWorkbook()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-cross-event-{Guid.NewGuid():N}");
        var firstProgressPath = Path.Combine(directory, "男单.szbd");
        var secondProgressPath = Path.Combine(directory, "混双.szbd");
        var reportPath = Path.Combine(directory, "跨项目选手冲突报告.xlsx");

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
            Assert.Contains("冲突汇总", workbook.Worksheets.Select(sheet => sheet.Name));
            Assert.Contains("严重冲突", workbook.Worksheets.Select(sheet => sheet.Name));
            Assert.Contains("输入赛事", workbook.Worksheets.Select(sheet => sheet.Name));
            Assert.Equal("严重冲突", workbook.Worksheet("严重冲突").Cell(2, 1).GetString());
            Assert.Equal("张三", workbook.Worksheet("严重冲突").Cell(2, 2).GetString());
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
                    CreateSingleMatchSchedule("混双1", "[张三 郑九]", "[钱十 吴一]", new TimeOnly(14, 10), new TimeOnly(14, 40), "C1")));

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

            var adjusted = workflow.MoveScheduleItem(
                board,
                mixedDoublesKey,
                "2026-06-13",
                new TimeOnly(15, 0),
                "C1");
            var saveResult = workflow.SaveScheduleBoard(adjusted);
            var reopened = store.Read(secondProgressPath);
            var adjustedPlayer = adjusted.PlayerDetails.Single(entry => entry.PlayerName == "张三");

            Assert.Equal(0, adjusted.BlockingConflictItemCount);
            Assert.True(adjusted.HasUnsavedChanges);
            Assert.Equal(0, adjustedPlayer.SevereIssueCount);
            Assert.Equal(0, adjustedPlayer.WarningIssueCount);
            Assert.Equal(30, adjustedPlayer.ShortestRestMinutes);
            Assert.Contains(
                adjustedPlayer.Appearances,
                appearance => appearance.EventName == "混双"
                              && appearance.StartTime == new TimeOnly(15, 0)
                              && appearance.ConflictSeverity is null);
            Assert.Equal(2, saveResult.UpdatedPaths.Count);
            Assert.Equal(new TimeOnly(15, 0), reopened.Snapshot.Schedule.Matches.Single().StartTime);
            Assert.Equal(new TimeOnly(15, 30), reopened.Snapshot.Schedule.Matches.Single().EndTime);
            Assert.Contains("C1", reopened.Snapshot.Schedule.Settings.Days.Single().Courts);
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
    public void ScheduleWorkflowMovesScheduledMatchAndRejectsOccupiedSlot()
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
        Assert.Throws<DrawValidationException>(() => ScheduleWorkflow.MoveScheduledMatch(
            schedule,
            "男单2",
            "2026-06-13",
            new TimeOnly(14, 0),
            "B1"));
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
            Assert.Equal(3, result.OutputPaths.Count);
            Assert.NotEqual(outputDirectory, result.OutputDirectory);
            Assert.True(Directory.Exists(result.OutputDirectory));
            Assert.All(result.OutputPaths, path => Assert.StartsWith(result.OutputDirectory, path, StringComparison.Ordinal));
            Assert.Contains(result.Schedule.Matches, match => match.MatchName == "男单 · 第1场");
            Assert.Contains(result.Schedule.Matches, match => match.MatchName == "混双 · 第1场");
            Assert.All(result.OutputPaths, path => Assert.True(File.Exists(path), path));
            Assert.Contains(result.OutputPaths, path => path.EndsWith("合并赛程记录表.xlsx", StringComparison.Ordinal));
            Assert.Contains(result.OutputPaths, path => path.EndsWith("合并赛程安排表.pdf", StringComparison.Ordinal));
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

    [Fact]
    public void TournamentProgressWorkflowExportsNextDayPackage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-next-package-{Guid.NewGuid():N}");

        try
        {
            var snapshot = CreateTournamentProgressSnapshot("tournament-package");
            var dayOne = snapshot.Schedule.Matches[0].DayLabel;
            var dayOneResults = BuildCompletedResultsForDay(snapshot.Schedule, dayOne);
            var state = new TournamentProgressState(
                snapshot,
                dayOneResults,
                [],
                [dayOne],
                []);

            var package = new TournamentProgressWorkflow().ExportNextDayPackage(
                state,
                directory,
                includePrintablePdf: false);
            var expectedScoreSheetCount = snapshot.Schedule.Matches.Count(match => match.DayLabel == package.DayLabel);
            var scorePdfPath = Path.Combine(package.OutputDirectory, "6月7日单场比赛计分表.pdf");

            Assert.Equal("2026-06-07", package.DayLabel);
            Assert.Equal(4, package.OutputPaths.Count);
            Assert.NotEqual(directory, package.OutputDirectory);
            Assert.True(Directory.Exists(package.OutputDirectory));
            Assert.All(package.OutputPaths, path => Assert.StartsWith(package.OutputDirectory, path, StringComparison.Ordinal));
            Assert.All(package.OutputPaths, path => Assert.True(File.Exists(path), path));
            Assert.Contains(package.OutputPaths, path => Path.GetFileName(path) == "6月7日赛程记录表.xlsx");
            Assert.Contains(package.OutputPaths, path => Path.GetFileName(path) == "6月7日赛程安排表.xlsx");
            Assert.Contains(package.OutputPaths, path => Path.GetFileName(path) == "6月7日带时间场地对阵表.xlsx");
            Assert.Contains(scorePdfPath, package.OutputPaths);

            using var recordWorkbook = new XLWorkbook(Path.Combine(package.OutputDirectory, "6月7日赛程记录表.xlsx"));
            using var scheduleWorkbook = new XLWorkbook(Path.Combine(package.OutputDirectory, "6月7日赛程安排表.xlsx"));
            Assert.Equal("对阵记录表", recordWorkbook.Worksheet("对阵记录表").Name);
            Assert.DoesNotContain(scheduleWorkbook.Worksheets, worksheet => worksheet.Name == "对阵记录表");
            Assert.Contains("2026-06-07", scheduleWorkbook.Worksheet("赛程明细").Cell(5, 2).GetString());
            AssertFileHeader(scorePdfPath, [0x25, 0x50, 0x44, 0x46]);
            AssertPdfUsesTextLayer(scorePdfPath);
            Assert.Equal(expectedScoreSheetCount, CountPdfPages(scorePdfPath));
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void TournamentProgressWorkflowExportsFirstDayPackage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-first-package-{Guid.NewGuid():N}");

        try
        {
            var snapshot = CreateTournamentProgressSnapshot("tournament-first-package");
            var state = new TournamentProgressState(
                snapshot,
                new Dictionary<string, MatchRecordResult>(StringComparer.Ordinal),
                [],
                [],
                []);

            var package = new TournamentProgressWorkflow().ExportFirstDayPackage(
                state,
                directory,
                includePrintablePdf: false);
            var expectedScoreSheetCount = snapshot.Schedule.Matches.Count(match => match.DayLabel == package.DayLabel);
            var scorePdfPath = Path.Combine(package.OutputDirectory, "6月6日单场比赛计分表.pdf");

            Assert.Equal("2026-06-06", package.DayLabel);
            Assert.Equal(4, package.OutputPaths.Count);
            Assert.NotEqual(directory, package.OutputDirectory);
            Assert.True(Directory.Exists(package.OutputDirectory));
            Assert.All(package.OutputPaths, path => Assert.StartsWith(package.OutputDirectory, path, StringComparison.Ordinal));
            Assert.All(package.OutputPaths, path => Assert.True(File.Exists(path), path));
            Assert.Contains(package.OutputPaths, path => Path.GetFileName(path) == "6月6日赛程记录表.xlsx");
            Assert.Contains(package.OutputPaths, path => Path.GetFileName(path) == "6月6日赛程安排表.xlsx");
            Assert.Contains(package.OutputPaths, path => Path.GetFileName(path) == "6月6日带时间场地对阵表.xlsx");
            Assert.Contains(scorePdfPath, package.OutputPaths);

            using var recordWorkbook = new XLWorkbook(Path.Combine(package.OutputDirectory, "6月6日赛程记录表.xlsx"));
            using var scheduleWorkbook = new XLWorkbook(Path.Combine(package.OutputDirectory, "6月6日赛程安排表.xlsx"));
            Assert.Equal("对阵记录表", recordWorkbook.Worksheet("对阵记录表").Name);
            Assert.DoesNotContain(scheduleWorkbook.Worksheets, worksheet => worksheet.Name == "对阵记录表");
            Assert.Contains("2026-06-06", scheduleWorkbook.Worksheet("赛程明细").Cell(5, 2).GetString());
            AssertFileHeader(scorePdfPath, [0x25, 0x50, 0x44, 0x46]);
            AssertPdfUsesTextLayer(scorePdfPath);
            Assert.Equal(expectedScoreSheetCount, CountPdfPages(scorePdfPath));
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void TournamentProgressWorkflowExportsTeamScoreSheetInNextDayPackage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-team-package-{Guid.NewGuid():N}");

        try
        {
            var participants = CreateParticipants(4);
            var result = new DrawService().Generate(
                participants,
                CreateSettings(
                    groupCount: 1,
                    mode: CompetitionMode.TeamKnockout,
                    eventKind: EventKind.Team,
                    knockoutGoal: KnockoutGoal.Champion));
            var schedule = new ScheduleService().Generate(
                result,
                new ScheduleSettings(
                    [
                        new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"]),
                        new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1"])
                    ],
                    MatchMinutes: 30,
                    MaxMatchesPerEntrantPerDay: 2));
            Assert.True(schedule.IsComplete);
            var now = DateTimeOffset.UtcNow;
            var dayOne = schedule.Matches[0].DayLabel;
            var state = new TournamentProgressState(
                new TournamentProgressSnapshot(
                    "team-package",
                    "校长杯团体",
                    now,
                    now,
                    "/tmp/校长杯团体参赛名单.xlsx",
                    result,
                    participants,
                    [],
                    schedule),
                BuildCompletedResultsForDay(schedule, dayOne),
                [],
                [dayOne],
                []);

            var package = new TournamentProgressWorkflow().ExportNextDayPackage(
                state,
                directory,
                includePrintablePdf: false);
            var teamScorePath = Path.Combine(package.OutputDirectory, "6月7日团体赛记分表.xlsx");

            Assert.Contains(teamScorePath, package.OutputPaths);
            Assert.True(File.Exists(teamScorePath));
            using var workbook = new XLWorkbook(teamScorePath);
            var sheet = workbook.Worksheet("团体记分表");
            Assert.Contains("团体赛记分表", sheet.Cell(1, 1).GetString());
            Assert.Equal("阶段", sheet.Cell(4, 1).GetString());
            Assert.Equal("分场记录", sheet.Cell(7, 1).GetString());
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void MatchRecordImportWarningListsEveryDetectedIssue()
    {
        var importResult = new MatchRecordImportResult(
            new Dictionary<string, MatchRecordResult>(StringComparer.Ordinal),
            ["2026-06-12"],
            ExpectedMatchCount: 4,
            MissingResultRows:
            [
                "序号 10 A组小组赛1 未填写胜方，已按待赛处理",
                "序号 11 A组小组赛2 未填写胜方，已按待赛处理"
            ],
            ValidationIssues:
            [
                "序号 12 A组小组赛3 已填写胜方但比分为空",
                "序号 13 A组小组赛4 胜方不在双方名单中"
            ]);

        var message = ScheduleWorkflow.BuildMatchRecordImportWarning(importResult, "2026-06-13");

        Assert.DoesNotContain("示例", message);
        Assert.Contains("详细问题", message);
        Assert.Contains("1. 序号 10 A组小组赛1 未填写胜方，已按待赛处理", message);
        Assert.Contains("2. 序号 11 A组小组赛2 未填写胜方，已按待赛处理", message);
        Assert.Contains("1. 序号 12 A组小组赛3 已填写胜方但比分为空", message);
        Assert.Contains("2. 序号 13 A组小组赛4 胜方不在双方名单中", message);
    }

    [Fact]
    public void TournamentProgressImportConfirmationListsEveryDetectedIssue()
    {
        var selectedImportResult = new MatchRecordImportResult(
            new Dictionary<string, MatchRecordResult>(StringComparer.Ordinal)
            {
                ["A组128进64第1场"] = new MatchRecordResult(
                    "A组128进64第1场",
                    "2026-06-12",
                    "甲",
                    "乙",
                    "15-12, 15-9",
                    "21m")
            },
            ["2026-06-12"],
            ExpectedMatchCount: 4,
            MissingResultRows:
            [
                "6月12日赛程记录表.xlsx：序号 78 A组128进64第1场 未填写胜方，已按待赛处理",
                "6月12日赛程记录表.xlsx：序号 79 A组128进64第2场 未填写胜方，已按待赛处理"
            ],
            ValidationIssues:
            [
                "6月12日赛程记录表.xlsx：序号 80 A组128进64第3场 已填写胜方但用时为空",
                "6月12日赛程记录表.xlsx：序号 81 A组128进64第4场 胜方不在双方名单中"
            ]);
        var correction = new TournamentProgressCorrection(
            "A组128进64第1场",
            new MatchRecordResult("A组128进64第1场", "2026-06-12", "甲", "乙", "15-10, 15-8", "18m"),
            new MatchRecordResult("A组128进64第1场", "2026-06-12", "甲", "乙", "15-12, 15-9", "21m"));
        var preview = new TournamentProgressImportPreview(
            selectedImportResult,
            selectedImportResult,
            [correction],
            ["已导入记录表.xlsx"],
            ["旧版记录表.xlsx 未带赛事标识，已按旧版记录表处理。"],
            NewResultCount: 1,
            FilesToImport: 1);

        var message = TournamentProgressWorkflow.BuildImportConfirmation(preview, "2026-06-13");

        Assert.DoesNotContain("示例", message);
        Assert.Contains("详细问题", message);
        Assert.Contains("1. 6月12日赛程记录表.xlsx：序号 78 A组128进64第1场 未填写胜方，已按待赛处理", message);
        Assert.Contains("2. 6月12日赛程记录表.xlsx：序号 79 A组128进64第2场 未填写胜方，已按待赛处理", message);
        Assert.Contains("1. 6月12日赛程记录表.xlsx：序号 80 A组128进64第3场 已填写胜方但用时为空", message);
        Assert.Contains("2. 6月12日赛程记录表.xlsx：序号 81 A组128进64第4场 胜方不在双方名单中", message);
        Assert.Contains("1. A组128进64第1场：原结果 胜方 甲，比分 15-10, 15-8，用时 18m，比赛日 2026-06-12 → 新结果 胜方 甲，比分 15-12, 15-9，用时 21m，比赛日 2026-06-12", message);
        Assert.Contains("1. 旧版记录表.xlsx 未带赛事标识，已按旧版记录表处理。", message);
        Assert.Contains("1. 已导入记录表.xlsx", message);
    }

    [Fact]
    public void TournamentProgressStoreCreatesAndRestoresSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-progress-create-{Guid.NewGuid():N}");
        var progressPath = Path.Combine(directory, "校长杯男单.szbd");

        try
        {
            var snapshot = CreateTournamentProgressSnapshot("tournament-create");
            var store = new TournamentProgressStore();

            var created = store.Create(progressPath, snapshot);
            var reopened = store.Read(progressPath);

            Assert.True(File.Exists(progressPath));
            Assert.Equal("tournament-create", created.Snapshot.TournamentId);
            Assert.Equal(snapshot.DrawResult.Audit.InputHash, reopened.Snapshot.DrawResult.Audit.InputHash);
            Assert.Equal(snapshot.Participants, reopened.Snapshot.Participants);
            Assert.Equal(snapshot.Schedule.Matches, reopened.Snapshot.Schedule.Matches);
            Assert.Empty(reopened.Results);
            Assert.Empty(reopened.ImportLogs);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void TournamentProgressStoreBackfillsLegacyScheduleDependencies()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-progress-legacy-dependencies-{Guid.NewGuid():N}");
        var progressPath = Path.Combine(directory, "旧版校长杯男单.szbd");

        try
        {
            var participants = CreateParticipants(4);
            var result = new DrawService().Generate(
                participants,
                CreateSettings(
                    groupCount: 1,
                    mode: CompetitionMode.SinglesKnockout,
                    knockoutGoal: KnockoutGoal.Champion));
            var schedule = new ScheduleService().Generate(
                result,
                new ScheduleSettings(
                    [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1", "A2", "A3"])],
                    MatchMinutes: 30,
                    MaxMatchesPerEntrantPerDay: 2));
            var now = DateTimeOffset.UtcNow;
            var snapshot = new TournamentProgressSnapshot(
                "legacy-dependencies",
                "旧版校长杯男单",
                now,
                now,
                "/tmp/旧版校长杯男单参赛名单.xlsx",
                result,
                participants,
                [],
                StripScheduleDependencies(schedule));
            var store = new TournamentProgressStore();

            store.Create(progressPath, snapshot);
            var reopened = store.Read(progressPath);
            var restoredSchedule = reopened.Snapshot.Schedule;
            var dependentMatch = restoredSchedule.Matches.Single(match => match.Dependencies.Count == 2);
            var sourceMatch = restoredSchedule.Matches.Single(match =>
                string.Equals(match.MatchId, dependentMatch.Dependencies[0].SourceMatchId, StringComparison.Ordinal));

            Assert.All(restoredSchedule.Matches, match => Assert.False(string.IsNullOrWhiteSpace(match.MatchId)));
            Assert.NotEqual(dependentMatch.MatchName, dependentMatch.MatchId);
            Assert.NotEmpty(dependentMatch.Dependencies);

            var exception = Assert.Throws<DrawValidationException>(() => ScheduleWorkflow.MoveScheduledMatch(
                restoredSchedule,
                dependentMatch.MatchName,
                sourceMatch.DayLabel,
                sourceMatch.StartTime,
                "A3"));
            Assert.Contains("赛程顺序错误", exception.Message);
            Assert.Contains("前序场次结束前开始", exception.Message);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void TournamentProgressStoreImportsOnceAndRejectsWinnerConflict()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-progress-import-{Guid.NewGuid():N}");
        var progressPath = Path.Combine(directory, "校长杯男单.szbd");
        var recordPath = Path.Combine(directory, "第一日记录表.xlsx");
        var conflictPath = Path.Combine(directory, "第一日冲突记录表.xlsx");

        try
        {
            var snapshot = CreateTournamentProgressSnapshot("tournament-import");
            var store = new TournamentProgressStore();
            store.Create(progressPath, snapshot);
            var dayLabel = snapshot.Schedule.Matches[0].DayLabel;
            var writer = new ScheduleExcelWriter();
            writer.WriteMatchRecord(recordPath, snapshot.Schedule, dayLabel, tournamentId: snapshot.TournamentId);
            FillMatchRecordWinners(recordPath, winnerOptionColumn: 15);

            var imported = store.Import(progressPath, [recordPath]);
            var duplicatePreview = store.PreviewImport(progressPath, [recordPath]);

            Assert.NotEmpty(imported.State.Results);
            Assert.Empty(imported.State.PendingMatchNames);
            Assert.Equal(
                snapshot.Schedule.Matches.Count - imported.State.Results.Count,
                imported.State.RemainingMatchCount);
            Assert.True(imported.State.RemainingMatchCount > 0);
            Assert.Single(imported.State.ImportLogs);
            Assert.Single(duplicatePreview.DuplicateFiles);
            Assert.Equal(0, duplicatePreview.FilesToImport);
            Assert.NotNull(imported.BackupPath);
            Assert.True(File.Exists(imported.BackupPath));

            writer.WriteMatchRecord(conflictPath, snapshot.Schedule, dayLabel, tournamentId: snapshot.TournamentId);
            FillMatchRecordWinners(conflictPath, winnerOptionColumn: 16);

            var error = Assert.Throws<TournamentProgressException>(() =>
                store.PreviewImport(progressPath, [conflictPath]));
            Assert.Contains("胜负方冲突", error.Message);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void TournamentProgressStoreRequiresConfirmationForResultCorrection()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-progress-correction-{Guid.NewGuid():N}");
        var progressPath = Path.Combine(directory, "校长杯男单.szbd");
        var firstPath = Path.Combine(directory, "第一日记录表.xlsx");
        var correctedPath = Path.Combine(directory, "第一日记录表_更正.xlsx");

        try
        {
            var snapshot = CreateTournamentProgressSnapshot("tournament-correction");
            var store = new TournamentProgressStore();
            store.Create(progressPath, snapshot);
            var dayLabel = snapshot.Schedule.Matches[0].DayLabel;
            var writer = new ScheduleExcelWriter();
            writer.WriteMatchRecord(firstPath, snapshot.Schedule, dayLabel, tournamentId: snapshot.TournamentId);
            FillMatchRecordWinners(firstPath, winnerOptionColumn: 15);
            store.Import(progressPath, [firstPath]);

            File.Copy(firstPath, correctedPath);
            using (var workbook = new XLWorkbook(correctedPath))
            {
                var sheet = workbook.Worksheet("对阵记录表");
                sheet.Cell(6, 10).Value = "25m";
                workbook.Save();
            }

            var preview = store.PreviewImport(progressPath, [correctedPath]);
            Assert.Single(preview.Corrections);
            Assert.Throws<TournamentProgressException>(() =>
                store.Import(progressPath, [correctedPath]));

            var corrected = store.Import(progressPath, [correctedPath], allowCorrections: true);
            Assert.Equal("25m", corrected.State.Results[preview.Corrections[0].MatchName].Duration);
            Assert.Equal(2, corrected.State.ImportLogs.Count);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void TournamentProgressStoreRejectsRecordFromAnotherTournament()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"badminton-progress-identity-{Guid.NewGuid():N}");
        var progressPath = Path.Combine(directory, "校长杯男单.szbd");
        var recordPath = Path.Combine(directory, "其他赛事记录表.xlsx");

        try
        {
            var snapshot = CreateTournamentProgressSnapshot("tournament-current");
            var store = new TournamentProgressStore();
            store.Create(progressPath, snapshot);
            new ScheduleExcelWriter().WriteMatchRecord(
                recordPath,
                snapshot.Schedule,
                snapshot.Schedule.Matches[0].DayLabel,
                tournamentId: "tournament-other");
            FillMatchRecordWinners(recordPath, winnerOptionColumn: 15);

            var error = Assert.Throws<TournamentProgressException>(() =>
                store.PreviewImport(progressPath, [recordPath]));

            Assert.Contains("不属于当前赛事存档", error.Message);
        }
        finally
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    [Fact]
    public void MatchRecordReaderCarriesResultsIntoNextDayRecord()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(18, 0), ["A1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var dayOnePath = Path.Combine(Path.GetTempPath(), $"badminton-record-day1-{Guid.NewGuid():N}.xlsx");
        var dayTwoPath = Path.Combine(Path.GetTempPath(), $"badminton-record-day2-{Guid.NewGuid():N}.xlsx");

        try
        {
            Assert.True(schedule.IsComplete);
            Assert.Contains(schedule.Matches, match => match.DayLabel == "2026-06-07"
                && match.SideA.Contains("胜者", StringComparison.Ordinal));

            var writer = new ScheduleExcelWriter();
            writer.WriteMatchRecord(dayOnePath, schedule, "2026-06-06");

            using (var workbook = new XLWorkbook(dayOnePath))
            {
                var sheet = workbook.Worksheet("对阵记录表");
                var lastRow = sheet.LastRowUsed()!.RowNumber();
                for (var row = 6; row <= lastRow; row++)
                {
                    var optionA = sheet.Cell(row, 15).GetString();
                    if (!string.IsNullOrWhiteSpace(optionA))
                    {
                        sheet.Cell(row, 9).Value = "15-10, 15-12";
                        sheet.Cell(row, 10).Value = "18m";
                        sheet.Cell(row, 12).Value = optionA;
                    }
                }

                workbook.Save();
            }

            var importResult = new MatchRecordReader().Read(dayOnePath);
            Assert.Equal(4, importResult.Results.Count);
            Assert.Contains("2026-06-06", importResult.DayLabels);

            writer.WriteMatchRecord(dayTwoPath, schedule, "2026-06-07", importResult.Results);

            using var nextWorkbook = new XLWorkbook(dayTwoPath);
            var recordSheet = nextWorkbook.Worksheet("对阵记录表");
            var nextText = string.Join(
                '\n',
                recordSheet.Range(6, 6, recordSheet.LastRowUsed()!.RowNumber(), 8)
                    .Cells()
                    .Select(cell => cell.GetString()));

            Assert.DoesNotContain("8进4", nextText, StringComparison.Ordinal);
            Assert.Contains("半决赛第1场胜者", nextText, StringComparison.Ordinal);
            Assert.Contains("选手", nextText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfExists(dayOnePath);
            DeleteIfExists(dayTwoPath);
        }
    }

    [Fact]
    public void MatchRecordReaderMergesMultipleRecordSheets()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 8), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 1));
        var dayOnePath = Path.Combine(Path.GetTempPath(), $"badminton-record-merge-day1-{Guid.NewGuid():N}.xlsx");
        var dayTwoPath = Path.Combine(Path.GetTempPath(), $"badminton-record-merge-day2-{Guid.NewGuid():N}.xlsx");
        var dayThreePath = Path.Combine(Path.GetTempPath(), $"badminton-record-merge-day3-{Guid.NewGuid():N}.xlsx");

        try
        {
            Assert.True(schedule.IsComplete);
            var writer = new ScheduleExcelWriter();
            writer.WriteMatchRecord(dayOnePath, schedule, "2026-06-06");
            FillMatchRecordWinners(dayOnePath, winnerOptionColumn: 15);

            var reader = new MatchRecordReader();
            var dayOneResults = reader.Read(dayOnePath);
            writer.WriteMatchRecord(dayTwoPath, schedule, "2026-06-07", dayOneResults.Results);
            FillMatchRecordWinners(dayTwoPath, winnerOptionColumn: 15);

            var mergedResults = reader.ReadMany([dayOnePath, dayTwoPath]);
            Assert.Equal(6, mergedResults.Results.Count);
            Assert.Contains("2026-06-06", mergedResults.DayLabels);
            Assert.Contains("2026-06-07", mergedResults.DayLabels);

            writer.WriteMatchRecord(dayThreePath, schedule, "2026-06-08", mergedResults.Results);

            using var workbook = new XLWorkbook(dayThreePath);
            var sheet = workbook.Worksheet("对阵记录表");
            var text = string.Join(
                '\n',
                sheet.Range(6, 6, sheet.LastRowUsed()!.RowNumber(), 8)
                    .Cells()
                    .Select(cell => cell.GetString()));

            Assert.DoesNotContain("胜者", text, StringComparison.Ordinal);
            Assert.Contains("选手", text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfExists(dayOnePath);
            DeleteIfExists(dayTwoPath);
            DeleteIfExists(dayThreePath);
        }
    }

    [Fact]
    public void MatchRecordReaderRejectsConflictingDuplicateResults()
    {
        var participants = CreateParticipants(2);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var firstPath = Path.Combine(Path.GetTempPath(), $"badminton-record-conflict-a-{Guid.NewGuid():N}.xlsx");
        var secondPath = Path.Combine(Path.GetTempPath(), $"badminton-record-conflict-b-{Guid.NewGuid():N}.xlsx");

        try
        {
            var writer = new ScheduleExcelWriter();
            writer.WriteMatchRecord(firstPath, schedule, "2026-06-06");
            writer.WriteMatchRecord(secondPath, schedule, "2026-06-06");
            FillMatchRecordWinners(firstPath, winnerOptionColumn: 15);
            FillMatchRecordWinners(secondPath, winnerOptionColumn: 16);

            var error = Assert.Throws<ExcelImportException>(() =>
                new MatchRecordReader().ReadMany([firstPath, secondPath]));

            Assert.Contains("胜方不一致", error.Message);
        }
        finally
        {
            DeleteIfExists(firstPath);
            DeleteIfExists(secondPath);
        }
    }

    [Fact]
    public void MatchRecordReaderReportsIncompleteRows()
    {
        var participants = CreateParticipants(2);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var recordPath = Path.Combine(Path.GetTempPath(), $"badminton-record-incomplete-{Guid.NewGuid():N}.xlsx");

        try
        {
            new ScheduleExcelWriter().WriteMatchRecord(recordPath, schedule, "2026-06-06");

            var importResult = new MatchRecordReader().Read(recordPath);

            Assert.False(importResult.IsComplete);
            Assert.Single(importResult.MissingResultRows);
            Assert.Contains("序号 1", importResult.MissingResultRows[0]);
            Assert.DoesNotContain("第 6 行", importResult.MissingResultRows[0]);
            Assert.Single(importResult.PendingMatchNames);
            Assert.Empty(importResult.Results);
        }
        finally
        {
            DeleteIfExists(recordPath);
        }
    }

    [Fact]
    public void MatchRecordReaderAllowsWinnerWithoutScoreForWalkover()
    {
        var participants = CreateParticipants(2);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var recordPath = Path.Combine(Path.GetTempPath(), $"badminton-record-walkover-{Guid.NewGuid():N}.xlsx");

        try
        {
            new ScheduleExcelWriter().WriteMatchRecord(recordPath, schedule, "2026-06-06");
            using (var workbook = new XLWorkbook(recordPath))
            {
                var sheet = workbook.Worksheet("对阵记录表");
                sheet.Cell(6, 12).Value = sheet.Cell(6, 15).GetString();
                workbook.Save();
            }

            var importResult = new MatchRecordReader().Read(recordPath);

            Assert.False(importResult.IsComplete);
            Assert.Empty(importResult.MissingResultRows);
            Assert.Empty(importResult.PendingMatchNames);
            Assert.Single(importResult.Results);
            Assert.Contains("未填写比分", importResult.ValidationIssues[0]);
            Assert.Contains("序号 1", importResult.ValidationIssues[0]);
            Assert.DoesNotContain("第 6 行", importResult.ValidationIssues[0]);
        }
        finally
        {
            DeleteIfExists(recordPath);
        }
    }

    [Fact]
    public void MatchRecordReaderReportsScoreWinnerMismatch()
    {
        var participants = CreateParticipants(2);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var recordPath = Path.Combine(Path.GetTempPath(), $"badminton-record-score-mismatch-{Guid.NewGuid():N}.xlsx");

        try
        {
            new ScheduleExcelWriter().WriteMatchRecord(recordPath, schedule, "2026-06-06");
            using (var workbook = new XLWorkbook(recordPath))
            {
                var sheet = workbook.Worksheet("对阵记录表");
                sheet.Cell(6, 9).Value = "15-10, 1-15, 11-15";
                sheet.Cell(6, 10).Value = "33m";
                sheet.Cell(6, 12).Value = sheet.Cell(6, 15).GetString();
                workbook.Save();
            }

            var importResult = new MatchRecordReader().Read(recordPath);

            Assert.False(importResult.IsComplete);
            Assert.Single(importResult.ValidationIssues);
            Assert.Contains("比分胜方为 B", importResult.ValidationIssues[0]);
            Assert.Contains("序号 1", importResult.ValidationIssues[0]);
            Assert.DoesNotContain("第 6 行", importResult.ValidationIssues[0]);
            Assert.Single(importResult.Results);
        }
        finally
        {
            DeleteIfExists(recordPath);
        }
    }

    [Fact]
    public void MatchRecordWriterCarriesPendingMatchesIntoNextDayRecord()
    {
        var participants = CreateParticipants(4);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(15, 0), ["A1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var dayOnePath = Path.Combine(Path.GetTempPath(), $"badminton-record-pending-day1-{Guid.NewGuid():N}.xlsx");
        var dayTwoPath = Path.Combine(Path.GetTempPath(), $"badminton-record-pending-day2-{Guid.NewGuid():N}.xlsx");

        try
        {
            new ScheduleExcelWriter().WriteMatchRecord(dayOnePath, schedule, "2026-06-06");
            var importResult = new MatchRecordReader().Read(dayOnePath);
            Assert.NotEmpty(importResult.PendingMatchNames);

            new ScheduleExcelWriter().WriteMatchRecord(
                dayTwoPath,
                schedule,
                "2026-06-07",
                importResult.Results,
                importResult.PendingMatchNames);

            using var workbook = new XLWorkbook(dayTwoPath);
            var sheet = workbook.Worksheet("对阵记录表");
            var firstPendingMatchName = importResult.PendingMatchNames[0];
            var row = Enumerable.Range(6, sheet.LastRowUsed()!.RowNumber() - 5)
                .First(index => sheet.Cell(index, 14).GetString() == firstPendingMatchName);

            Assert.Equal("2026-06-07", sheet.Cell(row, 2).GetString());
            Assert.Equal("待安排", sheet.Cell(row, 3).GetString());
            Assert.Equal("待安排", sheet.Cell(row, 11).GetString());
            Assert.Contains("顺延补赛", sheet.Cell(row, 13).GetString());
        }
        finally
        {
            DeleteIfExists(dayOnePath);
            DeleteIfExists(dayTwoPath);
        }
    }

    [Fact]
    public void ScheduleGridA4PdfSplitsOnePagePerCompetitionDay()
    {
        var participants = CreateParticipants(8);
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));
        var schedule = new ScheduleService().Generate(
            result,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 6), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 7), new TimeOnly(14, 0), new TimeOnly(16, 0), ["A1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        var workbookPath = Path.Combine(Path.GetTempPath(), $"badminton-schedule-source-{Guid.NewGuid():N}.xlsx");
        var pdfPath = Path.Combine(Path.GetTempPath(), $"badminton-schedule-a4-{Guid.NewGuid():N}.pdf");

        try
        {
            new ScheduleExcelWriter().Write(workbookPath, schedule);
            new DrawResultVisualWriter().Write(
                pdfPath,
                workbookPath,
                "时间场地网格",
                DrawResultVisualFormat.A4Pdf);

            AssertFileHeader(pdfPath, [0x25, 0x50, 0x44, 0x46]);
            AssertPdfUsesTextLayer(pdfPath);
            Assert.InRange(new FileInfo(pdfPath).Length, 1, 80L * 1024L * 1024L);
            Assert.Equal(schedule.DayCount, CountPdfPages(pdfPath));
        }
        finally
        {
            DeleteIfExists(workbookPath);
            DeleteIfExists(pdfPath);
        }
    }

    [Fact]
    public void DrawResultExcelWriterAnnotatesBracketWithScheduledTimes()
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
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-timed-bracket-{Guid.NewGuid():N}.xlsx");

        try
        {
            new DrawResultExcelWriter().Write(outputPath, result, participants, schedule);

            using var workbook = new XLWorkbook(outputPath);
            var bracketTexts = workbook.Worksheet("对阵表")
                .CellsUsed()
                .Select(cell => cell.GetString())
                .ToList();
            var timedWinnerCell = workbook.Worksheet("对阵表")
                .CellsUsed()
                .First(cell => cell.GetString().StartsWith("胜者", StringComparison.Ordinal)
                    && cell.GetString().Contains("2026-06-06 14:30-15:00", StringComparison.Ordinal)
                    && cell.GetString().Contains("A", StringComparison.Ordinal));

            Assert.Contains(bracketTexts, text => text.Contains("2026-06-06 14:00-14:30", StringComparison.Ordinal));
            Assert.Contains(bracketTexts, text => text.Contains("A1", StringComparison.Ordinal));
            Assert.Contains(bracketTexts, text => text.Contains("2026-06-07", StringComparison.Ordinal));
            Assert.True(timedWinnerCell.WorksheetRow().Height >= 40);
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void ParticipantTemplateHeaderIsReadable()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-template-{Guid.NewGuid():N}.xlsx");

        try
        {
            new ParticipantTemplateWriter().Write(outputPath);

            using var workbook = new XLWorkbook(outputPath);
            var sheet = workbook.Worksheet("参赛名单");
            var headers = sheet.Range("A1:I1").Cells().Select(cell => cell.GetString()).ToArray();
            Assert.Equal(["姓名", "学号", "学院/学部", "搭档姓名", "搭档学号", "搭档学院/学部", "是否种子", "种子序号", "备注"], headers);
            Assert.True(sheet.Row(1).Height >= 24);
            Assert.True(sheet.Row(2).Height >= 40);
            Assert.True(sheet.Column(2).Width >= 12);
            Assert.True(sheet.Column(3).Width >= 12);
            Assert.True(sheet.Column(6).Width >= 12);
            Assert.True(sheet.Column(9).Width >= 30);
            Assert.Contains("如为团体赛则仅填写C列学院/学部", sheet.Cell(4, 9).GetString());

            foreach (var cell in sheet.Range("A1:I4").Cells())
            {
                Assert.True(cell.Style.Alignment.WrapText);
                Assert.Equal(XLAlignmentVerticalValues.Center, cell.Style.Alignment.Vertical);
            }
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
    }

    [Fact]
    public void ReaderSupportsNewParticipantTemplateHeaders()
    {
        var doublesPath = Path.Combine(Path.GetTempPath(), $"badminton-new-template-doubles-{Guid.NewGuid():N}.xlsx");
        var teamPath = Path.Combine(Path.GetTempPath(), $"badminton-new-template-team-{Guid.NewGuid():N}.xlsx");

        try
        {
            WriteParticipantRowsWorkbook(
                doublesPath,
                new ParticipantWorkbookRow(
                    "张三",
                    PrimaryStudentId: "20260001",
                    TeamName: "计算机与软件学院",
                    PartnerName: "李四",
                    PartnerStudentId: "20260002",
                    PartnerTeamName: "管理学院"));
            WriteParticipantRowsWorkbook(
                teamPath,
                new ParticipantWorkbookRow("", TeamName: "经济学院"));

            var reader = new ParticipantExcelReader();
            Assert.Equal(EventKind.Doubles, reader.DetectEventKind(doublesPath));
            var doubles = reader.ReadParticipantsWithWarnings(doublesPath, EventKind.Doubles).Participants;
            Assert.Single(doubles);
            Assert.Equal("[张三 李四]", doubles[0].DisplayName);
            Assert.Equal("20260001", doubles[0].PrimaryStudentId);
            Assert.Equal("20260002", doubles[0].PartnerStudentId);
            Assert.Equal("计算机与软件学院", doubles[0].TeamName);
            Assert.Equal("管理学院", doubles[0].PartnerTeamName);

            Assert.Equal(EventKind.Team, reader.DetectEventKind(teamPath));
            var teams = reader.ReadParticipantsWithWarnings(teamPath, EventKind.Team).Participants;
            Assert.Single(teams);
            Assert.Equal("经济学院", teams[0].DisplayName);
            Assert.Equal("经济学院", teams[0].TeamName);
        }
        finally
        {
            DeleteIfExists(doublesPath);
            DeleteIfExists(teamPath);
        }
    }

    [Fact]
    public void ReaderDetectsParticipantEventKind()
    {
        var doublesPath = Path.Combine(Path.GetTempPath(), $"badminton-doubles-detect-{Guid.NewGuid():N}.xlsx");
        var singlesPath = Path.Combine(Path.GetTempPath(), $"badminton-singles-detect-{Guid.NewGuid():N}.xlsx");
        var teamPath = Path.Combine(Path.GetTempPath(), $"badminton-team-detect-{Guid.NewGuid():N}.xlsx");
        var teamWithContactPath = Path.Combine(Path.GetTempPath(), $"badminton-team-contact-detect-{Guid.NewGuid():N}.xlsx");

        try
        {
            WriteParticipantDetectionWorkbook(doublesPath, primaryName: "张三", partnerName: "李四", teamName: "计算机与软件学院");
            WriteParticipantDetectionWorkbook(singlesPath, primaryName: "王五", partnerName: "", teamName: "管理学院");
            WriteParticipantDetectionWorkbook(teamPath, primaryName: "", partnerName: "", teamName: "经济学院");
            WriteParticipantDetectionWorkbook(teamWithContactPath, primaryName: "赵队长", partnerName: "", teamName: "法学院");

            var reader = new ParticipantExcelReader();
            Assert.Equal(EventKind.Doubles, reader.DetectEventKind(doublesPath));
            Assert.True(reader.HasPartnerData(doublesPath));
            Assert.Equal(EventKind.Singles, reader.DetectEventKind(singlesPath));
            Assert.Equal(EventKind.Team, reader.DetectEventKind(teamPath));
            Assert.Equal(EventKind.Team, reader.DetectEventKind(teamWithContactPath, EventKind.Team));
            Assert.Equal(EventKind.Singles, reader.DetectEventKind(teamWithContactPath, EventKind.Singles));
        }
        finally
        {
            foreach (var path in new[] { doublesPath, singlesPath, teamPath, teamWithContactPath })
            {
                DeleteIfExists(path);
            }
        }
    }

    [Fact]
    public void ReaderRejectsNonExcelFileWithImportError()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"badminton-invalid-import-{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllText(invalidPath, "not an excel workbook");
            var reader = new ParticipantExcelReader();

            var detectError = Assert.Throws<ExcelImportException>(() => reader.DetectEventKind(invalidPath));
            var readError = Assert.Throws<ExcelImportException>(() => reader.ReadParticipants(invalidPath, EventKind.Singles));

            Assert.Contains(".xlsx", detectError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(".xlsx", readError.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteIfExists(invalidPath);
        }
    }

    [Fact]
    public void ReaderRejectsCorruptXlsxWithImportError()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"badminton-corrupt-import-{Guid.NewGuid():N}.xlsx");

        try
        {
            File.WriteAllText(invalidPath, "not an excel workbook");
            var reader = new ParticipantExcelReader();

            var error = Assert.Throws<ExcelImportException>(() => reader.ReadParticipants(invalidPath, EventKind.Singles));

            Assert.Contains("有效的 .xlsx", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteIfExists(invalidPath);
        }
    }

    [Fact]
    public void ReaderRejectsDuplicateSeedRanks()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"badminton-duplicate-seed-rank-{Guid.NewGuid():N}.xlsx");

        try
        {
            WriteParticipantRowsWorkbook(
                invalidPath,
                new ParticipantWorkbookRow("张三", SeedRank: "1"),
                new ParticipantWorkbookRow("李四", SeedRank: "1"));

            var error = Assert.Throws<ExcelImportException>(() =>
                new ParticipantExcelReader().ReadParticipants(invalidPath, EventKind.Singles));

            Assert.Contains("种子序号 1 重复", error.Message);
        }
        finally
        {
            DeleteIfExists(invalidPath);
        }
    }

    [Fact]
    public void ReaderRejectsSeedRankGreaterThanParticipantCount()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"badminton-overflow-seed-rank-{Guid.NewGuid():N}.xlsx");

        try
        {
            WriteParticipantRowsWorkbook(
                invalidPath,
                new ParticipantWorkbookRow("张三", SeedRank: "3"),
                new ParticipantWorkbookRow("李四"));

            var error = Assert.Throws<ExcelImportException>(() =>
                new ParticipantExcelReader().ReadParticipants(invalidPath, EventKind.Singles));

            Assert.Contains("不能大于当前参赛数量允许的种子数量 2", error.Message);
        }
        finally
        {
            DeleteIfExists(invalidPath);
        }
    }

    [Fact]
    public void ReaderRejectsInvalidSeedFlag()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"badminton-invalid-seed-flag-{Guid.NewGuid():N}.xlsx");

        try
        {
            WriteParticipantRowsWorkbook(
                invalidPath,
                new ParticipantWorkbookRow("张三", SeedFlag: "随便填"));

            var error = Assert.Throws<ExcelImportException>(() =>
                new ParticipantExcelReader().ReadParticipants(invalidPath, EventKind.Singles));

            Assert.Contains("是否种子", error.Message);
            Assert.Contains("是", error.Message);
            Assert.Contains("否", error.Message);
        }
        finally
        {
            DeleteIfExists(invalidPath);
        }
    }

    [Fact]
    public void ReaderWarnsDuplicateSinglesPlayerNameAndStillReturnsParticipants()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"badminton-duplicate-singles-name-{Guid.NewGuid():N}.xlsx");

        try
        {
            WriteParticipantRowsWorkbook(
                invalidPath,
                new ParticipantWorkbookRow("张三", PrimaryStudentId: "20260001"),
                new ParticipantWorkbookRow("张三", PrimaryStudentId: "20260002"));

            var result = new ParticipantExcelReader().ReadParticipantsWithWarnings(invalidPath, EventKind.Singles);
            var warning = Assert.Single(
                result.Warnings,
                warning => warning.Kind == ParticipantImportWarningKind.DuplicatePlayerName);

            Assert.Equal(2, result.Participants.Count);
            Assert.Contains("同名选手：张三", warning.Summary);
            Assert.Contains("第 2 行", warning.Detail);
            Assert.Contains("第 3 行", warning.Detail);
            Assert.Contains("学号 20260001", warning.Detail);
            Assert.Contains("学号 20260002", warning.Detail);
        }
        finally
        {
            DeleteIfExists(invalidPath);
        }
    }

    [Fact]
    public void ReaderWarnsAllDuplicatePlayerNameGroups()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"badminton-duplicate-name-groups-{Guid.NewGuid():N}.xlsx");

        try
        {
            WriteParticipantRowsWorkbook(
                invalidPath,
                new ParticipantWorkbookRow("张三"),
                new ParticipantWorkbookRow("张三"),
                new ParticipantWorkbookRow("李四"),
                new ParticipantWorkbookRow("李四"));

            var result = new ParticipantExcelReader().ReadParticipantsWithWarnings(invalidPath, EventKind.Singles);
            var duplicateWarnings = result.Warnings
                .Where(warning => warning.Kind == ParticipantImportWarningKind.DuplicatePlayerName)
                .ToList();

            Assert.Equal(4, result.Participants.Count);
            Assert.Equal(2, duplicateWarnings.Count);
            Assert.Contains(duplicateWarnings, warning => warning.Summary.Contains("张三", StringComparison.Ordinal));
            Assert.Contains(duplicateWarnings, warning => warning.Summary.Contains("李四", StringComparison.Ordinal));
        }
        finally
        {
            DeleteIfExists(invalidPath);
        }
    }

    [Fact]
    public void ReaderWarnsDuplicateDoublesPlayerNameAndStillReturnsParticipants()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"badminton-duplicate-doubles-name-{Guid.NewGuid():N}.xlsx");

        try
        {
            WriteParticipantRowsWorkbook(
                invalidPath,
                new ParticipantWorkbookRow("张三", PartnerName: "李四"),
                new ParticipantWorkbookRow("王五", PartnerName: "张三"));

            var result = new ParticipantExcelReader().ReadParticipantsWithWarnings(invalidPath, EventKind.Doubles);
            var warning = Assert.Single(
                result.Warnings,
                warning => warning.Kind == ParticipantImportWarningKind.DuplicatePlayerName);

            Assert.Equal(2, result.Participants.Count);
            Assert.Contains("同名选手：张三", warning.Summary);
            Assert.Contains("搭档", warning.Detail);
        }
        finally
        {
            DeleteIfExists(invalidPath);
        }
    }

    [Fact]
    public void ReaderWarnsUnrankedSeedAndStillMarksItAsSeed()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"badminton-unranked-seed-{Guid.NewGuid():N}.xlsx");

        try
        {
            WriteParticipantRowsWorkbook(
                invalidPath,
                new ParticipantWorkbookRow("张三", SeedFlag: "是"),
                new ParticipantWorkbookRow("李四"));

            var result = new ParticipantExcelReader().ReadParticipantsWithWarnings(invalidPath, EventKind.Singles);
            var warning = Assert.Single(
                result.Warnings,
                warning => warning.Kind == ParticipantImportWarningKind.UnrankedSeed);

            Assert.True(result.Participants[0].IsSeed);
            Assert.Null(result.Participants[0].SeedRank);
            Assert.Contains("种子未填写序号", warning.Summary);
        }
        finally
        {
            DeleteIfExists(invalidPath);
        }
    }

    [Fact]
    public void ReaderWarnsAllUnrankedSeeds()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"badminton-unranked-seeds-{Guid.NewGuid():N}.xlsx");

        try
        {
            WriteParticipantRowsWorkbook(
                invalidPath,
                new ParticipantWorkbookRow("张三", SeedFlag: "是"),
                new ParticipantWorkbookRow("李四", SeedFlag: "是"),
                new ParticipantWorkbookRow("王五"));

            var result = new ParticipantExcelReader().ReadParticipantsWithWarnings(invalidPath, EventKind.Singles);
            var unrankedSeedWarnings = result.Warnings
                .Where(warning => warning.Kind == ParticipantImportWarningKind.UnrankedSeed)
                .ToList();

            Assert.Equal(3, result.Participants.Count);
            Assert.Equal(2, unrankedSeedWarnings.Count);
            Assert.Contains(unrankedSeedWarnings, warning => warning.Detail.Contains("张三", StringComparison.Ordinal));
            Assert.Contains(unrankedSeedWarnings, warning => warning.Detail.Contains("李四", StringComparison.Ordinal));
        }
        finally
        {
            DeleteIfExists(invalidPath);
        }
    }

    [Fact]
    public void DrawServiceRejectsDuplicateSeedRanks()
    {
        var participants = new List<DrawParticipant>
        {
            new("张三", IsSeed: true, SeedRank: 1),
            new("李四", IsSeed: true, SeedRank: 1)
        };

        var error = Assert.Throws<DrawValidationException>(() =>
            new DrawService().Generate(participants, CreateSettings(groupCount: 1)));

        Assert.Contains("重复种子序号 1", error.Message);
    }

    [Fact]
    public void DrawServiceRejectsSeedRankGreaterThanParticipantCount()
    {
        var participants = new List<DrawParticipant>
        {
            new("张三", IsSeed: true, SeedRank: 3),
            new("李四")
        };

        var error = Assert.Throws<DrawValidationException>(() =>
            new DrawService().Generate(participants, CreateSettings(groupCount: 1)));

        Assert.Contains("不能大于当前参赛数量允许的种子数量 2", error.Message);
    }

    [Fact]
    public void DrawServiceRejectsSeedCountAboveOfficialLimit()
    {
        var participants = CreateParticipants(29).ToList();
        for (var index = 0; index < 5; index++)
        {
            participants[index] = participants[index] with { IsSeed = true, SeedRank = index + 1 };
        }

        var error = Assert.Throws<DrawValidationException>(() =>
            new DrawService().Generate(participants, CreateSettings(groupCount: 4)));

        Assert.Contains("最多设置 4 个种子", error.Message);
        Assert.Contains("设置了 5 个", error.Message);
    }

    [Fact]
    public void DrawServiceAllowsDuplicateDisplayNames()
    {
        var participants = new List<DrawParticipant>
        {
            new("张三", PrimaryName: "张三"),
            new("张三", PrimaryName: "张三")
        };

        var result = new DrawService().Generate(participants, CreateSettings(groupCount: 1));

        Assert.Equal(2, result.Audit.ParticipantCount);
    }

    [Fact]
    public void DrawServiceAllowsDuplicateDoublesPlayerName()
    {
        var participants = new List<DrawParticipant>
        {
            new("[张三 李四]", PrimaryName: "张三", PartnerName: "李四"),
            new("[王五 张三]", PrimaryName: "王五", PartnerName: "张三")
        };

        var result = new DrawService().Generate(
            participants,
            CreateSettings(groupCount: 1, eventKind: EventKind.Doubles));

        Assert.Equal(2, result.Audit.ParticipantCount);
    }

    private static void WriteParticipantDetectionWorkbook(
        string outputPath,
        string primaryName,
        string partnerName,
        string teamName)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("参赛名单");
        sheet.Cell(1, 1).Value = "姓名";
        sheet.Cell(1, 2).Value = "搭档";
        sheet.Cell(1, 3).Value = "队伍";
        sheet.Cell(2, 1).Value = primaryName;
        sheet.Cell(2, 2).Value = partnerName;
        sheet.Cell(2, 3).Value = teamName;
        workbook.SaveAs(outputPath);
    }

    private static void WriteParticipantRowsWorkbook(string outputPath, params ParticipantWorkbookRow[] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("参赛名单");
        sheet.Cell(1, 1).Value = "姓名";
        sheet.Cell(1, 2).Value = "学号";
        sheet.Cell(1, 3).Value = "学院/学部";
        sheet.Cell(1, 4).Value = "搭档姓名";
        sheet.Cell(1, 5).Value = "搭档学号";
        sheet.Cell(1, 6).Value = "搭档学院/学部";
        sheet.Cell(1, 7).Value = "是否种子";
        sheet.Cell(1, 8).Value = "种子序号";
        sheet.Cell(1, 9).Value = "备注";

        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var rowNumber = i + 2;
            sheet.Cell(rowNumber, 1).Value = row.PrimaryName;
            sheet.Cell(rowNumber, 2).Value = row.PrimaryStudentId;
            sheet.Cell(rowNumber, 3).Value = row.TeamName;
            sheet.Cell(rowNumber, 4).Value = row.PartnerName;
            sheet.Cell(rowNumber, 5).Value = row.PartnerStudentId;
            sheet.Cell(rowNumber, 6).Value = row.PartnerTeamName;
            sheet.Cell(rowNumber, 7).Value = row.SeedFlag;
            sheet.Cell(rowNumber, 8).Value = row.SeedRank;
            sheet.Cell(rowNumber, 9).Value = row.Note;
        }

        workbook.SaveAs(outputPath);
    }

    private static void FillMatchRecordWinners(string workbookPath, int winnerOptionColumn)
    {
        using var workbook = new XLWorkbook(workbookPath);
        var sheet = workbook.Worksheet("对阵记录表");
        var lastRow = sheet.LastRowUsed()!.RowNumber();
        for (var row = 6; row <= lastRow; row++)
        {
            var matchName = sheet.Cell(row, 14).GetString();
            if (string.IsNullOrWhiteSpace(matchName))
            {
                continue;
            }

            var winner = sheet.Cell(row, winnerOptionColumn).GetString();
            if (!string.IsNullOrWhiteSpace(winner))
            {
                sheet.Cell(row, 9).Value = winnerOptionColumn == 15 ? "15-10, 15-12" : "10-15, 12-15";
                sheet.Cell(row, 10).Value = "18m";
                sheet.Cell(row, 12).Value = winner;
            }
        }

        workbook.Save();
    }

    private static TournamentProgressSnapshot CreateTournamentProgressSnapshot(string tournamentId)
    {
        var participants = CreateParticipants(4);
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
                        new TimeOnly(15, 0),
                        ["A1"]),
                    new ScheduleDaySettings(
                        new DateOnly(2026, 6, 7),
                        new TimeOnly(14, 0),
                        new TimeOnly(16, 0),
                        ["A1"])
                ],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
        Assert.True(schedule.IsComplete);
        var now = DateTimeOffset.UtcNow;
        return new TournamentProgressSnapshot(
            tournamentId,
            "校长杯男单",
            now,
            now,
            "/tmp/校长杯男单参赛名单.xlsx",
            result,
            participants,
            [],
            schedule);
    }

    private static CrossEventScheduleSource CreateCrossEventSource(
        string eventName,
        params CrossEventScheduledMatch[] matches)
    {
        return new CrossEventScheduleSource(
            eventName,
            eventName,
            $"{eventName}.szbd",
            EventKind.Singles,
            matches);
    }

    private static CrossEventScheduledMatch CreateCrossEventMatch(
        int order,
        string matchName,
        string sideA,
        string sideB,
        TimeOnly start,
        TimeOnly end,
        string court,
        IReadOnlyList<string> sideAPlayers,
        IReadOnlyList<string> sideBPlayers)
    {
        return new CrossEventScheduledMatch(
            order,
            "2026-06-13",
            start,
            end,
            court,
            "A组",
            "首轮赛",
            matchName,
            sideA,
            sideB,
            sideAPlayers,
            sideBPlayers);
    }

    private static TournamentProgressSnapshot CreateManualProgressSnapshot(
        string eventName,
        IReadOnlyList<DrawParticipant> participants,
        SchedulePlan schedule)
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new DrawSettings(
            CompetitionMode.SinglesKnockout,
            participants.Any(participant => !string.IsNullOrWhiteSpace(participant.PartnerName))
                ? EventKind.Doubles
                : EventKind.Singles,
            1,
            "manual-test",
            KnockoutGoal: KnockoutGoal.Champion);
        var result = new DrawResult(
            [new DrawGroup(1, participants)],
            [],
            [new DrawGroup(1, participants)],
            settings,
            new DrawAuditInfo(
                DrawAlgorithmVersion.PerGroupPowerOfTwo,
                "manual-test",
                now,
                "manual",
                participants.Count,
                participants.Count(participant => participant.IsSeed),
                1));
        return new TournamentProgressSnapshot(
            Guid.NewGuid().ToString("N"),
            eventName,
            now,
            now,
            $"{eventName}参赛名单.xlsx",
            result,
            participants,
            [],
            schedule);
    }

    private static SchedulePlan CreateSingleMatchSchedule(
        string matchName,
        string sideA,
        string sideB,
        TimeOnly start,
        TimeOnly end,
        string court)
    {
        return new SchedulePlan(
            [
                new ScheduledMatch(
                    1,
                    "2026-06-13",
                    start,
                    end,
                    court,
                    1,
                    "A组",
                    "首轮赛",
                    matchName,
                    sideA,
                    sideB)
            ],
            new ScheduleSettings(
                [new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), [court])],
                MatchMinutes: 30,
                MaxMatchesPerEntrantPerDay: 2));
    }

    private static IReadOnlyDictionary<string, MatchRecordResult> BuildCompletedResultsForDay(
        SchedulePlan schedule,
        string dayLabel)
    {
        return schedule.Matches
            .Where(match => match.DayLabel == dayLabel)
            .ToDictionary(
                match => match.MatchName,
                match => new MatchRecordResult(
                    match.MatchName,
                    match.DayLabel,
                    ScheduleMatchTextForTest(match.SideA),
                    ScheduleMatchTextForTest(match.SideB),
                    "15-10, 15-12",
                    "18m"),
                StringComparer.Ordinal);
    }

    private static string ScheduleMatchTextForTest(string side)
    {
        var trimmed = side.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']'
            ? trimmed[1..^1].Trim()
            : trimmed;
    }

    private static IReadOnlyList<DrawParticipant> CreateParticipants(int count)
    {
        return Enumerable.Range(1, count)
            .Select(index => new DrawParticipant($"选手{index:D2}"))
            .ToList();
    }

    private static DrawSettings CreateSettings(
        int groupCount,
        string seed = "test-seed",
        CompetitionMode mode = CompetitionMode.SinglesRoundRobin,
        EventKind eventKind = EventKind.Singles,
        KnockoutGoal knockoutGoal = KnockoutGoal.OneQualifierPerGroup,
        PlacementPlayoff placementPlayoff = PlacementPlayoff.None)
    {
        return new DrawSettings(mode, eventKind, groupCount, seed, KnockoutGoal: knockoutGoal, PlacementPlayoff: placementPlayoff);
    }

    private static string Signature(IReadOnlyList<DrawGroup> groups)
    {
        return string.Join(';', groups.Select(group => string.Join(',', group.Participants.Select(participant => participant.DisplayName))));
    }

    private static bool IsSeedFont(IXLCell cell)
    {
        return cell.Style.Font.Bold
            && cell.Style.Font.FontColor.Color.ToArgb() == XLColor.FromHtml("#C00000").Color.ToArgb();
    }

    private static void AssertFileHeader(string path, IReadOnlyList<byte> expectedHeader)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > expectedHeader.Count);
        Assert.Equal(expectedHeader, bytes.Take(expectedHeader.Count).ToArray());
    }

    private static void AssertPdfUsesTextLayer(string path)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        Assert.Contains("/Font", text);
        Assert.Contains("/ToUnicode", text);
        Assert.DoesNotContain("/Subtype /Type3", text);
        Assert.DoesNotContain("/Subtype /Image", text);
    }

    private static void AssertWorkflowExportSet(
        IReadOnlyCollection<string> outputPaths,
        string basePath,
        int expectedPdfPages)
    {
        var expectedPaths = new[]
        {
            Path.ChangeExtension(basePath, ".xlsx"),
            Path.ChangeExtension(basePath, ".jpg"),
            Path.ChangeExtension(basePath, ".png"),
            Path.ChangeExtension(basePath, ".pdf")
        };

        Assert.Equal(
            expectedPaths.Order(StringComparer.OrdinalIgnoreCase),
            outputPaths.Order(StringComparer.OrdinalIgnoreCase));
        AssertFileHeader(Path.ChangeExtension(basePath, ".xlsx"), [0x50, 0x4B, 0x03, 0x04]);
        AssertFileHeader(Path.ChangeExtension(basePath, ".jpg"), [0xFF, 0xD8]);
        AssertFileHeader(Path.ChangeExtension(basePath, ".png"), [0x89, 0x50, 0x4E, 0x47]);
        AssertFileHeader(Path.ChangeExtension(basePath, ".pdf"), [0x25, 0x50, 0x44, 0x46]);
        AssertPdfUsesTextLayer(Path.ChangeExtension(basePath, ".pdf"));
        Assert.Equal(expectedPdfPages, CountPdfPages(Path.ChangeExtension(basePath, ".pdf")));
    }

    private static int CountPdfPages(string path)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        return Regex.Matches(text, @"/Type\s*/Page(?!s)").Count;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static SchedulePlan StripScheduleDependencies(SchedulePlan schedule)
    {
        return schedule with
        {
            Matches = schedule.Matches
                .Select(match => match with
                {
                    MatchId = "",
                    Dependencies = []
                })
                .ToList()
        };
    }

    private static ScheduleMatchDependency Dependency(
        string sourceMatchId,
        string sourceMatchName,
        ScheduleMatchDependencyOutcome outcome,
        ScheduleMatchSide side)
    {
        return new ScheduleMatchDependency(sourceMatchId, sourceMatchName, outcome, side);
    }

    private sealed record ParticipantWorkbookRow(
        string PrimaryName,
        string PrimaryStudentId = "",
        string PartnerName = "",
        string PartnerStudentId = "",
        string PartnerTeamName = "",
        string TeamName = "",
        string SeedFlag = "",
        string SeedRank = "",
        string Note = "");
}
