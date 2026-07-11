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
}
