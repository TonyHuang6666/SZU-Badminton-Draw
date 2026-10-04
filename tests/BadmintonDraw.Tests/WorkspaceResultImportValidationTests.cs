using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;
using static BadmintonDraw.Tests.WorkspaceResultImportTests;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceResultImportValidationTests
{
    [Theory]
    [InlineData("workspace")]
    [InlineData("project")]
    [InlineData("match")]
    [InlineData("graph")]
    [InlineData("epoch")]
    [InlineData("coverage-day")]
    [InlineData("actual-day")]
    [InlineData("actual-missing")]
    [InlineData("status")]
    [InlineData("winner-error")]
    [InlineData("duration-error")]
    [InlineData("score-error")]
    public void RequiredIdentityDatesAndInputErrorsAreValidatedBeforePreviouslySeenHash(string fault)
    {
        var source = Fixture(); var row = Row(source); var saved = Ready(Evaluate(source, [File(row)]));
        var damaged = fault switch
        {
            "workspace" => row with { WorkspaceId = Cell(Guid.NewGuid().ToString()) },
            "project" => row with { ProjectId = Cell(Guid.NewGuid().ToString()) },
            "match" => row with { MatchId = Cell("") }, "graph" => row with { GraphRevision = Cell("old") },
            "epoch" => row with { DrawConfirmedAt = Cell("2020-01-01T00:00:00.0000000+00:00") },
            "coverage-day" => row with { RecordDay = Cell("2030-01-01") }, "actual-day" => row with { ActualPlayedDay = Cell("2030-01-01") },
            "actual-missing" => row with { ActualPlayedDay = Empty }, "status" => row with { ResultKind = Cell("Walkover") },
            "winner-error" => row with { Winner = new("", ReadError: "missing cache") },
            "duration-error" => row with { Duration = new("#VALUE!", WorkspaceRecordCellKind.Error) },
            _ => row with { Score = new("", HasFormula: true, ReadError: "missing cache") }
        };
        Rejected(Evaluate(saved, [File(damaged)]));
        if (fault != "actual-missing") Rejected(Evaluate(saved, [File(damaged with { Winner = fault == "winner-error" ? damaged.Winner : Empty })]));
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("project")]
    [InlineData("match")]
    [InlineData("graph")]
    [InlineData("epoch")]
    public void EveryQualifierMustBeLiteralEvenOnPendingRows(string field)
    {
        var source = Fixture(); var row = Row(source) with { Winner = Empty, ActualPlayedDay = Empty };
        row = field switch
        {
            "workspace" => row with { WorkspaceId = row.WorkspaceId with { HasFormula = true } },
            "project" => row with { ProjectId = row.ProjectId with { HasFormula = true } },
            "match" => row with { MatchId = row.MatchId with { HasFormula = true } },
            "graph" => row with { GraphRevision = row.GraphRevision with { HasFormula = true } },
            _ => row with { DrawConfirmedAt = row.DrawConfirmedAt with { HasFormula = true } }
        };
        var evaluation = Evaluate(source, [File(row)]); Rejected(evaluation);
        Assert.Contains(evaluation.Diagnostics, d => d.Code == "result.cell-invalid" && d.Source!.Location!.RowNumber == 6);
    }

    [Theory]
    [InlineData(WorkspaceRecordCellKind.DateTime, "2026-09-13T00:00:00.0000000", true)]
    [InlineData(WorkspaceRecordCellKind.DateTime, "2026-09-13T00:00:00.0000000+08:00", true)]
    [InlineData(WorkspaceRecordCellKind.Text, "2026-09-13", true)]
    [InlineData(WorkspaceRecordCellKind.Text, "2026-09-13T00:00:00.0000000", false)]
    [InlineData(WorkspaceRecordCellKind.DateTime, "2026-09-13T12:00:00.0000000", false)]
    [InlineData(WorkspaceRecordCellKind.Number, "46278", false)]
    [InlineData(WorkspaceRecordCellKind.Boolean, "true", false)]
    [InlineData(WorkspaceRecordCellKind.Text, "09/13/2026", false)]
    [InlineData(WorkspaceRecordCellKind.Empty, "2026-09-13", false)]
    public void DatesUseActualCellKindAndRejectTimeOrGuessedSerials(WorkspaceRecordCellKind kind, string text, bool valid)
    {
        var source = Fixture(); var row = Row(source) with { ActualPlayedDay = new(text, kind) };
        var evaluation = Evaluate(source, [File(row)]);
        if (valid) Assert.Equal(new DateOnly(2026, 9, 13), Ready(evaluation).Results.Single().Value.ActualPlayedDay);
        else Rejected(evaluation);
        var pending = Evaluate(source, [File(row with { Winner = Empty })]);
        if (valid) Ready(pending); else Rejected(pending);
    }

    [Fact]
    public void SameEpochInstantIsAcceptedAndUnrelatedRevisionOrPlacementChangeDoesNotStaleTemplate()
    {
        var source = Fixture(); var row = Row(source);
        row = row with { DrawConfirmedAt = Cell(source.Projects[0].Draw!.ConfirmedAt!.Value.ToOffset(TimeSpan.FromHours(8)).ToString("O", CultureInfo.InvariantCulture)) };
        source = source with { Revision = 8, AuditEvents = [new(Guid.NewGuid(), "Exported", DateTimeOffset.UtcNow)],
            Schedule = source.Schedule! with { Placements = source.Schedule.Placements.ToDictionary(p => p.Key, p => p.Value with { StartTime = p.Value.StartTime.AddMinutes(5), EndTime = p.Value.EndTime.AddMinutes(5) }) } };
        Assert.Single(Ready(Evaluate(source, [File(row)])).Results);
    }

    [Theory]
    [InlineData(WorkspaceRecordCellKind.Number, "18", true)]
    [InlineData(WorkspaceRecordCellKind.Number, "1.5", false)]
    [InlineData(WorkspaceRecordCellKind.Number, "NaN", false)]
    [InlineData(WorkspaceRecordCellKind.Boolean, "true", false)]
    [InlineData((WorkspaceRecordCellKind)99, "18", false)]
    [InlineData(WorkspaceRecordCellKind.Empty, "18", false)]
    public void ScalarKindCannotCoerceInvalidDurationIntoResult(WorkspaceRecordCellKind kind, string text, bool valid)
    {
        var source = Fixture(); var evaluation = Evaluate(source, [File(Row(source) with { Duration = new(text, kind) })]);
        if (valid) Ready(evaluation); else Rejected(evaluation);
    }

    [Fact]
    public void UsableCachedActualInputsAreValuesButNoMissingCacheIsEvaluatedOrSilentlyBlanked()
    {
        var source = Fixture(); var row = Row(source);
        row = row with { Winner = row.Winner with { HasFormula = true }, Score = row.Score with { HasFormula = true },
            Duration = new("20", WorkspaceRecordCellKind.Number, HasFormula: true), ActualPlayedDay = row.ActualPlayedDay with { HasFormula = true } };
        Ready(Evaluate(source, [File(row)]));
        Rejected(Evaluate(source, [File(row with { Score = row.Score with { ReadError = "missing cached value" } })]));
        Rejected(Evaluate(source, [File(row with { ActualPlayedDay = new("", HasFormula: true, ReadError: "missing cached value") })]));
    }

    [Fact]
    public void KnownCoverageDoesNotHideUncoveredMatchesAndDateMustNotBeInferredFromPlacement()
    {
        var source = Fixture(2); var pending = Row(source) with { Winner = Empty, Score = Empty, Duration = Empty, ActualPlayedDay = Empty };
        var covered = Ready(Evaluate(source, [File(pending)]));
        Assert.Empty(covered.Results); Assert.Single(covered.ProcessedDays[0].CoveredMatches); Assert.Equal(TournamentStage.ScheduleReady, covered.Stage);
        var missingDate = Row(source, project: 1) with { ActualPlayedDay = Empty };
        Rejected(Evaluate(covered, [File(missingDate, 'b')])); Assert.Empty(covered.Results);
    }

    [Theory]
    [InlineData(WorkspaceRecordCellKind.Number, "21", true)]
    [InlineData(WorkspaceRecordCellKind.Number, "21.5", true)]
    [InlineData(WorkspaceRecordCellKind.Number, "NaN", false)]
    [InlineData(WorkspaceRecordCellKind.Number, "Infinity", false)]
    [InlineData(WorkspaceRecordCellKind.Number, "not-a-number", false)]
    [InlineData(WorkspaceRecordCellKind.Text, "比分待核对", true)]
    [InlineData(WorkspaceRecordCellKind.Boolean, "true", false)]
    [InlineData(WorkspaceRecordCellKind.Error, "#VALUE!", false)]
    public void ReadablePendingScoreDraftDoesNotBecomeAResult(WorkspaceRecordCellKind kind, string text, bool allowedDraft)
    {
        var source = Fixture(); var row = Row(source) with { Winner = Empty, ActualPlayedDay = Empty, Duration = Empty, Score = new(text, kind) };
        var evaluation = Evaluate(source, [File(row)]);
        if (allowedDraft)
        {
            var candidate = Ready(evaluation); Assert.Empty(candidate.Results); Assert.Equal(TournamentStage.ScheduleReady, candidate.Stage);
            Assert.False(candidate.ImportLogs[0].Rows[0].HadResult); Assert.Contains(evaluation.Diagnostics, d => d.Code == "result.pending-details");
        }
        else Rejected(evaluation);
        // The same incomplete payload is not an accepted score once a winner is selected.
        Rejected(Evaluate(source, [File(row with { Winner = Cell("A"), ActualPlayedDay = Cell("2026-09-13"), Duration = Cell("20") })]));
        Rejected(Evaluate(source, [File(row with { Score = new("21", WorkspaceRecordCellKind.Number, ReadError: "missing cached value") })]));
    }

    [Fact]
    public void PendingNumericDraftNeverRewritesAnExistingCorrectedResult()
    {
        var source = Fixture(); var row = Row(source);
        var imported = Ready(Evaluate(source, [File(row, 'b')]));
        var corrected = Ready(Evaluate(imported, [File(row with { Score = Cell("21-9") }, 'c')], Options(true, "已核对")));
        var pending = row with { Winner = Empty, ActualPlayedDay = Empty, Duration = Empty, Score = new("21", WorkspaceRecordCellKind.Number) };
        var evaluation = Evaluate(corrected, [File(pending)], Options(true, "不能误更正"));
        var candidate = Ready(evaluation);
        Assert.Same(corrected.Results.Single().Value, candidate.Results.Single().Value);
        Assert.Same(corrected.ResultHistory.Single(), candidate.ResultHistory.Single()); Assert.Empty(evaluation.Corrections);
        Assert.Equal(0, evaluation.ProposedCounts.AddedResultCount); Assert.Equal(0, evaluation.ProposedCounts.CorrectionCount);
        Assert.False(candidate.ImportLogs.Single(log => log.ContentHash[0] == 'a').Rows.Single().HadResult);
        Assert.Equal(TournamentStage.Completed, candidate.Stage);
    }

    [Fact]
    public void TeamsUseOneAggregateTallyNotIndividualGameHeuristicsOrInventedThresholds()
    {
        var source = Fixture(team: true); var row = Row(source) with { Score = Cell("3-2") };
        Assert.Equal("3-2", Ready(Evaluate(source, [File(row)])).Results.Single().Value.Score);
        Ready(Evaluate(source, [File(row with { Score = Cell("1-0") })]));
        Rejected(Evaluate(source, [File(row with { Score = Cell("21-19, 21-19") })]));
        Rejected(Evaluate(source, [File(row with { Score = Cell("2-3") })]));
    }

    [Fact]
    public void FullOptionsUseGraphLabelsAndDoublesKeepBothIdentitiesWithoutNameSplitting()
    {
        var source = Fixture(); var p = source.Projects[0];
        var roster = new[] { new DrawParticipant("李【甲】 某胜者 / 王/乙", PrimaryName: "李【甲】 某胜者", PartnerName: "王/乙", PrimaryStudentId: "001", PartnerStudentId: "002"),
            new DrawParticipant("李【甲】 某胜者 / 王/乙", PrimaryName: "李【甲】 某胜者", PartnerName: "王/乙", PrimaryStudentId: "003", PartnerStudentId: "004") };
        var a = ProjectEntrantIdentity.Create(EventDiscipline.MenDoubles, roster[0]); var b = ProjectEntrantIdentity.Create(EventDiscipline.MenDoubles, roster[1]);
        p = p with { Discipline = EventDiscipline.MenDoubles, Roster = new(roster, "名单.xlsx", "hash", []),
            MatchGraph = p.MatchGraph! with { Matches = [p.MatchGraph.Matches[0] with { SideA = a, SideB = b }] },
            Draw = p.Draw! with { Result = p.Draw.Result with { Settings = p.Draw.Result.Settings with { EventKind = EventKind.Doubles } } } };
        source = source with { Projects = [p] }; var row = Row(source);
        row = row with { Winner = Cell(WorkspaceWinnerOptionText.Format(ScheduleMatchSide.SideA, a.DisplayName).Replace(" ", "\n  ")),
            OptionA = Cell("伪造的下拉身份"), SideA = Cell("伪造的人") };
        var winner = Ready(Evaluate(source, [File(row)])).Results.Single().Value.Winner;
        Assert.Equal(new[] { "001", "002" }, winner.Players.Select(player => player.StudentId));
        var sideB = Ready(Evaluate(source, [File(row with { Winner = Cell("b"), Score = Cell("10-21") })])).Results.Single().Value.Winner;
        Assert.Equal(new[] { "003", "004" }, sideB.Players.Select(player => player.StudentId));
        Rejected(Evaluate(source, [File(row with { Winner = Cell(a.DisplayName) })]));
        Rejected(Evaluate(source, [File(row with { Winner = Cell("A【伪造的下拉身份】") })]));
    }

    [Fact]
    public void CanonicallyEqualExistingScoreKeepsWholeResultAndCorrectionAppendsRealHistoryVersions()
    {
        var source = Fixture(); var row = Row(source); var first = Ready(Evaluate(source, [File(row)]));
        var original = first.Results.Single().Value with { Score = "021 － 010" };
        first = first with { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [original.Key] = original } };
        var same = Ready(Evaluate(first, [File(row, 'b')])); Assert.Same(original, same.Results.Single().Value); Assert.Empty(same.ResultHistory);
        var second = Ready(Evaluate(same, [File(row with { Score = Cell("21-9") }, 'c')], Options(true, "第一次核对")));
        var third = Ready(Evaluate(second, [File(row with { ResultKind = Cell("弃权"), Duration = Cell("0"), Score = Cell("未开赛") }, 'd')], Options(true, "第二次核对")));
        Assert.Equal(new long[] { 1, 2 }, third.ResultHistory.Select(h => h.Sequence));
        Assert.Equal(third.ResultHistory[0].After, third.ResultHistory[1].Before);
        Assert.Same(original, third.ResultHistory[0].Before); Assert.Equal(TournamentStage.Completed, third.Stage);
        var badTime = Options(true, "时间错误") with { ImportedAt = original.RecordedAt.AddDays(-2) };
        Rejected(Evaluate(first, [File(row with { Score = Cell("21-8") }, 'e')], badTime));
    }

    [Fact]
    public void DerivedIdCollisionsAndMalformedFilesRejectWithoutMutatingSource()
    {
        var source = Fixture(); var row = Row(source); var options = Options();
        var derivedLog = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{options.OperationId:D}:log:{new string('a', 64)}")).AsSpan(0, 16));
        var collision = source with { AuditEvents = [new(derivedLog, "Existing", options.ImportedAt)] };
        Rejected(Evaluate(collision, [File(row)], options));
        Rejected(Evaluate(source, [File(row) with { Document = new("bad-hash", [row]) }]));
        Rejected(Evaluate(source, [File(row) with { SourcePath = "" }]));
        Rejected(Evaluate(source, [File(row) with { Document = new(new string('b', 64), []) }]));
        Rejected(Evaluate(source, [File(row) with { Document = new(new string('b', 64), [row, row]) }]));
    }
}
