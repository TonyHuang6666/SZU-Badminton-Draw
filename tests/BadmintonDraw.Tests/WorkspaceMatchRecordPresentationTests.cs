using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using Xunit;
using static BadmintonDraw.Tests.WorkspaceResultImportTests;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceMatchRecordPresentationTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("workspace-record-presentation-").FullName;

    [Fact]
    public void DailyRecordKeepsTheLegacyPrintedFormAndRequiredResultFieldsOutsideIt()
    {
        var workspace = WorkspaceRecordExportTestData.Create();
        var context = new WorkspaceScheduleExportContext(workspace);
        var rows = context.MatchKeys.Where(k => workspace.Schedule!.Placements[k.MatchId].DayLabel == "2026-09-13")
            .Select(k => new WorkspaceRecordExportRow(k, new(2026, 9, 13))).ToArray();
        var path = Path.Combine(directory, "daily.xlsx");
        new WorkspaceMatchRecordWriter().Write(path, context, rows);
        using var book = new XLWorkbook(path); var sheet = book.Worksheet("对阵记录表");

        Assert.Equal("9月13日赛程记录表", sheet.Cell("A1").GetString());
        Assert.Equal(new[] { "序号", "日期", "时间", "进度", "组别", "对阵数据", "", "", "比分", "用时", "场地", "胜方", "备注" },
            Enumerable.Range(1, 13).Select(c => sheet.Cell(4, c).GetString()));
        Assert.Contains(sheet.MergedRanges, r => r.RangeAddress.ToString() == "F4:H4");
        Assert.Equal(16, sheet.Cell("A1").Style.Font.FontSize);
        Assert.Equal(24, sheet.Row(2).Height);
        Assert.True(sheet.Cell("A3").IsEmpty());
        Assert.Equal(42, sheet.Row(5).Height);
        Assert.Equal(42, sheet.Row(6).Height);
        Assert.Equal(15, sheet.Column(3).Width);
        Assert.Equal(12, sheet.Column(4).Width);
        Assert.Equal(24, sheet.Column(13).Width);
        Assert.DoesNotContain("\n", sheet.Cell("D6").GetString());
        Assert.Equal("15-10, 15-12", sheet.Cell("I5").GetString());
        Assert.Equal("18m", sheet.Cell("J5").GetString());
        Assert.Contains("结果类型", sheet.Cell("U4").GetString());
        Assert.Contains("实际比赛日期", sheet.Cell("V4").GetString());
        Assert.All(Enumerable.Range(14, 7), column => Assert.True(sheet.Column(column).IsHidden));
        Assert.False(sheet.Column("U").IsHidden); Assert.False(sheet.Column("V").IsHidden);
        Assert.Equal(13, Assert.Single(sheet.PageSetup.PrintAreas).RangeAddress.LastAddress.ColumnNumber);
        Assert.Equal(1, sheet.PageSetup.FirstRowToRepeatAtTop);
        Assert.Equal(4, sheet.PageSetup.LastRowToRepeatAtTop);
        Assert.Equal(XLPageOrientation.Landscape, sheet.PageSetup.PageOrientation);
        Assert.Equal(1, sheet.PageSetup.PagesWide);
        Assert.Equal(0, sheet.PageSetup.PagesTall);
    }

    [Fact]
    public void RecordMakesPhasesAndParticipantPairsReadableAndDistinguishesTheExampleFromEntryCells()
    {
        var workspace = DoublesWorkspace(); var project = workspace.Projects[0];
        var context = new WorkspaceScheduleExportContext(workspace);
        var path = Path.Combine(directory, "presentation.xlsx");
        new WorkspaceMatchRecordWriter().Write(path, context, project.MatchGraph!.Matches
            .Select(n => new WorkspaceRecordExportRow(new(project.Id, n.Id), new(2026, 9, 13))).ToArray());
        using var book = new XLWorkbook(path); var sheet = book.Worksheet("对阵记录表");

        Assert.Equal("A【Lee Wei\n王【甲】】", sheet.Cell("F6").GetString());
        Assert.True(sheet.Cell("F6").Style.Font.Bold);
        Assert.True(sheet.Cell("H6").Style.Font.Bold);
        Assert.True(sheet.Cell("F6").Style.Alignment.WrapText);
        Assert.Equal(XLAlignmentHorizontalValues.Center, sheet.Cell("F6").Style.Alignment.Horizontal);
        Assert.Equal(XLColor.FromHtml("#FCE4D6"), sheet.Cell("D6").Style.Fill.BackgroundColor);
        Assert.Equal(XLColor.FromHtml("#FFF2CC"), sheet.Cell("D8").Style.Fill.BackgroundColor);
        Assert.Equal(XLColor.FromHtml("#F2F2F2"), sheet.Cell("A5").Style.Fill.BackgroundColor);
        Assert.True(sheet.Cell("A5").Style.Font.Italic);
        Assert.All(new[] { "I6", "J6", "L6", "U6", "V6" }, address =>
            Assert.Equal(XLColor.White, sheet.Cell(address).Style.Fill.BackgroundColor));
        Assert.False(sheet.ShowGridLines);
    }

    [Fact]
    public void RecalculatedWinnerAndLoserChainsPreserveTypedPlayerLinesAndImportAfterSaving()
    {
        var workspace = DoublesWorkspace(); var project = workspace.Projects[0];
        var nodes = project.MatchGraph!.Matches; var context = new WorkspaceScheduleExportContext(workspace);
        var path = Path.Combine(directory, "linked-doubles.xlsx");
        new WorkspaceMatchRecordWriter().Write(path, context, nodes.Reverse().Select(n =>
            new WorkspaceRecordExportRow(new(project.Id, n.Id), DateOnly.Parse(workspace.Schedule!.Placements[n.Id].DayLabel))).ToArray());
        using (var book = new XLWorkbook(path))
        {
            var sheet = book.Worksheet("对阵记录表");
            var first = Row(sheet, nodes[0]); var second = Row(sheet, nodes[1]);
            var third = Row(sheet, nodes[2]); var last = Row(sheet, nodes[3]);
            Assert.Equal("A【第1场胜者】", sheet.Cell(third, 6).GetString());
            sheet.Cell(first, 12).Value = " a ";
            sheet.Cell(second, 12).Value = sheet.Cell(second, 16).GetString();
            sheet.Cell(third, 12).Value = "A";
            book.RecalculateAllFormulas();
            Assert.Equal("A【Lee Wei\n王【甲】】", sheet.Cell(third, 6).GetString());
            Assert.Equal("B【王三\n杨 四】", sheet.Cell(third, 8).GetString());
            Assert.Equal("A【Lee Wei\n王【甲】】", sheet.Cell(last, 6).GetString());
            Assert.Equal("B【王三\n杨 四】", sheet.Cell(last, 8).GetString());
            Assert.Equal("A【Lee Wei / 王【甲】】", sheet.Cell(third, 15).GetString());

            // A corrected upstream winner must update both levels; spaces inside one name remain intact.
            sheet.Cell(first, 12).Value = sheet.Cell(first, 16).GetString();
            book.RecalculateAllFormulas();
            Assert.Equal("A【Ada Lin\n李 乙】", sheet.Cell(third, 6).GetString());
            Assert.Equal("A【Ada Lin\n李 乙】", sheet.Cell(last, 6).GetString());
            sheet.Cell(third, 12).Value = sheet.Cell(third, 16).GetString();
            sheet.Cell(last, 12).Value = "A";
            book.RecalculateAllFormulas();
            Assert.Equal("A【王三\n杨 四】", sheet.Cell(last, 6).GetString());
            Assert.Equal("B【Ada Lin\n李 乙】", sheet.Cell(last, 8).GetString());
            Assert.All(sheet.CellsUsed().Where(c => c.HasFormula), c => Assert.NotEqual(XLDataType.Error, c.Value.Type));
            foreach (var node in nodes)
            {
                var row = Row(sheet, node);
                sheet.Cell(row, 9).Value = node == nodes[3] ? "21-10" : "10-21";
                sheet.Cell(row, 10).Value = 18; sheet.Cell(row, 21).Value = "正常";
                sheet.Cell(row, 22).Value = sheet.Cell(row, 2).GetString();
            }
            book.Save();
        }

        using (var reopened = new XLWorkbook(path))
        {
            reopened.RecalculateAllFormulas();
            var sheet = reopened.Worksheet("对阵记录表");
            Assert.Equal("A【王三\n杨 四】", sheet.Cell(Row(sheet, nodes[3]), 6).GetString());
            Assert.Equal("B【Ada Lin\n李 乙】", sheet.Cell(Row(sheet, nodes[3]), 8).GetString());
        }
        var document = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(System.IO.File.ReadAllBytes(path));
        var candidate = Ready(Evaluate(workspace, [new("linked-doubles.xlsx", path, document)]));
        Assert.Equal(4, candidate.Results.Count);
        Assert.Equal("王三 / 杨 四", candidate.Results[new(project.Id, nodes[3].Id)].Winner.DisplayName);
        Assert.Equal("Ada Lin / 李 乙", candidate.Results[new(project.Id, nodes[3].Id)].Loser.DisplayName);
    }

    private static TournamentWorkspace DoublesWorkspace()
    {
        var workspace = WorkspaceRecordExportTestData.Create(); var project = workspace.Projects[0];
        string[] firstNames = ["Lee Wei", "Ada Lin", "王三", "=1+1"];
        string[] partners = ["王【甲】", "李 乙", "杨 四", "某某胜者"];
        var roster = firstNames.Select((name, i) => new DrawParticipant(name + " / " + partners[i],
            PrimaryName: name, PartnerName: partners[i], PrimaryStudentId: "double-a-" + i,
            PartnerStudentId: "double-b-" + i)).ToArray();
        var entrants = roster.Select(p => ProjectEntrantIdentity.Create(EventDiscipline.MenDoubles, p)).ToArray();
        var nodes = project.MatchGraph!.Matches.ToArray();
        nodes[0] = nodes[0] with { SideA = entrants[0], SideB = entrants[1] };
        nodes[1] = nodes[1] with { SideA = entrants[2], SideB = entrants[3] };
        nodes[2] = nodes[2] with { Phase = "决赛" };
        nodes[3] = nodes[3] with { SideA = new EntrantSource.WinnerOf(nodes[2].Id),
            SideB = new EntrantSource.LoserOf(nodes[2].Id), Dependencies = [nodes[2].Id] };
        var draw = project.Draw!.Result;
        project = project with { Discipline = EventDiscipline.MenDoubles, DisplayName = "男双",
            Roster = project.Roster! with { Participants = roster },
            Draw = project.Draw with { Result = draw with { Settings = draw.Settings with { EventKind = EventKind.Doubles }, Groups = [new(1, roster)] } },
            MatchGraph = project.MatchGraph with { Matches = nodes } };
        return workspace with { Projects = [project, workspace.Projects[1]] };
    }

    private static int Row(IXLWorksheet sheet, MatchNode node) => sheet.Column(14).CellsUsed()
        .Single(c => c.GetString() == node.Id.ToString("D")).Address.RowNumber;
    public void Dispose() => Directory.Delete(directory, true);
}
