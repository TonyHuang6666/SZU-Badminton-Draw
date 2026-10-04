using System.Text.Json;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;
using static BadmintonDraw.Tests.WorkspaceResultImportTests;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceResultImportBatchTests
{
    [Fact]
    public void ReverseOrderedPrerequisitesWinnerAndLoserBranchesResolveAcrossFilesAndProjects()
    {
        var source = Fixture(2, bracket: true); var files = new List<WorkspaceRecordImportFile>();
        // Hash sorting also places dependents first: file sorting cannot accidentally satisfy topology.
        for (var p = 0; p < 2; p++) for (var m = 0; m < 4; m++) files.Add(File(Row(source, p, m), (char)('0' + 7 - p * 4 - m)));
        var options = Options(); var forward = Ready(Evaluate(source, files, options));
        var reverse = Ready(Evaluate(source, files.AsEnumerable().Reverse().ToArray(), options));
        Assert.Equal(Snapshot(forward), Snapshot(reverse)); Assert.Equal(8, reverse.Results.Count); Assert.Equal(TournamentStage.Completed, reverse.Stage);
        foreach (var project in source.Projects)
        {
            var nodes = project.MatchGraph!.Matches;
            Assert.Equal(forward.Results[new(project.Id, nodes[0].Id)].Winner, forward.Results[new(project.Id, nodes[2].Id)].Winner);
            Assert.Equal(forward.Results[new(project.Id, nodes[0].Id)].Loser, forward.Results[new(project.Id, nodes[3].Id)].Winner);
        }
    }

    [Fact]
    public void UnresolvedCompletedDependentBlocksButPendingDependentIsCoverage()
    {
        var source = Fixture(bracket: true); var row = Row(source, match: 2);
        var bad = Evaluate(source, [File(row)]); Rejected(bad); Assert.Contains(bad.Diagnostics, d => d.Code == "result.upstream-missing");
        var pending = Ready(Evaluate(source, [File(row with { Winner = Empty, ActualPlayedDay = Empty })]));
        Assert.Empty(pending.Results); Assert.Single(pending.ProcessedDays);
    }

    [Fact]
    public void ReverseWorksheetRowsResolveCrossDayResultsWithoutUsingImportedHelperNames()
    {
        var source = Fixture(bracket: true); var ids = source.Projects[0].MatchGraph!.Matches.Select(n => n.Id).ToArray();
        source = source with { Schedule = source.Schedule! with { Placements = source.Schedule.Placements.ToDictionary(p => p.Key,
            p => p.Key == ids[2] || p.Key == ids[3] ? p.Value with { DayLabel = "2026-09-14" } : p.Value) } };
        var rows = Enumerable.Range(0, 4).Reverse().Select((m, index) => Row(source, match: m, row: index + 6) with
        { RecordDay = Cell(m >= 2 ? "2026-09-14" : "2026-09-13"), ActualPlayedDay = Cell(m >= 2 ? "2026-09-14" : "2026-09-13"), SideA = Cell("错误的显示缓存") }).ToArray();
        var file = File(rows[0]) with { Document = new(new string('a', 64), rows) };
        var completed = Ready(Evaluate(source, [file]));
        Assert.Equal(4, completed.Results.Count); Assert.Equal(2, completed.ProcessedDays.Count); Assert.Equal(TournamentStage.Completed, completed.Stage);
        Assert.Same(source.Schedule, completed.Schedule);
    }

    [Fact]
    public void WinnerChangeCannotRewriteRecordedDescendantsOrTheirLosers()
    {
        var source = Fixture(bracket: true);
        var files = Enumerable.Range(0, 4).Select(i => File(Row(source, match: i), (char)('a' + i))).ToArray();
        var completed = Ready(Evaluate(source, files)); var before = Snapshot(completed);
        var changedUpstream = Row(source, match: 1) with { Winner = Cell("B"), Score = Cell("10-21") };
        // Switching the second semifinal would change the recorded final's loser even if its winner stays A.
        var evaluation = Evaluate(completed, [File(changedUpstream, 'e'), File(Row(source, match: 2), 'f')], Options(true, "不允许改变晋级结果"));
        Rejected(evaluation); Assert.Contains(evaluation.Diagnostics, d => d.Code == "result.participants-changed");
        Assert.Equal(before, Snapshot(completed)); Assert.Equal(4, completed.Results.Count);
    }

    [Theory]
    [InlineData("score")]
    [InlineData("duration")]
    [InlineData("day")]
    [InlineData("kind")]
    [InlineData("winner")]
    public void DifferentCompletedPayloadsInAnyNewFilesBlockEvenWhenCorrectionsAllowed(string field)
    {
        var source = Fixture(); var row = Row(source); var other = field switch
        {
            "score" => row with { Score = Cell("21-9") }, "duration" => row with { Duration = Cell("21") },
            "day" => row with { ActualPlayedDay = Cell("2026-09-14") },
            "kind" => row with { ResultKind = Cell("弃权"), Duration = Cell("0") },
            _ => row with { Winner = Cell("B"), Score = Cell("10-21") }
        };
        var evaluation = Evaluate(source, [File(row), File(other, 'b')], Options(true, "允许更正"));
        Rejected(evaluation); Assert.Contains(evaluation.Diagnostics, d => d.Code == "result.batch-conflict" && d.RelatedSources.Count == 2);
        Rejected(Evaluate(source, [File(row) with { Document = new(new string('c', 64), [row, other with { Location = new("对阵记录表", 7) }]) }], Options(true, "允许更正")));
    }

    [Fact]
    public void SameResultDuplicatesHaveOneOwnerWhileEveryPendingAndCompletedSourceRowSurvives()
    {
        var source = Fixture(); var row = Row(source); var options = Options();
        var a = File(row) with { Document = new(new string('a', 64), [row, row with { Location = new("对阵记录表", 7), Winner = Empty }]) };
        var b = File(row with { Score = Cell("021－010"), Duration = Cell("20m") }, 'b');
        var candidate = Ready(Evaluate(source, [b, a], options));
        Assert.Single(candidate.Results); Assert.Equal(2, candidate.ImportLogs.Count);
        Assert.Equal(1, candidate.ImportLogs.Single(l => l.ContentHash[0] == 'a').AddedResultCount);
        Assert.Equal(0, candidate.ImportLogs.Single(l => l.ContentHash[0] == 'b').AddedResultCount);
        Assert.Equal(3, candidate.ImportLogs.Sum(l => l.Rows.Count)); Assert.Single(candidate.ProcessedDays[0].CoveredMatches);
        var copies = Evaluate(source, [a with { SourcePath = "/z.xlsx" }, a with { SourcePath = "/a.xlsx" }], options);
        Assert.Single(Ready(copies).ImportLogs); Assert.Equal("/a.xlsx", copies.Candidate!.ImportLogs[0].SourcePath);
        Assert.Contains(copies.Files, f => f.Status == ResultImportFileStatus.DuplicateInBatch);
        Rejected(Evaluate(source, [a, a with { Document = new(a.Document.ContentHash, [row with { Score = Cell("21-8") }]) }], options));
    }

    [Fact]
    public void OldHashIsNoOpAfterCorrectionButNewIdenticalResultFilePreservesWholeRecordedVersion()
    {
        var source = Fixture(); var row = Row(source); var imported = Ready(Evaluate(source, [File(row)]));
        var correctionFile = File(row with { Score = Cell("21-9") }, 'b');
        var corrected = Ready(Evaluate(imported, [correctionFile], Options(true, "核对原始记录")));
        var duplicate = Evaluate(corrected, [File(row)]);
        Assert.Equal(ResultImportEvaluationStatus.NoChanges, duplicate.Status); Assert.Null(duplicate.Candidate);
        Assert.Empty(duplicate.Corrections);
        var newFile = Ready(Evaluate(corrected, [correctionFile with { Document = new(new string('c', 64), [row with { Score = Cell("021–009"), Duration = Cell("20分钟") }]) }]));
        Assert.Same(corrected.Results.Single().Value, newFile.Results.Single().Value);
        Assert.Single(newFile.ResultHistory); Assert.Equal(3, newFile.ImportLogs.Count);
    }

    [Fact]
    public void CorrectionRequiresPermissionAndReasonAndHasOneDeterministicHistoryOwner()
    {
        var source = Fixture(); var row = Row(source); var imported = Ready(Evaluate(source, [File(row)]));
        var update = row with { Score = Cell("21-8"), ActualPlayedDay = Cell("2026-09-14") };
        foreach (var options in new[] { Options(), Options(true, " "), Options(false, "记录错误") })
        {
            var preview = Evaluate(imported, [File(update, 'b')], options);
            Assert.Equal(ResultImportEvaluationStatus.RequiresConfirmation, preview.Status); Assert.Null(preview.Candidate); Assert.Single(preview.Corrections);
        }
        var acceptedOptions = Options(true, "核对纸质记录");
        var corrected = Ready(Evaluate(imported, [File(update, 'c'), File(update, 'b')], acceptedOptions));
        var history = Assert.Single(corrected.ResultHistory);
        Assert.Same(imported.Results.Single().Value, history.Before); Assert.Equal(acceptedOptions.ImportedAt, history.ChangedAt);
        Assert.Equal(history.After, corrected.Results.Single().Value); Assert.Equal(1, history.Sequence);
        Assert.Equal(corrected.ImportLogs.Single(l => l.ContentHash[0] == 'b').Id, history.ImportLogId);
        Assert.Equal(1, corrected.ImportLogs.Sum(l => l.CorrectionCount)); Assert.Equal(TournamentStage.Completed, corrected.Stage);
        Rejected(Evaluate(imported, [File(update with { Winner = Cell("B"), Score = Cell("8-21") }, 'd')], Options(true, "不能改胜负方")));
    }

    [Fact]
    public void StillCurrentVoidedReceiptDoesNotReactivateButStaleEpochAlwaysBlocks()
    {
        var source = Fixture(); var row = Row(source) with { Winner = Empty, ActualPlayedDay = Empty };
        var imported = Ready(Evaluate(source, [File(row)])); var log = imported.ImportLogs[0];
        var audit = new WorkspaceAuditEvent(Guid.NewGuid(), "ScheduleGenerated", log.ImportedAt.AddMinutes(1));
        imported = imported with { ImportLogs = [log with { VoidedAt = audit.OccurredAt, VoidReason = "重排", VoidedByAuditEventId = audit.Id }], ProcessedDays = [], AuditEvents = [..imported.AuditEvents, audit] };
        var duplicate = Evaluate(imported, [File(row)]);
        Assert.Equal(ResultImportEvaluationStatus.NoChanges, duplicate.Status); Assert.Null(duplicate.Candidate);
        Assert.Equal(ResultImportFileStatus.PreviouslyVoided, Assert.Single(duplicate.Files).Status);
        var p = imported.Projects[0]; imported = imported with { Projects = [p with { Draw = p.Draw! with { ConfirmedAt = p.Draw.ConfirmedAt!.Value.AddMinutes(1) } }] };
        Rejected(Evaluate(imported, [File(row)]));
    }

    [Fact]
    public void InvalidContextAndOutputMutationCannotCreateAnUnvalidatedCandidate()
    {
        var source = Fixture(); var files = new[] { File(Row(source)) }; var options = Options();
        Rejected(Evaluate(source, files, options with { OperationId = Guid.Empty }));
        Rejected(Evaluate(source, files, options with { ImportedAt = default }));
        Rejected(Evaluate(source, []));
        var result = Evaluate(source, files, options); Ready(result);
        Assert.Throws<NotSupportedException>(() => ((IList<ResultImportFileEvaluation>)result.Files).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ResultImportDiagnostic>)result.Diagnostics).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ResultImportCorrection>)result.Corrections).Clear());
        var collision = source with { AuditEvents = [new(options.OperationId, "Existing", options.ImportedAt)] };
        Rejected(Evaluate(collision, files, options));
    }
}
