using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceTimedDrawWriterTests
{
    [Fact]
    public void TeamRoundRobinMatrixAndListUseTheActualGraphPairTiming()
    {
        using var fixture = new WorkspaceTimedDrawTestFixture(CompetitionMode.TeamRoundRobin, 4);
        using var workbook = new XLWorkbook(fixture.Export()); var sheet = workbook.Worksheet("对阵表");
        var group = Assert.Single(fixture.Draw.Groups);
        var identities = group.Participants.Select(p => ProjectEntrantIdentity.Create(EventDiscipline.Team, p).IdentityKey).ToArray();
        foreach (var node in fixture.Project.MatchGraph!.Matches)
        {
            var first = Array.IndexOf(identities, Assert.IsType<EntrantSource.Participant>(node.SideA).IdentityKey);
            var second = Array.IndexOf(identities, Assert.IsType<EntrantSource.Participant>(node.SideB).IdentityKey);
            Assert.True(first >= 0 && second >= 0);
            var matrix = sheet.Cell(6 + Math.Min(first, second), 2 + Math.Max(first, second)).GetString();
            var expected = WorkspaceTimedDrawTestFixture.TimeText(fixture.Placement(node));
            Assert.Contains(expected, matrix);
            var pairText = $"{group.Participants[first].DisplayName}  vs  {group.Participants[second].DisplayName}";
            var pairCell = Assert.Single(sheet.CellsUsed(), cell => cell.GetString().StartsWith(pairText, StringComparison.Ordinal));
            Assert.Contains(expected, pairCell.GetString());
        }
    }

    [Fact]
    public void UnequalQualifierShortGroupFinalIsTimedAtItsActualAdvanceCell()
    {
        using var fixture = new WorkspaceTimedDrawTestFixture(CompetitionMode.SinglesKnockout, 7, 2);
        using var workbook = new XLWorkbook(fixture.Export()); var sheet = workbook.Worksheet("对阵表");
        var shortGroup = fixture.Draw.Groups.Single(g => g.Participants.Count == 3);
        var final = fixture.Project.MatchGraph!.Matches.Where(n => n.GroupNumber == shortGroup.Number).OrderBy(n => n.Order).Last();
        Assert.Equal("6进3", final.Phase);
        var advance = Assert.Single(sheet.CellsUsed(), cell => cell.GetString().StartsWith($"第{shortGroup.Number}组出线", StringComparison.Ordinal));
        Assert.Equal(13, advance.Address.ColumnNumber); // The shared final/advance column, not its missing intermediate slot.
        Assert.Contains(WorkspaceTimedDrawTestFixture.TimeText(fixture.Placement(final)), advance.GetString());
    }

    [Theory]
    [InlineData(CompetitionMode.SinglesKnockout, 8, 1, KnockoutGoal.Champion, PlacementPlayoff.ThirdToEighth, "ko-playoffs")]
    [InlineData(CompetitionMode.TeamKnockout, 9, 1, KnockoutGoal.Champion, PlacementPlayoff.ThirdPlace, "team-play-in")]
    [InlineData(CompetitionMode.SinglesKnockout, 7, 2, KnockoutGoal.OneQualifierPerGroup, PlacementPlayoff.None, "unequal-groups")]
    [InlineData(CompetitionMode.SinglesKnockout, 9, 4, KnockoutGoal.Champion, PlacementPlayoff.ThirdPlace, "grouped-champion")]
    [InlineData(CompetitionMode.SinglesKnockout, 3, 2, KnockoutGoal.OneQualifierPerGroup, PlacementPlayoff.None, "direct-qualifier")]
    [InlineData(CompetitionMode.SinglesRoundRobin, 5, 1, KnockoutGoal.OneQualifierPerGroup, PlacementPlayoff.None, "odd-rr")]
    [InlineData(CompetitionMode.SinglesRoundRobin, 7, 2, KnockoutGoal.OneQualifierPerGroup, PlacementPlayoff.None, "groups-rr")]
    [InlineData(CompetitionMode.TeamRoundRobin, 4, 1, KnockoutGoal.OneQualifierPerGroup, PlacementPlayoff.None, "team-rr")]
    public void EveryPlayableNodeHasExactTimingCoverageAndPreservedPrintLayout(CompetitionMode mode, int count, int groups,
        KnockoutGoal goal, PlacementPlayoff playoff, string name)
    {
        using var fixture = new WorkspaceTimedDrawTestFixture(mode, count, groups, goal, playoff);
        var before = JsonSerializer.Serialize(fixture.Workspace);
        var path = fixture.Export(); using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("对阵表");
        var views = fixture.Draw.Settings.IsKnockout ? 1 : 2;
        foreach (var node in fixture.Project.MatchGraph!.Matches)
        {
            Assert.NotEqual(node.OriginalMatchId, node.DisplayName);
            var placement = fixture.Placement(node);
            var expected = WorkspaceTimedDrawTestFixture.TimeText(placement) + "\n" + placement.Court;
            Assert.Equal(views, sheet.CellsUsed().Count(c => c.GetString().EndsWith(expected, StringComparison.Ordinal)));
        }
        Assert.Equal(fixture.Project.MatchGraph.Matches.Count * views,
            sheet.CellsUsed().Count(c => c.GetString().Contains("2026-09-14 ", StringComparison.Ordinal)));
        Assert.Equal(XLPageOrientation.Landscape, sheet.PageSetup.PageOrientation);
        Assert.Equal(1, sheet.PageSetup.PagesWide); Assert.NotEmpty(sheet.PageSetup.PrintAreas);
        Assert.Contains(fixture.Project.Id.ToString(), string.Join("\n", workbook.Worksheet("抽签设置与审计信息").CellsUsed().Select(c => c.GetString())));
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Workspace));
        RetainFixture(name, fixture, path, sheet);
    }

    [Fact]
    public void PlacementPlayoffsAndPlayInUseTheirActualGeometry()
    {
        using var fixture = new WorkspaceTimedDrawTestFixture(CompetitionMode.SinglesKnockout, 9, 1,
            KnockoutGoal.Champion, PlacementPlayoff.ThirdToEighth);
        using var workbook = new XLWorkbook(fixture.Export()); var sheet = workbook.Worksheet("对阵表");
        var placementStart = 6 + 8 * 4 + 2;
        var cells = new Dictionary<string, (int Row, int Column)>
        {
            [PlacementPlayoffLabels.ThirdPlaceMatchName] = (placementStart + 4, 9),
            [PlacementPlayoffLabels.FifthToEighthSemiMatchName(1)] = (placementStart + 11, 9),
            [PlacementPlayoffLabels.FifthToEighthSemiMatchName(2)] = (placementStart + 17, 9),
            [PlacementPlayoffLabels.FifthPlaceMatchName] = (placementStart + 13, 17),
            [PlacementPlayoffLabels.SeventhPlaceMatchName] = (placementStart + 15, 13)
        };
        foreach (var (name, address) in cells)
        {
            var node = Assert.Single(fixture.Project.MatchGraph!.Matches, n => n.DisplayName == name);
            Assert.Contains(WorkspaceTimedDrawTestFixture.TimeText(fixture.Placement(node)), sheet.Cell(address.Row, address.Column).GetString());
        }
        var playIn = Assert.Single(fixture.Project.MatchGraph!.Matches, n => n.DisplayName.Contains("首轮赛"));
        var playInCell = Assert.Single(sheet.Column(3).CellsUsed(), c => c.GetString().StartsWith("胜者入正赛", StringComparison.Ordinal));
        Assert.Contains(WorkspaceTimedDrawTestFixture.TimeText(fixture.Placement(playIn)), playInCell.GetString());
        Assert.All(fixture.Project.Roster!.Participants, p => Assert.Contains(sheet.CellsUsed(), c => c.GetString() == p.DisplayName));
    }

    [Fact]
    public void TwoVisibleIdenticalProjectsStillUseOnlyTheSelectedProjectGuidsAndPreciseTimes()
    {
        using var fixture = new WorkspaceTimedDrawTestFixture(CompetitionMode.SinglesKnockout, 4);
        var first = fixture.Project; var id = Guid.NewGuid();
        var second = first with { Id = id, Discipline = EventDiscipline.WomenSingles, SortOrder = 1,
            MatchGraph = MatchGraphFactory.Create(id, fixture.Draw, 35) };
        var resources = fixture.Workspace.Resources!;
        var schedule = Assert.IsType<TournamentSchedulingResult.Success>(new TournamentScheduler().Generate(new(
            [first.MatchGraph!, second.MatchGraph], resources, fixture.Workspace.Schedule!.Policy) { ScheduleRevision = 3 })).Schedule;
        var shifted = schedule.Placements.ToDictionary(p => p.Key, p => p.Value with
        {
            StartTime = p.Value.StartTime.Add(TimeSpan.FromTicks(301234567)),
            EndTime = p.Value.EndTime.Add(TimeSpan.FromTicks(301234567))
        });
        var workspace = fixture.Workspace with { Projects = [first, second], Schedule = schedule with { Placements = shifted } };
        var original = JsonSerializer.Serialize(workspace);
        using var workbook = new XLWorkbook(fixture.Export(workspace, second)); var sheet = workbook.Worksheet("对阵表");
        foreach (var node in second.MatchGraph.Matches)
        {
            var p = shifted[node.Id]; var time = $"{p.DayLabel} {p.StartTime:HH:mm:ss.fffffff}-{p.EndTime:HH:mm:ss.fffffff}";
            Assert.Single(sheet.CellsUsed(), c => c.GetString().Contains(time, StringComparison.Ordinal));
            var other = first.MatchGraph!.Matches.Single(n => n.DisplayName == node.DisplayName);
            var foreign = shifted[other.Id];
            Assert.DoesNotContain(sheet.CellsUsed(), c => c.GetString().Contains($"{foreign.DayLabel} {foreign.StartTime:HH:mm:ss.fffffff}-", StringComparison.Ordinal));
        }
        Assert.Equal(original, JsonSerializer.Serialize(workspace));
    }

    [Fact]
    public void LiteralNamesAreNotParsedAndDrawOnlyCallersRetainTheirCells()
    {
        var roster = new[] { new DrawParticipant(" =1+1 / 张  三【甲】胜者", PrimaryStudentId: "one"),
            new DrawParticipant("第1组首轮赛1胜者", PrimaryStudentId: "two"),
            new DrawParticipant("@SUM(A1) / 李四", PrimaryStudentId: "three") };
        using var fixture = new WorkspaceTimedDrawTestFixture(CompetitionMode.SinglesKnockout, 3, source: roster);
        using var timed = new XLWorkbook(fixture.Export()); var sheet = timed.Worksheet("对阵表");
        foreach (var p in roster) Assert.Contains(sheet.CellsUsed(), c => c.GetString() == p.DisplayName && !c.HasFormula);
        var legacyPath = fixture.PathFor("draw-only");
        new DrawResultExcelWriter().Write(legacyPath, fixture.Draw, roster, context: fixture.ExportContext);
        using var legacy = new XLWorkbook(legacyPath); var original = legacy.Worksheet("对阵表");
        Assert.Equal(original.MergedRanges.Select(r => r.RangeAddress.ToString()), sheet.MergedRanges.Select(r => r.RangeAddress.ToString()));
        foreach (var cell in original.CellsUsed())
            Assert.StartsWith(cell.GetString(), sheet.Cell(cell.Address).GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(original.CellsUsed(), c => c.GetString().Contains("2026-09-14 ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("workspace-id")]
    [InlineData("workspace-name")]
    [InlineData("revision")]
    [InlineData("project-id")]
    [InlineData("project-name")]
    [InlineData("confirmed")]
    [InlineData("file-name")]
    [InlineData("file-hash")]
    [InlineData("seed")]
    [InlineData("participant-hash")]
    [InlineData("audit-id")]
    [InlineData("exported-at")]
    public void StaleOrForeignProvenanceFailsBeforeCreatingAnyDirectory(string field)
    {
        using var fixture = new WorkspaceTimedDrawTestFixture(CompetitionMode.SinglesKnockout, 4);
        var source = fixture.ExportContext;
        source = field switch
        {
            "workspace-id" => source with { WorkspaceId = Guid.NewGuid() },
            "workspace-name" => source with { WorkspaceName = source.WorkspaceName + " " },
            "revision" => source with { SourceRevision = source.SourceRevision + 1 },
            "project-id" => source with { ProjectId = Guid.NewGuid() },
            "project-name" => source with { ProjectName = source.ProjectName + " " },
            "confirmed" => source with { ConfirmedAt = source.ConfirmedAt!.Value.AddTicks(1) },
            "file-name" => source with { SourceFileName = "changed.xlsx" },
            "file-hash" => source with { SourceFileHash = "changed" },
            "seed" => source with { RandomSeed = "changed" },
            "participant-hash" => source with { ParticipantHash = "changed" },
            "audit-id" => source with { ExportAuditId = Guid.Empty },
            "exported-at" => source with { ExportedAt = default },
            _ => throw new ArgumentException(field)
        };
        var path = Path.Combine(fixture.DirectoryPath, "must-not-exist", "out.xlsx");
        var failure = Assert.Throws<WorkspaceValidationException>(() => new DrawResultExcelWriter().WriteTimed(path,
            new(fixture.Workspace), fixture.Project.Id, source));
        Assert.Equal("export.context", failure.Code); Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Theory]
    [InlineData("missing-label")]
    [InlineData("duplicate-label")]
    [InlineData("wrong-source")]
    [InlineData("missing-node")]
    [InlineData("extra-node")]
    [InlineData("rr-wrong-pair")]
    public void IncoherentDrawLayoutFailsBeforeReplacingExistingOutput(string mutation)
    {
        using var fixture = new WorkspaceTimedDrawTestFixture(mutation.StartsWith("rr-")
            ? CompetitionMode.SinglesRoundRobin : CompetitionMode.SinglesKnockout, 4);
        var nodes = fixture.Project.MatchGraph!.Matches.ToList();
        if (mutation == "missing-label") nodes[0] = nodes[0] with { DisplayName = "absent layout" };
        if (mutation == "duplicate-label") nodes[0] = nodes[0] with { DisplayName = nodes[1].DisplayName };
        if (mutation is "wrong-source" or "rr-wrong-pair") nodes[0] = nodes[0] with { SideA = nodes[0].SideB, SideB = nodes[0].SideA };
        if (mutation == "missing-node") nodes.RemoveAt(nodes.Count - 1);
        if (mutation == "extra-node") nodes.Add(nodes[0] with { Id = Guid.NewGuid(), OriginalMatchId = "extra", Order = 99, DisplayName = "extra layout" });
        var project = fixture.Project with { MatchGraph = fixture.Project.MatchGraph with { Matches = nodes } };
        var placements = fixture.Workspace.Schedule!.Placements.Where(p => nodes.Any(n => n.Id == p.Key)).ToDictionary(p => p.Key, p => p.Value);
        if (mutation == "extra-node") placements[nodes[^1].Id] = placements[nodes[0].Id] with { MatchId = nodes[^1].Id };
        var workspace = fixture.Workspace with { Projects = [project], Schedule = fixture.Workspace.Schedule with { Placements = placements } };
        var context = new WorkspaceScheduleExportContext(workspace); // These fixtures pass Core's structural boundary.
        var output = fixture.PathFor("timed"); File.WriteAllText(output, "existing user material");
        var error = Assert.Throws<WorkspaceValidationException>(() => new DrawResultExcelWriter().WriteTimed(output, context,
            project.Id, WorkspaceTimedDrawTestFixture.Source(workspace, project)));
        Assert.Equal("export.layout", error.Code); Assert.Equal("existing user material", File.ReadAllText(output));
    }

    private static void RetainFixture(string name, WorkspaceTimedDrawTestFixture fixture, string path, IXLWorksheet sheet)
    {
        var root = Environment.GetEnvironmentVariable("SZBD_L3_FIXTURE_DIR");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root); var destination = Path.Combine(root, name + ".xlsx"); File.Copy(path, destination, true);
        var manifest = new
        {
            Source = fixture.ExportContext, GraphRevision = fixture.Project.MatchGraph!.Revision,
            Artifact = Path.GetFileName(destination), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))).ToLowerInvariant(),
            Bindings = fixture.Project.MatchGraph.Matches.Select(n => new
            {
                Key = new WorkspaceMatchKey(n.ProjectId, n.Id), n.OriginalMatchId, n.DisplayName, n.SideA, n.SideB,
                Placement = fixture.Placement(n), Cells = sheet.CellsUsed().Where(c => c.GetString().Contains(
                    WorkspaceTimedDrawTestFixture.TimeText(fixture.Placement(n)), StringComparison.Ordinal))
                    .Select(c => new { Address = c.Address.ToStringRelative(), Text = c.GetString() }).ToArray()
            }).ToArray()
        };
        File.WriteAllText(Path.Combine(root, name + ".manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(root, name + ".workspace.json"), JsonSerializer.Serialize(fixture.Workspace, new JsonSerializerOptions { WriteIndented = true }));
    }
}
