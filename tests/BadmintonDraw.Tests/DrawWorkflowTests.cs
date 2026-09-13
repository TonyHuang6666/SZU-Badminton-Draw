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
    public void DrawServiceGeneratesFromEditedParticipants()
    {
        var participants = CreateParticipants(16).ToList();
        participants[0] = participants[0] with { IsSeed = true, SeedRank = 1 };
        var result = new DrawService().Generate(participants,
            new DrawSettings(CompetitionMode.SinglesKnockout, EventKind.Singles, 4,
                "seed-edited-participants", KnockoutGoal: KnockoutGoal.OneQualifierPerGroup));

        Assert.Contains(result.Groups[0].Participants, participant => participant.SeedRank == 1);
        Assert.Equal(16, result.Audit.ParticipantCount);
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
}
