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
        IReadOnlyList<string> sideBPlayers,
        IReadOnlyList<CrossEventPlayerIdentity>? sideAPlayerIdentities = null,
        IReadOnlyList<CrossEventPlayerIdentity>? sideBPlayerIdentities = null)
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
            sideBPlayers,
            SideAPlayerIdentities: sideAPlayerIdentities,
            SideBPlayerIdentities: sideBPlayerIdentities);
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

    private static IReadOnlyList<DrawParticipant> CreateLooseParticipants(string prefix, int count)
    {
        return Enumerable.Range(1, count)
            .Select(index => new DrawParticipant($"{prefix}选手{index}", PrimaryName: $"{prefix}选手{index}"))
            .ToList();
    }

    private static SchedulePlan CreateThreeDayLooseSchedule(string prefix, int matchCount, string preferredCourt)
    {
        var matches = Enumerable.Range(1, matchCount)
            .Select(index => new ScheduledMatch(
                index,
                "2026-06-13",
                new TimeOnly(14, 0).AddMinutes((index - 1) * 20),
                new TimeOnly(14, 20).AddMinutes((index - 1) * 20),
                preferredCourt,
                1,
                "A组",
                "首轮赛",
                $"{prefix}第{index}场",
                $"{prefix}选手{index * 2 - 1}",
                $"{prefix}选手{index * 2}"))
            .ToList();
        return new SchedulePlan(
            matches,
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "B2", "B3"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 14), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "B2", "B3"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 15), new TimeOnly(14, 0), new TimeOnly(18, 0), ["B1", "B2", "B3"])
                ],
                MatchMinutes: 20,
                MaxMatchesPerEntrantPerDay: 3));
    }

    private static SchedulePlan CreateThreeDaySingleMatchSchedule(
        string matchName,
        string sideA,
        string sideB,
        string phase,
        string court)
    {
        return new SchedulePlan(
            [
                new ScheduledMatch(
                    1,
                    "2026-06-13",
                    new TimeOnly(14, 0),
                    new TimeOnly(14, 30),
                    court,
                    1,
                    "A组",
                    phase,
                    matchName,
                    sideA,
                    sideB)
            ],
            new ScheduleSettings(
                [
                    new ScheduleDaySettings(new DateOnly(2026, 6, 13), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1", "B2", "B3"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 14), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1", "B2", "B3"]),
                    new ScheduleDaySettings(new DateOnly(2026, 6, 15), new TimeOnly(14, 0), new TimeOnly(16, 0), ["B1", "B2", "B3"])
                ],
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
