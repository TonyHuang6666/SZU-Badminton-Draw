using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceOperationsContractsTests
{
    [Fact]
    public void ExplicitZeroDurationWalkoverPassesDomainProjectionAndBoardValidation()
    {
        var workspace = TournamentWorkspaceRulesTests.Fixture(TournamentStage.Completed);
        var result = workspace.Results.Single().Value with
        { Kind = TournamentResultKind.Walkover, Score = "", DurationMinutes = 0, ActualPlayedDay = new(2026, 9, 13) };
        workspace = workspace with { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [result.Key] = result } };

        TournamentWorkspaceRules.Validate(workspace);
        Assert.Single(ScheduledMatchProjection.Build(workspace.Projects.Select(p => p.MatchGraph!).ToArray(), workspace.Schedule!, workspace.Results));
        var request = new TournamentSchedulingRequest(workspace.Projects.Select(p => p.MatchGraph!).ToArray(), workspace.Resources!, workspace.Schedule!.Policy)
        { Results = workspace.Results, BaselinePlacements = workspace.Schedule.Placements };
        var validator = new TournamentPlacementValidator(request);
        Assert.True(validator.ValidateSchedule(workspace.Schedule.Placements).IsValid);
        Assert.False(validator.ValidateSchedule(workspace.Schedule.Placements.ToDictionary(p => p.Key,
            p => p.Value with { StartTime = p.Value.StartTime.AddMinutes(1), EndTime = p.Value.EndTime.AddMinutes(1) })).IsValid);
        using var temp = new WorkspaceStoreTemp();
        new TournamentWorkspaceStore().Create(temp.Path, workspace);
        var saved = new TournamentWorkspaceStore().Read(temp.Path).Results.Single().Value;
        Assert.Equal(TournamentResultKind.Walkover, saved.Kind);
        Assert.Equal(0, saved.DurationMinutes);
        Assert.Equal("", saved.Score);
    }

    [Fact]
    public void AllOperationsTablesRoundTripAndPreserveTheActualPlayedDay()
    {
        using var temp = new WorkspaceStoreTemp();
        var expected = Fixture();
        var store = new TournamentWorkspaceStore();
        store.Create(temp.Path, expected);
        var actual = store.Read(temp.Path);

        Assert.Equal(JsonSerializer.Serialize(expected.ImportLogs), JsonSerializer.Serialize(actual.ImportLogs));
        Assert.Equal(JsonSerializer.Serialize(expected.ResultHistory), JsonSerializer.Serialize(actual.ResultHistory));
        Assert.Equal(JsonSerializer.Serialize(expected.ProcessedDays), JsonSerializer.Serialize(actual.ProcessedDays));
        Assert.Equal(new DateOnly(2026, 9, 13), actual.Results.Single().Value.ActualPlayedDay);
        Assert.Equal("21-11", actual.ResultHistory.Single().Before.Score);
        Assert.Equal("21-10", actual.Results.Single().Value.Score);
    }

    [Theory]
    [InlineData(TournamentResultKind.Played, "", 20)]
    [InlineData(TournamentResultKind.Played, "21-10", 0)]
    [InlineData(TournamentResultKind.Walkover, "", 1)]
    [InlineData(TournamentResultKind.Walkover, "", -1)]
    [InlineData((TournamentResultKind)99, "21-10", 20)]
    public void InvalidResultKindsOrValuesFailAllThreeConsumers(TournamentResultKind kind, string score, int duration)
    {
        var workspace = Fixture();
        var result = workspace.Results.Single().Value with { Kind = kind, Score = score, DurationMinutes = duration };
        workspace = workspace with { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [result.Key] = result } };
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Validate(workspace));
        Assert.Throws<WorkspaceValidationException>(() => ScheduledMatchProjection.Build(workspace.Projects.Select(p => p.MatchGraph!).ToArray(), workspace.Schedule!, workspace.Results));
        var request = new TournamentSchedulingRequest(workspace.Projects.Select(p => p.MatchGraph!).ToArray(), workspace.Resources!, workspace.Schedule!.Policy)
        { Results = workspace.Results, BaselinePlacements = workspace.Schedule.Placements };
        Assert.Contains(new TournamentPlacementValidator(request).ValidateInput().Violations, v => v.Code == SchedulingConstraintCode.InvalidResult);
    }

    [Fact]
    public void UnknownActualDayStaysExplicitWhileAnUnconfiguredDateIsRejected()
    {
        var workspace = TournamentWorkspaceRulesTests.Fixture(TournamentStage.Completed);
        Assert.Null(workspace.Results.Single().Value.ActualPlayedDay);
        TournamentWorkspaceRules.Validate(workspace);
        var result = workspace.Results.Single().Value with { ActualPlayedDay = new(2030, 1, 1) };
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Validate(workspace with
        { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [result.Key] = result } }));
    }

    [Fact]
    public void ActualPlayedDayCanDifferFromPlacementWithoutMovingTheSchedule()
    {
        using var temp = new WorkspaceStoreTemp();
        var workspace = TournamentWorkspaceRulesTests.Fixture(TournamentStage.Completed);
        var resources = workspace.Resources! with { Days = [workspace.Resources!.Days[0],
            new(new(2026, 9, 14), new(9, 0), new(18, 0), ["1"])] };
        var result = workspace.Results.Single().Value with { ActualPlayedDay = new(2026, 9, 14) };
        workspace = workspace with { Resources = resources, Schedule = workspace.Schedule! with { Resources = resources },
            Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [result.Key] = result } };
        new TournamentWorkspaceStore().Create(temp.Path, workspace);
        var saved = new TournamentWorkspaceStore().Read(temp.Path);
        Assert.Equal(new DateOnly(2026, 9, 14), saved.Results.Single().Value.ActualPlayedDay);
        Assert.Equal("2026-09-13", saved.Schedule!.Placements.Single().Value.DayLabel);
    }

    [Theory]
    [InlineData("log-id")]
    [InlineData("hash")]
    [InlineData("duplicate-hash")]
    [InlineData("log-time")]
    [InlineData("location")]
    [InlineData("duplicate-location")]
    [InlineData("foreign-match")]
    [InlineData("foreign-day")]
    [InlineData("warning-location")]
    [InlineData("counts")]
    [InlineData("coverage-missing")]
    [InlineData("coverage-key")]
    [InlineData("coverage-time")]
    [InlineData("history-reason")]
    [InlineData("history-id")]
    [InlineData("history-sequence")]
    [InlineData("history-log")]
    [InlineData("history-source")]
    [InlineData("history-key")]
    [InlineData("history-players")]
    [InlineData("history-player-name")]
    [InlineData("history-unchanged")]
    [InlineData("history-current")]
    [InlineData("void-result")]
    public void InvalidOperationsEvidenceCannotEnterTheAggregate(string fault)
    {
        var workspace = Fixture(); var log = workspace.ImportLogs[0]; var row = log.Rows[0]; var history = workspace.ResultHistory[0];
        workspace = fault switch
        {
            "log-id" => workspace with { ImportLogs = [log with { Id = Guid.Empty }] },
            "hash" => workspace with { ImportLogs = [log with { ContentHash = "not-a-hash" }] },
            "duplicate-hash" => workspace with { ImportLogs = [log, log with { Id = Guid.NewGuid() }] },
            "log-time" => workspace with { ImportLogs = [log with { ImportedAt = default }] },
            "location" => workspace with { ImportLogs = [log with { Rows = [row with { Location = new("", 0) }] }] },
            "duplicate-location" => workspace with { ImportLogs = [log with { Rows = [row, row] }] },
            "foreign-match" => workspace with { ImportLogs = [log with { Rows = [row with { Key = new(Guid.NewGuid(), row.Key.MatchId) }] }] },
            "foreign-day" => workspace with { ImportLogs = [log with { Rows = [row with { RecordDay = new(2030, 1, 1) }] }] },
            "warning-location" => workspace with { ImportLogs = [log with { Warnings = [new("warning", "提示", new("不存在", 6))] }] },
            "counts" => workspace with { ImportLogs = [log with { CorrectionCount = 2 }] },
            "coverage-missing" => workspace with { ProcessedDays = [] },
            "coverage-key" => workspace with { ProcessedDays = [workspace.ProcessedDays[0] with { CoveredMatches = [] }] },
            "coverage-time" => workspace with { ProcessedDays = [workspace.ProcessedDays[0] with { UpdatedAt = default }] },
            "history-reason" => workspace with { ResultHistory = [history with { Reason = " " }] },
            "history-id" => workspace with { ResultHistory = [history with { Id = Guid.Empty }] },
            "history-sequence" => workspace with { ResultHistory = [history with { Sequence = 2 }] },
            "history-log" => workspace with { ResultHistory = [history with { ImportLogId = Guid.NewGuid() }] },
            "history-source" => workspace with { ResultHistory = [history with { Source = new("对阵记录表", 99) }] },
            "history-key" => workspace with { ResultHistory = [history with { Key = new(Guid.NewGuid(), history.Key.MatchId) }] },
            "history-players" => workspace with { ResultHistory = [history with { Before = history.Before with { Winner = history.Before.Loser, Loser = history.Before.Winner } }] },
            "history-player-name" => workspace with { ResultHistory = [history with { Before = history.Before with { Winner = history.Before.Winner with { DisplayName = "" } } }] },
            "history-unchanged" => workspace with { ResultHistory = [history with { Before = history.After }] },
            "history-current" => workspace with { ResultHistory = [history with { After = history.After with { Score = "21-9" } }] },
            "void-result" => workspace with { ImportLogs = [log with { VoidedAt = log.ImportedAt, VoidReason = "重排", VoidedByAuditEventId = Guid.NewGuid() }] },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Validate(workspace));
    }

    [Fact]
    public void VoidedPendingReceiptsRetainProvenanceAfterTheirProjectWasRemoved()
    {
        using var temp = new WorkspaceStoreTemp();
        var workspace = Fixture(); var log = workspace.ImportLogs[0];
        var audit = new WorkspaceAuditEvent(Guid.NewGuid(), "DrawReopened", log.ImportedAt.AddMinutes(1), log.Rows[0].Key.ProjectId, Detail: "名单更正");
        log = log with { Rows = [log.Rows[0] with { HadResult = false }], AddedResultCount = 0, CorrectionCount = 0,
            VoidedAt = audit.OccurredAt, VoidReason = "名单更正", VoidedByAuditEventId = audit.Id };
        workspace = TournamentWorkspace.Create("重新准备名单", TournamentKind.Individual, TournamentPurpose.FullTournament,
            [TournamentProject.Create(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout, 0)]) with
        { ImportLogs = [log], AuditEvents = [audit] };
        TournamentWorkspaceRules.Validate(workspace);
        new TournamentWorkspaceStore().Create(temp.Path, workspace);
        var restored = new TournamentWorkspaceStore().Read(temp.Path);
        Assert.Empty(restored.ProcessedDays);
        Assert.Equal(log.Rows[0].Key, restored.ImportLogs[0].Rows[0].Key);
        Assert.Equal(log.ContentHash, restored.ImportLogs[0].ContentHash);
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Validate(workspace with { AuditEvents = [] }));
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Validate(workspace with
        { ImportLogs = [log with { VoidedAt = null }] }));
    }

    [Fact]
    public void OperationsCollectionsDefensivelyCopyCallerOwnedLists()
    {
        var fixture = Fixture();
        var rows = fixture.ImportLogs[0].Rows.ToList(); var warnings = fixture.ImportLogs[0].Warnings.ToList();
        var keys = fixture.ProcessedDays[0].CoveredMatches.ToList();
        var logs = new List<WorkspaceImportLog> { fixture.ImportLogs[0] with { Rows = rows, Warnings = warnings } };
        var days = new List<WorkspaceProcessedDay> { fixture.ProcessedDays[0] with { CoveredMatches = keys } };
        var history = fixture.ResultHistory.ToList();
        var snapshot = fixture with { ImportLogs = logs, ProcessedDays = days, ResultHistory = history };
        rows.Clear(); warnings.Clear(); keys.Clear(); logs.Clear(); days.Clear(); history.Clear();
        TournamentWorkspaceRules.Validate(snapshot);
        Assert.Single(snapshot.ImportLogs[0].Rows);
        Assert.Single(snapshot.ImportLogs[0].Warnings);
        Assert.Single(snapshot.ProcessedDays[0].CoveredMatches);
        Assert.Single(snapshot.ResultHistory);
        Assert.Throws<NotSupportedException>(() => ((IList<WorkspaceImportedRow>)snapshot.ImportLogs[0].Rows).Clear());
    }

    [Fact]
    public void CorrectionChainKeepsEveryVersionAndRejectsABrokenIntermediateVersion()
    {
        var workspace = Fixture(); var last = workspace.ResultHistory[0]; var finalLog = workspace.ImportLogs[0];
        var firstLog = finalLog with { Id = Guid.NewGuid(), ImportedAt = last.Before.RecordedAt, ContentHash = new string('b', 64) };
        var first = last with { Id = Guid.NewGuid(), Sequence = 1, ImportLogId = firstLog.Id,
            Before = last.Before with { Score = "21-12", RecordedAt = last.Before.RecordedAt.AddMinutes(-1) },
            After = last.Before, ChangedAt = last.Before.RecordedAt };
        workspace = workspace with { ImportLogs = [firstLog, finalLog], ResultHistory = [first, last with { Sequence = 2 }] };
        TournamentWorkspaceRules.Validate(workspace);
        Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Validate(workspace with
        { ResultHistory = [first with { After = first.After with { Score = "21-13" } }, last with { Sequence = 2 }] }));
    }

    internal static TournamentWorkspace Fixture()
    {
        var workspace = TournamentWorkspaceRulesTests.Fixture(TournamentStage.Completed);
        var current = workspace.Results.Single().Value with { ActualPlayedDay = new(2026, 9, 13) };
        var before = current with { Score = "21-11", RecordedAt = current.RecordedAt.AddMinutes(-1) };
        var location = new WorkspaceRecordLocation("对阵记录表", 6);
        var log = new WorkspaceImportLog(Guid.NewGuid(), current.RecordedAt, "记录.xlsx", "/test/记录.xlsx", new string('a', 64),
            [new(current.Key, new(2026, 9, 13), location, true)], 0, 1, [new("test.warning", "保留提示", location)]);
        return workspace with
        {
            Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult> { [current.Key] = current },
            ImportLogs = [log],
            ProcessedDays = [new(new(2026, 9, 13), [current.Key], current.RecordedAt)],
            ResultHistory = [new(Guid.NewGuid(), 1, current.Key, before, current, log.Id, location, current.RecordedAt, "核对纸质记录后更正")]
        };
    }
}
