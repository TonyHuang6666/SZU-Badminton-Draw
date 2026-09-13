using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Tests;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class WorkspaceOperationsHistoryTests
{
    [Fact]
    public async Task PendingCoverageIsNotCompletionAndVoidedDisappearedKeysRemainFullyVisible()
    {
        using var f = new OperationsUiFixture(2); await f.Import(f.Record(fill: false));
        var before = f.Page.Session.Workspace; var log = Assert.Single(before.ImportLogs);
        Assert.Equal(0, f.Page.CompletedMatchCount); Assert.Equal(TournamentStage.ScheduleReady, before.Stage);
        Assert.Contains("非当日完赛", Assert.Single(f.Page.History.Coverage));
        foreach (var row in log.Rows)
        {
            Assert.Contains(row.Key.ProjectId.ToString(), f.Page.History.Receipts[0]);
            Assert.Contains(row.Key.MatchId.ToString(), f.Page.History.Receipts[0]);
            Assert.Contains(row.Location.SheetName, f.Page.History.Receipts[0]);
        }
        f.Workflow.ReopenDraw(before.Projects[0].Id, "公开抽签核对", before.Revision);
        var saved = f.Data.Store.Read(f.Workflow.CurrentSession!.WorkspacePath);
        var history = new WorkspaceOperationsHistory(saved); var voided = Assert.Single(saved.ImportLogs);
        Assert.Empty(history.Coverage); Assert.Empty(history.Results); Assert.Single(history.Receipts);
        var evidence = history.Receipts[0]; Assert.Contains("已作废", evidence); Assert.Contains("历史引用", evidence);
        Assert.Contains(log.SourcePath, evidence); Assert.Contains(log.ContentHash, evidence);
        Assert.Contains(voided.VoidReason!, evidence); Assert.Contains(voided.VoidedAt!.Value.ToString("O"), evidence);
        Assert.Contains(voided.VoidedByAuditEventId!.Value.ToString(), evidence);
        foreach (var row in log.Rows) Assert.Contains(row.Key.MatchId.ToString(), evidence);
    }

    [Fact]
    public async Task ReplanningDatesVoidsPendingCoverageButKeepsOriginalRowsAndRecordDay()
    {
        using var f = new OperationsUiFixture(); await f.Import(f.Record(fill: false));
        var source = f.Shell.CurrentSession!.Workspace; var log = Assert.Single(source.ImportLogs); var row = Assert.Single(log.Rows);
        var originalDay = row.RecordDay; var schedule = source.Schedule!;
        var shiftedDays = schedule.Resources.Days.Select(d => d with { Date = d.Date.AddDays(7) }).ToArray();
        var policy = schedule.Policy with { DayLoadTargets = [], StageWaveTargets = [] };
        f.Workflow.GenerateSchedule(schedule.Resources with { Days = shiftedDays }, policy, source.Revision);
        var saved = f.Data.Store.Read(f.Workflow.CurrentSession!.WorkspacePath); var history = f.Page.History;
        Assert.Empty(history.Coverage); Assert.Empty(saved.ProcessedDays); Assert.Empty(saved.Results);
        Assert.DoesNotContain(saved.Schedule!.Resources.Days, d => d.Date == originalDay);
        var text = Assert.Single(history.Receipts); Assert.Contains("已作废", text);
        Assert.Contains(originalDay.ToString("yyyy-MM-dd"), text); Assert.Contains(log.SourcePath, text); Assert.Contains(log.ContentHash, text);
        Assert.Contains(row.Key.ProjectId.ToString(), text); Assert.Contains(row.Key.MatchId.ToString(), text);
        Assert.Equal(log.Rows, Assert.Single(saved.ImportLogs).Rows);
    }

    [Fact]
    public async Task SavedCorrectionChainRetainsFullIdentityDatesSourceAndUnknownAuditVerbatim()
    {
        using var f = new OperationsUiFixture(2); await f.Import(f.Record());
        var first = f.Page.Session.Workspace;
        var correction = f.Record("correction.xlsx");
        WorkspaceResultImportFacadeFixture.Edit(correction, s => { s.Cell(6, 10).Value = 27; s.Cell(6, 22).Value = "2026-09-14"; });
        var preview = f.Workflow.PreviewResultImport([correction]); f.Workflow.ImportResults(preview, new(true, "核实实际日期"), first.Revision);
        const string detail = "陌生协议原文\n\n保留【字段】/ 零值 0\r\n尾部";
        var current = f.Workflow.CurrentSession!;
        f.Data.Store.Mutate(current.WorkspacePath, current.Workspace.Revision,
            w => w with { AuditEvents = [.. w.AuditEvents, new(Guid.NewGuid(), "FutureActionV6", DateTimeOffset.UtcNow, Detail: detail)] });
        var saved = f.Data.Store.Read(current.WorkspacePath); var history = new WorkspaceOperationsHistory(saved);
        Assert.Equal(2, history.Results.Count); var change = Assert.Single(saved.ResultHistory); var text = Assert.Single(history.Corrections);
        Assert.Contains(change.Id.ToString(), text); Assert.Contains(change.Key.ProjectId.ToString(), text); Assert.Contains(change.Key.MatchId.ToString(), text);
        Assert.Contains(change.ImportLogId.ToString(), text); Assert.Contains(change.Reason, text); Assert.Contains(change.Source.SheetName, text);
        foreach (var result in new[] { change.Before, change.After })
        {
            Assert.Contains(result.RecordedAt.ToString("O"), text); Assert.Contains(result.ActualPlayedDay!.Value.ToString("yyyy-MM-dd"), text);
            foreach (var entrant in new[] { result.Winner, result.Loser })
            {
                Assert.Contains(entrant.IdentityKey, text);
                foreach (var player in entrant.Players) { Assert.Contains(player.Name, text); Assert.Contains(player.StudentId, text); Assert.Contains(player.IdentityKey, text); }
            }
        }
        var source = saved.ImportLogs.Single(l => l.Id == change.ImportLogId); Assert.Contains(source.ContentHash, text); Assert.Contains(source.SourcePath, text);
        Assert.Contains(history.Audits, a => a.Contains("FutureActionV6", StringComparison.Ordinal) && a.EndsWith(detail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PersistedUnknownActualDateAndExplicitWalkoverZeroAreNotInferredFromPlacement()
    {
        using var f = new OperationsUiFixture(); var path = f.Record();
        WorkspaceResultImportFacadeFixture.Edit(path, s => { s.Cell(6, 21).Value = "弃权"; s.Cell(6, 9).Value = "因伤未开赛"; s.Cell(6, 10).Value = 0; });
        await f.Import(path); var actual = Assert.Single(f.Shell.CurrentSession!.Workspace.Results).Value;
        Assert.Equal(TournamentResultKind.Walkover, actual.Kind); Assert.Equal(0, actual.DurationMinutes);
        // A valid historical snapshot can explicitly lack played-day metadata even though new filled record rows require it.
        var source = f.Shell.CurrentSession.Workspace;
        var unknown = source with { Results = source.Results.ToDictionary(p => p.Key, p => p.Value with { ActualPlayedDay = null }) };
        var store = new BadmintonDraw.Persistence.TournamentWorkspaceStore(); var historicalPath = f.Data.PathFor("historical.szbd");
        store.Create(historicalPath, unknown); var saved = store.Read(historicalPath); var history = new WorkspaceOperationsHistory(saved);
        var text = Assert.Single(history.Results); Assert.Contains("Walkover", text); Assert.Contains("因伤未开赛", text);
        Assert.Contains("实际用时：0", text); Assert.Contains("实际比赛日：未知", text);
        Assert.Contains("当前实际比赛日：未知", Assert.Single(history.Coverage));
        Assert.Equal(actual.RecordedAt, Assert.Single(saved.Results).Value.RecordedAt);
    }
}
