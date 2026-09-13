using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceScheduleQualityWriterTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("quality-writer-").FullName;
    private static readonly string[] Sheets = ["检查总览", "当前赛程卡片", "检查明细", "选手每日负荷", "资源与日负荷", "来源项目"];
    public void Dispose() => Directory.Delete(directory, true);
    private (string Path, XLWorkbook Book) Write(TournamentWorkspace workspace)
    {
        var path = System.IO.Path.Combine(directory, Guid.NewGuid() + ".xlsx");
        new WorkspaceScheduleQualityExcelWriter().Write(path, new(workspace), WorkspaceScheduleQualityFixture.GeneratedAt);
        return (path, new XLWorkbook(path));
    }
    private static IXLCell Value(IXLWorksheet sheet, string label) => sheet.CellsUsed().Single(c => c.GetString() == label).CellRight();
    private static string Text(IXLWorksheet sheet) => string.Join("\n", sheet.CellsUsed().Select(c => c.GetString()));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public void ActualGlobalSnapshotAndCompletedLocksProduceFaithfulQualityAndProvenance(int completed)
    {
        var workspace = WorkspaceScheduleQualityFixture.MultiProject(completed); var before = WorkspaceScheduleQualityFixture.Snapshot(workspace);
        var request = WorkspaceScheduleQualityFixture.Request(workspace);
        var expected = new TournamentScheduleQualityAnalyzer().Analyze(request, workspace.Schedule!.Placements);
        Assert.True(new TournamentPlacementValidator(request).ValidateSchedule(workspace.Schedule.Placements).IsValid);
        Assert.Equal(0, expected.HardConstraintCount);
        var (path, book) = Write(workspace); using (book)
        {
            Assert.Equal(Sheets, book.Worksheets.Select(s => s.Name)); var summary = book.Worksheet(Sheets[0]);
            Assert.Equal(6, Value(summary, "全局场次数").GetValue<int>());
            Assert.Equal(completed, Value(summary, "已记录赛果").GetValue<int>());
            Assert.Equal(6 - completed, Value(summary, "待记录场次").GetValue<int>());
            Assert.Equal(expected.SoftScore, Value(summary, "当前基线软约束评分").GetValue<long>());
            Assert.Contains("历史移动次数不计算", Text(summary)); Assert.DoesNotContain("历史移动次数：0", Text(summary));
            Assert.Equal(WorkspaceScheduleQualityFixture.GeneratedAt.ToString("O", CultureInfo.InvariantCulture), Value(summary, "生成时间").GetString());
            Assert.Contains(workspace.Name, Text(summary)); Assert.Contains("17", Value(summary, "工作区修订").GetString());
            Assert.Equal("9", Value(summary, "赛程修订").GetString());
            var cards = book.Worksheet(Sheets[1]);
            Assert.Equal(6, cards.RowsUsed().Count(r => r.RowNumber() > 4));
            foreach (var project in workspace.Projects)
            foreach (var node in project.MatchGraph!.Matches)
            {
                var row = cards.RowsUsed().Single(r => r.Cell(3).GetString() == node.Id.ToString());
                Assert.Equal(project.Id.ToString(), row.Cell(1).GetString()); Assert.Equal(project.DisplayName, row.Cell(2).GetString());
                var placement = workspace.Schedule.Placements[node.Id];
                Assert.Equal(placement.StartTime.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture), row.Cell(6).GetString());
                Assert.Equal(placement.EndTime.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture), row.Cell(7).GetString());
                Assert.Equal(workspace.Results.TryGetValue(new(project.Id, node.Id), out var result) ? "已记录" : "待记录", row.Cell(11).GetString());
                Assert.Equal(result is null ? "" : result.ActualPlayedDay?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "未知", row.Cell(12).GetString());
                if (completed == 6 && node.IsChampionshipFinal)
                {
                    Assert.Equal(workspace.Results[new(project.Id, node.Dependencies[0])].Winner.DisplayName, row.Cell(9).GetString());
                    Assert.Equal(workspace.Results[new(project.Id, node.Dependencies[1])].Winner.DisplayName, row.Cell(10).GetString());
                }
            }
            var source = Text(book.Worksheet(Sheets[5]));
            foreach (var project in workspace.Projects)
            {
                Assert.Contains(project.Id.ToString(), source); Assert.Contains(project.MatchGraph!.Revision, source);
                Assert.Contains(project.Draw!.ConfirmedAt!.Value.ToString("O", CultureInfo.InvariantCulture), source);
                Assert.Contains(project.Roster!.ContentHash, source); Assert.Contains(project.Roster.SourceFileName, source);
            }
            Assert.DoesNotContain("LockedPlacement", Text(book.Worksheet(Sheets[2])));
            Assert.Equal(before, WorkspaceScheduleQualityFixture.Snapshot(workspace));
            if (completed == 6) Retain(path, "valid-completed", workspace, expected);
            if (completed == 0) Retain(path, "valid-multiproject", workspace, expected);
        }
    }

    [Fact]
    public void CrossProjectViolationsUseIndependentRelatedOwnerAndProhibitOperationalUse()
    {
        var workspace = WorkspaceScheduleQualityFixture.MultiProject(conflict: true);
        var expected = new TournamentScheduleQualityAnalyzer().Analyze(WorkspaceScheduleQualityFixture.Request(workspace), workspace.Schedule!.Placements);
        Assert.Contains(expected.Violations, v => v.Code == SchedulingConstraintCode.CourtOverlap && v.RelatedMatchId is not null);
        var (path, book) = Write(workspace); using (book)
        {
            Assert.Equal(expected.Violations.Count, Value(book.Worksheet(Sheets[0]), "硬约束违规项数").GetValue<int>());
            Assert.Contains("禁止作为现场执行赛程/运营材料放行依据", Text(book.Worksheet(Sheets[0])));
            var rows = book.Worksheet(Sheets[2]).RowsUsed().Where(r => r.RowNumber() > 4).ToArray();
            Assert.Equal(expected.Violations.Count, rows.Length);
            foreach (var violation in expected.Violations)
            {
                var row = Assert.Single(rows, r => r.Cell(1).GetString() == violation.Code.ToString() && r.Cell(4).GetString() == violation.MatchId.ToString() && r.Cell(7).GetString() == (violation.RelatedMatchId?.ToString() ?? "—"));
                Assert.Equal(violation.Message, row.Cell(2).GetString());
                if (violation.RelatedMatchId is { } related)
                {
                    var owner = workspace.Projects.Single(p => p.MatchGraph!.Matches.Any(n => n.Id == related));
                    Assert.Equal(owner.Id.ToString(), row.Cell(8).GetString()); Assert.NotEqual(row.Cell(3).GetString(), row.Cell(8).GetString());
                    var relatedCard = book.Worksheet(Sheets[1]).RowsUsed().Single(r => r.Cell(3).GetString() == related.ToString());
                    Assert.Contains(violation.Code.ToString(), relatedCard.Cell(13).GetString());
                }
            }
            Retain(path, "cross-project-invalid", workspace, expected);
        }
    }

    [Fact]
    public void RealNineteenVariableForecastKeepsUnknownProbabilityAndExactCounts()
    {
        var workspace = WorkspaceScheduleQualityFixture.UnknownForecast();
        var expected = new TournamentScheduleQualityAnalyzer().Analyze(WorkspaceScheduleQualityFixture.Request(workspace), workspace.Schedule!.Placements);
        var forecast = expected.PlayerLoads.Single(p => p.PlayerKey == "student:A");
        Assert.False(forecast.IsExact); Assert.Null(forecast.ExpectedCount); Assert.Null(forecast.ProbabilityAtOrAboveLimit); Assert.Empty(forecast.Distribution);
        Assert.Equal(19, forecast.ConfirmedCount); Assert.Equal(38, forecast.MaximumCount);
        var (path, book) = Write(workspace); using (book)
        {
            var row = book.Worksheet(Sheets[3]).RowsUsed().Single(r => r.Cell(1).GetString() == "student:A");
            Assert.Equal(19, row.Cell(4).GetValue<int>()); Assert.Equal(38, row.Cell(5).GetValue<int>());
            Assert.Equal("未知", row.Cell(6).GetString()); Assert.Equal("未知", row.Cell(7).GetString());
            Assert.Contains("未知", row.Cell(8).GetString()); Assert.Equal("未知", row.Cell(9).GetString());
            Assert.Contains("≥", Text(book.Worksheet(Sheets[3]))); Assert.Contains("0.5", Text(book.Worksheet(Sheets[0])));
            var continuation = book.Worksheet(Sheets[3]).RowsUsed().Where(r => r.Cell(2).GetString().StartsWith("关联场次（续）", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(continuation);
            foreach (var continued in continuation)
            {
                Assert.Contains("student:A", continued.Cell(2).GetString()); Assert.Contains("2026-09-19", continued.Cell(2).GetString());
                Assert.All(Enumerable.Range(3, 7), column => Assert.True(continued.Cell(column).IsEmpty()));
                Assert.True(continued.Height < 409);
            }
            var allKeys = string.Join("\n", new[] { row.Cell(10).GetString() }.Concat(continuation.Select(r => r.Cell(10).GetString())));
            foreach (var id in forecast.MatchIds) Assert.Contains($"{workspace.Projects.Single().Id} / {id}", allKeys);
            Retain(path, "forecast-unknown", workspace, expected);
        }
    }

    [Fact]
    public void ResourceCapacitiesAndProbabilityAtCapKeepActualGlobalMeaning()
    {
        var workspace = WorkspaceScheduleQualityFixture.MultiProject();
        var expected = new TournamentScheduleQualityAnalyzer().Analyze(WorkspaceScheduleQualityFixture.Request(workspace), workspace.Schedule!.Placements);
        Assert.Equal(0, expected.HardConstraintCount); var (_, book) = Write(workspace); using (book)
        {
            var resource = book.Worksheet(Sheets[4]);
            foreach (var day in expected.DayLoads)
            {
                var row = resource.RowsUsed().First(r => r.Cell(1).GetString() == day.DayLabel);
                Assert.Equal(day.AvailableMatchMinutes, row.Cell(5).GetValue<int>()); Assert.Equal(day.RequiredPlacedMinutes, row.Cell(6).GetValue<int>());
                if (day.AvailableMatchMinutes == 0) Assert.Equal("无可用容量", row.Cell(7).GetString());
                else Assert.Equal((double)day.RequiredPlacedMinutes / day.AvailableMatchMinutes, row.Cell(7).GetDouble(), 10);
            }
            Assert.Equal("未设置（仅受场地/分时段容量限制）", Value(resource, "默认裁判人数").GetString());
            Assert.Contains("12:30:00.0000000", Text(resource)); Assert.Contains("13:30:00.0000000", Text(resource));
            Assert.Contains("BeforeBoundaryMinutes", Text(resource));
            foreach (var project in workspace.Projects)
            {
                var timing = workspace.Schedule!.Policy.ProjectTimings[project.Id];
                var row = resource.RowsUsed().Single(r => r.Cell(1).GetString() == "项目时长设置" && r.Cell(2).GetString() == project.Id.ToString());
                Assert.Equal(timing.MatchMinutes, row.Cell(4).GetValue<int>());
                Assert.Equal(timing.KnockoutTimingBoundaryEntrants?.ToString(CultureInfo.InvariantCulture) ?? "未设置", row.Cell(5).GetString());
                Assert.Equal(timing.BeforeBoundaryMinutes?.ToString(CultureInfo.InvariantCulture) ?? "未设置", row.Cell(6).GetString());
            }
            Assert.Equal(3, resource.RowsUsed().Count(r => r.Cell(1).GetString().StartsWith("不可用场地 ", StringComparison.Ordinal)));
            Assert.Equal(2, resource.RowsUsed().Count(r => r.Cell(1).GetString().StartsWith("裁判窗口 ", StringComparison.Ordinal)));
            Assert.Equal(15, Value(resource, "全局最小休息分钟").GetValue<int>());
            Assert.Equal(1, Value(resource, "全局选手每日上限").GetValue<int>());
            var player = expected.PlayerLoads.First(p => p.DayLabel == "2026-09-19"); Assert.Equal(1, player.ProbabilityAtOrAboveLimit);
            var load = book.Worksheet(Sheets[3]).RowsUsed().Single(r => r.Cell(1).GetString() == player.PlayerKey && r.Cell(3).GetString() == player.DayLabel);
            Assert.Equal(1, load.Cell(7).GetDouble()); Assert.Equal(1, load.Cell(4).GetValue<int>());
        }
    }

    [Fact]
    public void InvalidSchedulingInputRetainsGlobalIssueAndMarksSkippedQualityUnavailable()
    {
        var workspace = WorkspaceScheduleQualityFixture.MultiProject(inputInvalid: true); var request = WorkspaceScheduleQualityFixture.Request(workspace);
        Assert.False(new TournamentPlacementValidator(request).ValidateInput().IsValid);
        var (path, book) = Write(workspace); using (book)
        {
            Assert.Equal("不可用（排程输入无效）", Value(book.Worksheet(Sheets[0]), "当前基线软约束评分").GetString());
            Assert.Contains("不可用", Text(book.Worksheet(Sheets[3])));
            var row = book.Worksheet(Sheets[2]).RowsUsed().Single(r => r.Cell(1).GetString() == "InvalidPolicy");
            Assert.Equal("—", row.Cell(3).GetString()); Assert.Equal("—", row.Cell(4).GetString());
            Assert.Contains("检查不通过", Text(book.Worksheet(Sheets[0])));
            Retain(path, "input-invalid", workspace, new TournamentScheduleQualityAnalyzer().Analyze(request, workspace.Schedule!.Placements));
        }
    }

    [Fact]
    public void WorkbookPreservesLiteralNamesAndPrintableSixSheetStructureWithoutFormulasOrExternalLinks()
    {
        var (path, book) = Write(WorkspaceScheduleQualityFixture.MultiProject()); using (book)
        {
            Assert.Contains(book.Worksheets.SelectMany(s => s.CellsUsed()), c => c.GetString() == "=1+1");
            foreach (var sheet in book.Worksheets)
            {
                Assert.DoesNotContain(sheet.CellsUsed(), c => c.HasFormula);
                Assert.NotEmpty(sheet.PageSetup.PrintAreas); Assert.Equal(1, sheet.PageSetup.PagesWide); Assert.Equal(0, sheet.PageSetup.PagesTall);
                Assert.Equal(1, sheet.PageSetup.FirstRowToRepeatAtTop); Assert.Equal(4, sheet.PageSetup.LastRowToRepeatAtTop);
                Assert.True(sheet.SheetView.SplitRow >= 4); Assert.False(sheet.ShowGridLines);
                Assert.True(sheet.PageSetup.PaperSize is XLPaperSize.A3Paper or XLPaperSize.A4Paper);
            }
            Assert.Equal(XLPageOrientation.Landscape, book.Worksheet(Sheets[1]).PageSetup.PageOrientation);
            Assert.Contains("未发现硬约束违规", Text(book.Worksheet(Sheets[2])));
        }
        using var zip = ZipFile.OpenRead(path); Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("externalLinks", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("target", double.NaN)]
    [InlineData("target", double.PositiveInfinity)]
    [InlineData("target", double.NegativeInfinity)]
    [InlineData("target", 2d)]
    [InlineData("warning", double.NaN)]
    [InlineData("warning", double.PositiveInfinity)]
    [InlineData("warning", double.NegativeInfinity)]
    [InlineData("warning", 2d)]
    [InlineData("progress", double.NaN)]
    [InlineData("progress", double.PositiveInfinity)]
    [InlineData("progress", double.NegativeInfinity)]
    [InlineData("progress", 2d)]
    public void InvalidPolicyNumbersRemainInspectableWithoutBecomingExcelNumbers(string field, double value)
    {
        var workspace = WorkspaceScheduleQualityFixture.MultiProject(); var policy = workspace.Schedule!.Policy;
        policy = field switch
        {
            "target" => policy with { DayLoadTargets = [policy.DayLoadTargets[0] with { TargetUtilization = value }] },
            "warning" => policy with { DayLoadTargets = [policy.DayLoadTargets[0] with { WarningUtilization = value }] },
            "progress" => policy with { StageWaveTargets = [policy.StageWaveTargets[0] with { CumulativeProgress = value }] },
            _ => throw new ArgumentException(field)
        };
        workspace = workspace with { Schedule = workspace.Schedule with { Policy = policy } };
        _ = new WorkspaceScheduleExportContext(workspace); // Structurally accepted; scheduling input must still fail.
        var request = WorkspaceScheduleQualityFixture.Request(workspace);
        Assert.Contains(new TournamentPlacementValidator(request).ValidateInput().Violations, v => v.Code == SchedulingConstraintCode.InvalidPolicy);
        var expected = new TournamentScheduleQualityAnalyzer().Analyze(request, workspace.Schedule.Placements);
        var (_, book) = Write(workspace); using (book)
        {
            var summary = book.Worksheet(Sheets[0]);
            Assert.Equal(expected.HardConstraintCount, Value(summary, "硬约束违规项数").GetValue<int>());
            Assert.Contains("禁止作为现场执行赛程/运营材料放行依据", Text(summary));
            Assert.Equal("不可用（排程输入无效）", Value(summary, "当前基线软约束评分").GetString());
            Assert.Contains("不可用（排程输入无效）", Text(book.Worksheet(Sheets[3])));
            Assert.Contains("InvalidPolicy", Text(book.Worksheet(Sheets[2])));
            var row = book.Worksheet(Sheets[4]).RowsUsed().Single(r => r.Cell(1).GetString() == (field == "progress" ? "阶段目标" : "负荷目标"));
            var cell = row.Cell(field == "warning" ? 4 : 3);
            if (double.IsFinite(value)) { Assert.Equal(XLDataType.Number, cell.DataType); Assert.Equal(value, cell.GetDouble()); }
            else { Assert.Equal(XLDataType.Text, cell.DataType); Assert.Equal("无效原值：" + value.ToString("R", CultureInfo.InvariantCulture), cell.GetString()); }
            Assert.False(cell.HasFormula);
        }
    }

    [Fact]
    public void DefaultGenerationInstantDoesNotCreateFileAndMalformedContextCannotProduceReport()
    {
        var path = System.IO.Path.Combine(directory, "rejected.xlsx");
        Assert.Throws<ArgumentException>(() => new WorkspaceScheduleQualityExcelWriter().Write(path, new(WorkspaceScheduleQualityFixture.MultiProject()), default));
        Assert.False(File.Exists(path));
        var noSchedule = WorkspaceScheduleQualityFixture.MultiProject() with { Stage = TournamentStage.DrawsConfirmed, Schedule = null };
        Assert.Throws<WorkspaceValidationException>(() => new WorkspaceScheduleExportContext(noSchedule));
    }

    private static void Retain(string path, string scenario, TournamentWorkspace workspace, TournamentScheduleQuality expected)
    {
        var output = Environment.GetEnvironmentVariable("SZBD_QUALITY_FIXTURE_DIR"); if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output); var retained = System.IO.Path.Combine(output, scenario + ".xlsx"); File.Copy(path, retained, true);
        var request = WorkspaceScheduleQualityFixture.Request(workspace);
        File.WriteAllText(System.IO.Path.Combine(output, scenario + ".manifest.json"), JsonSerializer.Serialize(new
        {
            Scenario = scenario, OutputPath = retained, SHA256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(retained))),
            WorkspaceId = workspace.Id, WorkspaceRevision = workspace.Revision, ScheduleRevision = workspace.Schedule!.Revision,
            GeneratedAt = WorkspaceScheduleQualityFixture.GeneratedAt, workspace.Stage, ProjectIds = workspace.Projects.Select(p => p.Id),
            SourceSnapshot = JsonSerializer.Deserialize<JsonElement>(WorkspaceScheduleQualityFixture.Snapshot(workspace)),
            ExpectedFromIndependentCoreRequest = new { request.ScheduleRevision, request.ProjectNames, request.BaselinePlacements, request.Resources, request.Policy,
                Results = request.Results.Values, InputValid = new TournamentPlacementValidator(request).ValidateInput().IsValid, Quality = expected }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
