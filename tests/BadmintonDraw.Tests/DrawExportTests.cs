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
}
