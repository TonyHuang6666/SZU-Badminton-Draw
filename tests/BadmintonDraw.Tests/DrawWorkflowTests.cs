using BadmintonDraw.Core;
using BadmintonDraw.Excel;
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
    public void RoundRobinA4PdfExportsOneLandscapePagePerGroup()
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
            Assert.Equal(4, CountPdfPages(pdfPath));
        }
        finally
        {
            DeleteIfExists(workbookPath);
            DeleteIfExists(pdfPath);
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
            var headers = sheet.Range("A1:G1").Cells().Select(cell => cell.GetString()).ToArray();
            Assert.Equal(["姓名", "学院/学部", "搭档姓名", "搭档学院/学部", "是否种子", "种子序号", "备注"], headers);
            Assert.True(sheet.Row(1).Height >= 24);
            Assert.True(sheet.Row(2).Height >= 40);
            Assert.True(sheet.Column(2).Width >= 12);
            Assert.True(sheet.Column(4).Width >= 12);
            Assert.True(sheet.Column(5).Width >= 12);
            Assert.True(sheet.Column(7).Width >= 30);
            Assert.Contains("如为团体赛则仅填写B列学院/学部", sheet.Cell(4, 7).GetString());

            foreach (var cell in sheet.Range("A1:G4").Cells())
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
                    TeamName: "计算机与软件学院",
                    PartnerName: "李四",
                    PartnerTeamName: "管理学院"));
            WriteParticipantRowsWorkbook(
                teamPath,
                new ParticipantWorkbookRow("", TeamName: "经济学院"));

            var reader = new ParticipantExcelReader();
            Assert.Equal(EventKind.Doubles, reader.DetectEventKind(doublesPath));
            var doubles = reader.ReadParticipantsWithWarnings(doublesPath, EventKind.Doubles).Participants;
            Assert.Single(doubles);
            Assert.Equal("[张三 李四]", doubles[0].DisplayName);
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

            Assert.Contains("不能大于参赛单位总数 2", error.Message);
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
                new ParticipantWorkbookRow("张三"),
                new ParticipantWorkbookRow("张三"));

            var result = new ParticipantExcelReader().ReadParticipantsWithWarnings(invalidPath, EventKind.Singles);
            var warning = Assert.Single(
                result.Warnings,
                warning => warning.Kind == ParticipantImportWarningKind.DuplicatePlayerName);

            Assert.Equal(2, result.Participants.Count);
            Assert.Contains("同名选手：张三", warning.Summary);
            Assert.Contains("第 2 行", warning.Detail);
            Assert.Contains("第 3 行", warning.Detail);
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

        Assert.Contains("种子序号不能大于参赛人数或队伍数", error.Message);
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
        sheet.Cell(1, 2).Value = "学院/学部";
        sheet.Cell(1, 3).Value = "搭档姓名";
        sheet.Cell(1, 4).Value = "搭档学院/学部";
        sheet.Cell(1, 5).Value = "是否种子";
        sheet.Cell(1, 6).Value = "种子序号";
        sheet.Cell(1, 7).Value = "备注";

        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var rowNumber = i + 2;
            sheet.Cell(rowNumber, 1).Value = row.PrimaryName;
            sheet.Cell(rowNumber, 2).Value = row.TeamName;
            sheet.Cell(rowNumber, 3).Value = row.PartnerName;
            sheet.Cell(rowNumber, 4).Value = row.PartnerTeamName;
            sheet.Cell(rowNumber, 5).Value = row.SeedFlag;
            sheet.Cell(rowNumber, 6).Value = row.SeedRank;
            sheet.Cell(rowNumber, 7).Value = row.Note;
        }

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
        CompetitionMode mode = CompetitionMode.SinglesRoundRobin,
        EventKind eventKind = EventKind.Singles)
    {
        return new DrawSettings(mode, eventKind, groupCount, seed);
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
        Assert.DoesNotContain("/Subtype /Image", text);
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

    private sealed record ParticipantWorkbookRow(
        string PrimaryName,
        string PartnerName = "",
        string PartnerTeamName = "",
        string TeamName = "",
        string SeedFlag = "",
        string SeedRank = "",
        string Note = "");
}
