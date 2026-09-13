using System.Text;
using BadmintonDraw.Core;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed partial class DrawWorkflowTests
{
    private static void WriteParticipantDetectionWorkbook(
        string outputPath, string primaryName, string partnerName, string teamName)
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
        string[] headers = ["姓名", "学号", "学院/学部", "搭档姓名", "搭档学号", "搭档学院/学部", "是否种子", "种子序号", "备注"];
        for (var column = 0; column < headers.Length; column++) sheet.Cell(1, column + 1).Value = headers[column];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            string[] values = [row.PrimaryName, row.PrimaryStudentId, row.TeamName, row.PartnerName,
                row.PartnerStudentId, row.PartnerTeamName, row.SeedFlag, row.SeedRank, row.Note];
            for (var column = 0; column < values.Length; column++) sheet.Cell(i + 2, column + 1).Value = values[column];
        }
        workbook.SaveAs(outputPath);
    }

    private static IReadOnlyList<DrawParticipant> CreateParticipants(int count) =>
        Enumerable.Range(1, count).Select(index => new DrawParticipant($"选手{index:D2}")).ToList();

    private static DrawSettings CreateSettings(int groupCount, string seed = "test-seed",
        CompetitionMode mode = CompetitionMode.SinglesRoundRobin, EventKind eventKind = EventKind.Singles,
        KnockoutGoal knockoutGoal = KnockoutGoal.OneQualifierPerGroup,
        PlacementPlayoff placementPlayoff = PlacementPlayoff.None) =>
        new(mode, eventKind, groupCount, seed, KnockoutGoal: knockoutGoal, PlacementPlayoff: placementPlayoff);

    private static string Signature(IReadOnlyList<DrawGroup> groups) =>
        string.Join(';', groups.Select(group => string.Join(',', group.Participants.Select(p => p.DisplayName))));

    private static bool IsSeedFont(IXLCell cell) => cell.Style.Font.Bold &&
        cell.Style.Font.FontColor.Color.ToArgb() == XLColor.FromHtml("#C00000").Color.ToArgb();

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

    private static int CountPdfPages(string path)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        return System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page(?!s)").Count;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record ParticipantWorkbookRow(string PrimaryName, string PrimaryStudentId = "",
        string PartnerName = "", string PartnerStudentId = "", string PartnerTeamName = "",
        string TeamName = "", string SeedFlag = "", string SeedRank = "", string Note = "");
}
