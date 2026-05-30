using BadmintonDraw.Core;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
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
    public void MultipleSeedsInSameGroupUseProtectedBracketSlots()
    {
        var participants = CreateParticipants(8).ToList();
        participants[0] = participants[0] with { IsSeed = true, SeedRank = 1 };
        participants[1] = participants[1] with { IsSeed = true, SeedRank = 2 };
        participants[2] = participants[2] with { IsSeed = true, SeedRank = 3 };
        participants[3] = participants[3] with { IsSeed = true, SeedRank = 4 };
        var result = new DrawService().Generate(participants, CreateSettings(
            groupCount: 1,
            mode: CompetitionMode.SinglesKnockout));

        Assert.Equal(1, result.ByeGroups[0].Participants[0].SeedRank);
        Assert.Equal(2, result.ByeGroups[0].Participants[7].SeedRank);
        Assert.Equal(3, result.ByeGroups[0].Participants[3].SeedRank);
        Assert.Equal(4, result.ByeGroups[0].Participants[4].SeedRank);
    }

    [Fact]
    public void ExportedBracketSpacesMultipleSeedsWithinGroup()
    {
        var participants = CreateParticipants(8).ToList();
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
            Assert.Equal("选手02", sheet.Cell(34, 5).GetString());
            Assert.Equal("选手03", sheet.Cell(18, 5).GetString());
            Assert.Equal("选手04", sheet.Cell(22, 5).GetString());
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
    public void PowerOfTwoBracketSplitsGroupHeaderAroundWinnerCells()
    {
        var participants = CreateParticipants(157);
        var settings = CreateSettings(groupCount: 8, mode: CompetitionMode.SinglesKnockout);
        var result = new DrawService().Generate(participants, settings);
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-draw-bracket-{Guid.NewGuid():N}.xlsx");

        try
        {
            new DrawResultExcelWriter().Write(outputPath, result, participants);

            using var workbook = new XLWorkbook(outputPath);
            var sheet = workbook.Worksheet("对阵表");
            var mergedRanges = sheet.MergedRanges
                .Select(range => range.RangeAddress.ToStringRelative())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var row in new[] { 69, 197, 325, 453 })
            {
                Assert.Contains($"A{row}:X{row}", mergedRanges);
                Assert.Contains($"Y{row}:Z{row}", mergedRanges);
                Assert.Contains($"AA{row}:AH{row}", mergedRanges);
                Assert.DoesNotContain($"A{row}:AH{row}", mergedRanges);
            }
        }
        finally
        {
            DeleteIfExists(outputPath);
        }
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
    public void ParticipantTemplateHeaderIsReadable()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"badminton-template-{Guid.NewGuid():N}.xlsx");

        try
        {
            new ParticipantTemplateWriter().Write(outputPath);

            using var workbook = new XLWorkbook(outputPath);
            var sheet = workbook.Worksheet("参赛名单");
            Assert.True(sheet.Row(1).Height >= 24);
            Assert.True(sheet.Row(2).Height >= 40);
            Assert.True(sheet.Column(4).Width >= 12);
            Assert.True(sheet.Column(5).Width >= 12);

            foreach (var cell in sheet.Range("A1:F4").Cells())
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

    private static IReadOnlyList<DrawParticipant> CreateParticipants(int count)
    {
        return Enumerable.Range(1, count)
            .Select(index => new DrawParticipant($"选手{index:D2}"))
            .ToList();
    }

    private static DrawSettings CreateSettings(
        int groupCount,
        string seed = "test-seed",
        CompetitionMode mode = CompetitionMode.SinglesRoundRobin)
    {
        return new DrawSettings(mode, EventKind.Singles, groupCount, seed);
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

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
