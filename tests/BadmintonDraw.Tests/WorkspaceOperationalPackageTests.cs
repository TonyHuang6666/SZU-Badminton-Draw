using System.Text.Json;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceOperationalPackageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Three_same_named_projects_keep_qualified_identity_and_full_draws_for_narrow_day_scope(bool oneProject)
    {
        using var f = new WorkspaceOperationalPackageFixture(projects: 3, entrants: 4);
        var selected = f.Workspace.Projects[1];
        var before = f.Workspace;
        var result = f.Workflow.ExportOperationalPackage(new(f.Output, oneProject ? selected.Id : null,
            [WorkspaceOperationalPackageFixture.FirstDay]), f.Workspace.Revision);
        Assert.Equal(oneProject ? 9 : 13, result.Outputs.Count);
        Assert.Equal(oneProject ? 3 : 9, result.Counts.DistinctMatchCount);
        Assert.Equal(oneProject ? 3 : 9, result.Counts.RecordRowCount);
        Assert.Equal(oneProject ? 2 : 6, result.Outputs.Count(o => o.Kind is OperationalMaterialKind.TimedDrawExcel or OperationalMaterialKind.TimedDrawA4Pdf));
        Assert.DoesNotContain(result.Outputs, o => o.Kind == OperationalMaterialKind.ProjectRecordExcel);
        var record = Assert.Single(result.Outputs, o => Path.GetFileName(o.Path).EndsWith("赛程记录表.xlsx", StringComparison.Ordinal));
        Assert.Equal(OperationalMaterialKind.MergedRecordExcel, record.Kind);
        Assert.Equal("9月20日合并赛程记录表.xlsx", Path.GetFileName(record.Path));
        var rows = ReadRecord(record);
        Assert.Equal(oneProject ? 3 : 9, rows.Count);
        Assert.Equal(oneProject ? 1 : 3, rows.Select(r => r.ProjectId.Text).Distinct().Count());
        foreach (var row in rows)
        {
            var project = f.Workspace.Projects.Single(p => p.Id.ToString() == row.ProjectId.Text);
            Assert.Contains(project.MatchGraph!.Matches, n => n.Id.ToString() == row.MatchId.Text);
            Assert.Equal(project.MatchGraph.Revision, row.GraphRevision.Text);
            Assert.Equal(project.Draw!.ConfirmedAt!.Value, DateTimeOffset.Parse(row.DrawConfirmedAt.Text));
            Assert.Equal("2026-09-20", row.RecordDay.Text);
        }
        using var quality = new XLWorkbook(Assert.Single(result.Outputs, o => o.Kind == OperationalMaterialKind.QualityExcel).Path);
        var provenance = string.Join("\n", quality.Worksheet("来源项目").CellsUsed().Select(c => c.GetString()));
        Assert.All(f.Workspace.Projects, p => Assert.Contains(p.Id.ToString(), provenance));
        var placements = f.Workspace.Schedule!.Placements;
        Assert.Equal(new[] { 20, 25, 30 }, f.Workspace.Projects.Select(p =>
            (int)(placements[p.MatchGraph!.Matches[0].Id].EndTime - placements[p.MatchGraph.Matches[0].Id].StartTime).TotalMinutes));
        f.Retain(oneProject ? "selected-md" : "three-projects", before, result);
    }

    [Fact]
    public void Merged_record_orders_simultaneous_projects_by_configured_court_order()
    {
        using var f = new WorkspaceOperationalPackageFixture(projects: 3, transform: source =>
        {
            var resources = source.Resources! with
            { Days = source.Resources.Days.Select(day => day with { Courts = ["B3", "B1", "B2"] }).ToArray() };
            var placements = source.Schedule!.Placements.ToDictionary();
            var courts = new[] { "B2", "B3", "B1" };
            foreach (var project in source.Projects)
            {
                var match = project.MatchGraph!.Matches.Single();
                placements[match.Id] = placements[match.Id] with
                { Court = courts[project.SortOrder], StartTime = new(9, 0), EndTime = new TimeOnly(9, 0).AddMinutes(match.ExpectedDurationMinutes) };
            }
            return source with { Resources = resources, Schedule = source.Schedule with { Resources = resources, Placements = placements } };
        });
        var result = f.Workflow.ExportOperationalPackage(new(f.Output, Days: [WorkspaceOperationalPackageFixture.FirstDay]), f.Workspace.Revision);
        var rows = ReadRecord(Assert.Single(result.Outputs, o => o.Kind == OperationalMaterialKind.MergedRecordExcel));
        Assert.Equal(new[] { f.Workspace.Projects[1].Id.ToString(), f.Workspace.Projects[2].Id.ToString(), f.Workspace.Projects[0].Id.ToString() },
            rows.Select(row => row.ProjectId.Text));
        using var workbook = new XLWorkbook(Assert.Single(result.Outputs, o => o.Kind == OperationalMaterialKind.MergedRecordExcel).Path);
        Assert.Equal(new[] { "B3", "B1", "B2" }, Enumerable.Range(6, 3).Select(row => workbook.Worksheet("对阵记录表").Cell(row, 11).GetString()));
    }

    [Fact]
    public void New_merged_package_preserves_old_project_record_files_without_listing_them_as_outputs()
    {
        using var f = new WorkspaceOperationalPackageFixture();
        var package = Directory.CreateDirectory(Path.Combine(f.Output, "9月20日-9月22日多项目合并材料包")).FullName;
        var oldRecord = Path.Combine(package, "9月20日同名项目赛程记录表.xlsx");
        File.Copy(f.Record("old-record", [new(f.FirstKey, WorkspaceOperationalPackageFixture.FirstDay)], fill: true), oldRecord);
        var oldHash = WorkspaceOperationalPackageFixture.Hash(oldRecord);

        var result = f.Workflow.ExportOperationalPackage(new(f.Output), f.Workspace.Revision);

        Assert.Equal(oldHash, WorkspaceOperationalPackageFixture.Hash(oldRecord));
        var preserved = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(File.ReadAllBytes(oldRecord));
        Assert.Equal("21-10", Assert.Single(preserved.Rows).Score.Text);
        Assert.DoesNotContain(result.Outputs, output => output.Path == oldRecord);
        Assert.Single(result.Outputs, output => output.Kind == OperationalMaterialKind.MergedRecordExcel);
        Assert.DoesNotContain(result.Outputs, output => output.Kind == OperationalMaterialKind.ProjectRecordExcel);
        using var manifest = ReadManifest(result);
        Assert.DoesNotContain(manifest.RootElement.GetProperty("Files").EnumerateArray(),
            output => output.GetProperty("FileName").GetString() == Path.GetFileName(oldRecord));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Real_team_knockout_and_round_robin_choose_team_scores_only(bool roundRobin)
    {
        using var f = new WorkspaceOperationalPackageFixture(entrants: 4, team: true, roundRobin: roundRobin);
        var before = f.Workspace;
        var result = f.Workflow.ExportOperationalPackage(new(f.Output), f.Workspace.Revision);
        Assert.Single(result.Outputs, o => o.Kind == OperationalMaterialKind.TeamScoreExcel);
        Assert.DoesNotContain(result.Outputs, o => o.Kind == OperationalMaterialKind.IndividualScorePdf);
        Assert.Equal(roundRobin ? 6 : 3, result.Counts.RecordRowCount);
        using var score = new XLWorkbook(Assert.Single(result.Outputs, o => o.Kind == OperationalMaterialKind.TeamScoreExcel).Path);
        Assert.Contains("队伍", string.Join("\n", score.Worksheet("团体记分表").CellsUsed().Select(c => c.GetString())));
        f.Retain(roundRobin ? "team-round-robin" : "team-knockout", before, result);
    }

    [Fact]
    public void Only_explicit_carry_target_gets_deduplicated_active_earlier_pending_receipt_rows()
    {
        using var f = new WorkspaceOperationalPackageFixture();
        var day = WorkspaceOperationalPackageFixture.FirstDay;
        var pending = f.Record("pending", [new(f.FirstKey, day)]); f.Import(pending);
        var second = f.Record("pending-again", [new(f.FirstKey, day)]);
        using (var book = new XLWorkbook(second)) { book.Properties.Title = "second genuine receipt"; book.Save(); }
        f.Import(second);
        Assert.Equal(2, f.Workspace.ImportLogs.Count);
        var before = f.Workspace;
        var result = f.Workflow.ExportOperationalPackage(new(f.Output, PendingCarryoverDay: day.AddDays(1)), before.Revision);
        Assert.Equal(new OperationalPackageCounts(1, 2, 1, 1, 13), result.Counts);
        var record = Assert.Single(result.Outputs, o => o.Kind == OperationalMaterialKind.MergedRecordExcel && o.RecordDay == day.AddDays(1));
        Assert.Equal("2026-09-21", Assert.Single(ReadRecord(record)).RecordDay.Text);
        Assert.DoesNotContain(result.Outputs, o => o.RecordDay == day.AddDays(2));
        using var daily = new XLWorkbook(Assert.Single(result.Outputs, o => o.Kind == OperationalMaterialKind.DailyScheduleExcel && o.RecordDay == day.AddDays(1)).Path);
        Assert.Equal("待安排", daily.Worksheet("赛程明细").Cell(5, 4).GetString());
        Assert.Equal("待安排", daily.Worksheet("赛程明细").Cell(5, 5).GetString());
        Assert.Contains("2026-09-20", daily.Worksheet("赛程明细").Cell(5, 12).GetString());
        Assert.Contains("无本日已安排", daily.Worksheet("时间场地网格").Cell(3, 1).GetString());
        Assert.Equal(WorkspaceOperationalPackageFixture.Business(before), WorkspaceOperationalPackageFixture.Business(result.Command.Workspace));
        using var manifest = ReadManifest(result);
        var proofs = manifest.RootElement.GetProperty("CarryoverEvidence").EnumerateArray().ToArray();
        Assert.Equal(2, proofs.Length);
        Assert.All(proofs, p => Assert.Contains(before.ImportLogs, log => log.Id == p.GetProperty("ImportLogId").GetGuid() &&
            log.ContentHash == p.GetProperty("ContentHash").GetString()));
        Assert.Equal(2, manifest.RootElement.GetProperty("Rows").GetArrayLength());
        f.Retain("pending-carryover", before, result);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("same-day")]
    [InlineData("future")]
    [InlineData("voided")]
    [InlineData("completed")]
    public void Old_placements_alone_or_ineligible_receipts_do_not_create_carryovers(string evidence)
    {
        using var f = new WorkspaceOperationalPackageFixture();
        var day = WorkspaceOperationalPackageFixture.FirstDay;
        if (evidence != "none")
            f.Import(f.Record(evidence, [new(f.FirstKey, day.AddDays(evidence == "same-day" ? 1 : evidence == "future" ? 2 : 0))], evidence == "completed"));
        if (evidence == "voided")
            f.Workflow.GenerateSchedule(f.Workspace.Resources!, f.Workspace.Schedule!.Policy, f.Workspace.Revision);
        var before = f.Workspace;
        var result = f.Workflow.ExportOperationalPackage(new(f.Output, PendingCarryoverDay: day.AddDays(1)), f.Workspace.Revision);
        Assert.Equal(0, result.Counts.PendingCarryoverCount);
        Assert.Equal(1, result.Counts.RecordRowCount);
        Assert.DoesNotContain(result.Outputs, o => o.RecordDay == day.AddDays(1));
        if (evidence == "completed") Assert.Equal(TournamentStage.Completed, result.Command.Workspace.Stage);
        if (evidence == "completed") f.Retain("completed", before, result);
    }

    [Fact]
    public void Empty_scoped_project_day_adds_no_record_rows_but_retains_its_whole_timed_draw()
    {
        using var f = new WorkspaceOperationalPackageFixture(projects: 2, transform: source =>
        {
            var second = source.Projects[1].MatchGraph!.Matches.Single().Id;
            var placements = source.Schedule!.Placements.ToDictionary();
            placements[second] = placements[second] with { DayLabel = "2026-09-21" };
            return source with { Schedule = source.Schedule with { Placements = placements } };
        });
        var result = f.Workflow.ExportOperationalPackage(new(f.Output, Days: [WorkspaceOperationalPackageFixture.FirstDay]), f.Workspace.Revision);
        Assert.Equal(11, result.Outputs.Count);
        Assert.Equal(2, result.Counts.TimedDrawMatchCount);
        Assert.DoesNotContain(result.Outputs, o => o.Kind == OperationalMaterialKind.ProjectRecordExcel);
        var row = Assert.Single(ReadRecord(Assert.Single(result.Outputs, o => o.Kind == OperationalMaterialKind.MergedRecordExcel)));
        Assert.Equal(f.Workspace.Projects[0].Id.ToString(), row.ProjectId.Text);
    }

    [Theory]
    [InlineData("empty", "export.days")]
    [InlineData("duplicate", "export.days")]
    [InlineData("unknown", "export.days")]
    [InlineData("carry", "export.carryover-day")]
    [InlineData("layout", "export.layout")]
    [InlineData("project", "project.not-found")]
    [InlineData("no-matches", "export.no-matches")]
    [InlineData("stale", "RevisionConflict")]
    public void Invalid_selection_rejects_before_any_material_directory(string scenario, string code)
    {
        using var f = new WorkspaceOperationalPackageFixture();
        var day = WorkspaceOperationalPackageFixture.FirstDay;
        var request = new OperationalExportRequest(f.Output);
        request = scenario switch
        {
            "empty" => request with { Days = [] }, "duplicate" => request with { Days = [day, day] },
            "unknown" => request with { Days = [day.AddYears(1)] },
            "carry" => request with { Days = [day], PendingCarryoverDay = day.AddDays(1) },
            "layout" => request with { DrawLayout = new(0, 1) },
            "project" => request with { ProjectId = Guid.NewGuid() },
            "no-matches" => request with { Days = [day.AddDays(1)] }, _ => request
        };
        var hash = WorkspaceOperationalPackageFixture.Hash(f.Archive);
        var error = Assert.Throws<OperationalPackageExportException>(() => f.Workflow.ExportOperationalPackage(request,
            f.Workspace.Revision - (scenario == "stale" ? 1 : 0)));
        Assert.Equal(code, error.Error.Code); Assert.False(error.AuditRecorded);
        Assert.Empty(error.Outputs); Assert.False(Directory.Exists(f.Output));
        Assert.Equal(hash, WorkspaceOperationalPackageFixture.Hash(f.Archive));
        if (scenario == "stale") Assert.Null(error.SourceRevision);
    }

    [Fact]
    public void Conflict_in_unselected_project_blocks_entire_package_with_actual_violation_ownership()
    {
        using var f = new WorkspaceOperationalPackageFixture(projects: 3, transform: source =>
        {
            var placements = source.Schedule!.Placements.ToDictionary();
            var one = source.Projects[1].MatchGraph!.Matches.Single().Id; var two = source.Projects[2].MatchGraph!.Matches.Single().Id;
            placements[two] = placements[two] with { Court = placements[one].Court, StartTime = placements[one].StartTime,
                EndTime = placements[one].StartTime.AddMinutes(30) };
            return source with { Schedule = source.Schedule with { Placements = placements } };
        });
        var error = Assert.Throws<OperationalPackageExportException>(() => f.Workflow.ExportOperationalPackage(
            new(f.Output, f.Workspace.Projects[0].Id), f.Workspace.Revision));
        Assert.Equal("export.hard-violations", error.Error.Code);
        Assert.Contains(error.Error.SchedulingFailure!.Violations, v => v.Code == SchedulingConstraintCode.CourtOverlap &&
            v.ProjectId != f.Workspace.Projects[0].Id);
        Assert.False(Directory.Exists(f.Output));
    }

    [Fact]
    public void Requests_and_returned_collections_snapshot_construction_and_init_inputs()
    {
        using var f = new WorkspaceOperationalPackageFixture();
        var dates = new List<DateOnly> { WorkspaceOperationalPackageFixture.FirstDay };
        var request = new OperationalExportRequest(f.Output, Days: dates); dates.Clear();
        var changed = request with { Days = dates }; dates.Add(WorkspaceOperationalPackageFixture.FirstDay.AddDays(2));
        Assert.Empty(changed.Days!);
        var result = f.Workflow.ExportOperationalPackage(request, f.Workspace.Revision);
        Assert.Single(result.Scope.Days); Assert.Equal(9, result.Outputs.Count);
        var outputs = result.Outputs.ToList(); var copy = result with { Outputs = outputs }; outputs.Clear();
        Assert.Equal(9, copy.Outputs.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<OperationalPackageOutput>)result.Outputs).Clear());
    }

    internal static IReadOnlyList<WorkspaceRecordRawRow> ReadRecord(OperationalPackageOutput output) =>
        new WorkspaceMatchRecordReader().ReadWorkspaceRecord(File.ReadAllBytes(output.Path)).Rows;
    internal static JsonDocument ReadManifest(OperationalPackageOutcome result) =>
        JsonDocument.Parse(File.ReadAllText(Assert.Single(result.Outputs, o => o.Kind == OperationalMaterialKind.Manifest).Path));

    [Fact]
    public void Narrow_day_still_gets_full_timed_graph_and_unselected_receipt_cannot_add_rows()
    {
        using var f = new WorkspaceOperationalPackageFixture(projects: 2, entrants: 4, transform: source =>
        {
            var placements = source.Schedule!.Placements.ToDictionary();
            foreach (var project in source.Projects)
            {
                var final = project.MatchGraph!.Matches.Single(n => n.SideA is EntrantSource.WinnerOf);
                placements[final.Id] = placements[final.Id] with { DayLabel = "2026-09-22" };
            }
            return source with { Schedule = source.Schedule with { Placements = placements } };
        });
        var selected = f.Workspace.Projects[0]; var other = f.Workspace.Projects[1];
        var otherKey = new WorkspaceMatchKey(other.Id, other.MatchGraph!.Matches.First(n => n.SideA is EntrantSource.Participant).Id);
        f.Import(f.Record("unselected-pending", [new(otherKey, WorkspaceOperationalPackageFixture.FirstDay)]));
        var result = f.Workflow.ExportOperationalPackage(new(f.Output, selected.Id,
            [WorkspaceOperationalPackageFixture.FirstDay, WorkspaceOperationalPackageFixture.FirstDay.AddDays(1)],
            WorkspaceOperationalPackageFixture.FirstDay.AddDays(1)), f.Workspace.Revision);
        Assert.Equal(new OperationalPackageCounts(2, 2, 0, 3, 9), result.Counts);
        Assert.DoesNotContain(result.Outputs, o => o.RecordDay == WorkspaceOperationalPackageFixture.FirstDay.AddDays(1));
        using var timed = new XLWorkbook(Assert.Single(result.Outputs, o => o.Kind == OperationalMaterialKind.TimedDrawExcel).Path);
        var content = string.Join("\n", timed.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString()));
        Assert.Contains("2026-09-22", content);
        Assert.All(selected.MatchGraph!.Matches, n =>
        {
            var placement = f.Workspace.Schedule!.Placements[n.Id];
            Assert.Contains($"{placement.DayLabel} {placement.StartTime:HH:mm}-{placement.EndTime:HH:mm}\n{placement.Court}", content);
        });
        using var manifest = ReadManifest(result);
        Assert.Equal(0, manifest.RootElement.GetProperty("CarryoverEvidence").GetArrayLength());
    }

    [Fact]
    public void Real_single_project_package_publishes_all_nine_artifacts_then_saves_one_audit_only()
    {
        using var fixture = new WorkspaceOperationalPackageFixture();
        var before = fixture.Workspace;
        var result = fixture.Workflow.ExportOperationalPackage(new(fixture.Output), before.Revision);
        Assert.True(result.AuditRecorded);
        Assert.Equal(before.Revision, result.SourceRevision);
        Assert.Equal(before.Revision + 1, result.Command.Workspace.Revision);
        Assert.Equal(WorkspaceOperationalPackageFixture.Business(before), WorkspaceOperationalPackageFixture.Business(result.Command.Workspace));
        Assert.Equal(new OperationalPackageCounts(1, 1, 0, 1, 9), result.Counts);
        Assert.Equal(9, result.Outputs.Count);
        Assert.Equal(9, result.Outputs.Select(o => o.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(result.Outputs, output =>
        {
            Assert.True(output.ByteLength > 0);
            Assert.Equal(new FileInfo(output.Path).Length, output.ByteLength);
            Assert.Equal(WorkspaceOperationalPackageFixture.Hash(output.Path), output.Sha256);
            if (output.Path.EndsWith(".xlsx")) { using var book = new XLWorkbook(output.Path); Assert.NotEmpty(book.Worksheets); }
            if (output.Path.EndsWith(".pdf")) Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(output.Path).Take(8).ToArray()));
        });
        var record = Assert.Single(result.Outputs, o => o.Kind == OperationalMaterialKind.MergedRecordExcel);
        var parsed = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(File.ReadAllBytes(record.Path));
        Assert.Single(parsed.Rows);
        var durable = new TournamentWorkspaceStore().Read(fixture.Archive);
        Assert.Single(durable.AuditEvents, a => a.Id == result.AuditId && a.Action == "OperationalPackageExported");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Assert.Single(result.Outputs, o => o.Kind == OperationalMaterialKind.Manifest).Path));
        Assert.Equal("Planned", manifest.RootElement.GetProperty("Audit").GetProperty("State").GetString());
        Assert.Equal(8, manifest.RootElement.GetProperty("Files").GetArrayLength());
        var package = Assert.Single(Directory.GetDirectories(fixture.Output));
        Assert.Equal(Path.Combine(fixture.Output, "9月20日-9月22日多项目合并材料包"), package);
        Assert.Empty(Directory.GetDirectories(package));
        Assert.All(result.Outputs, output => Assert.Equal(package, Path.GetDirectoryName(output.Path)));
        fixture.Retain("single-project", before, result);
    }
}
