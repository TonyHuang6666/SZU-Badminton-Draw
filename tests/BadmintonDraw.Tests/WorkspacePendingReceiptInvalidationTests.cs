using System.Security.Cryptography;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspacePendingReceiptInvalidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulReopenOrReplanVoidsPendingCoverageWithSameAuditAndKeepsSourceEvidence(bool replan)
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        var original = store.Create(temp.Path, Fixture());
        var workflow = new TournamentWorkspaceWorkflow(store); workflow.OpenWorkspace(temp.Path);
        var hash = Hash(temp.Path);
        var result = replan
            ? workflow.GenerateSchedule(NewResources(original), original.Schedule!.Policy, 0)
            : workflow.ReopenDraw(original.Projects[0].Id, "名单核对后重抽", 0);
        var saved = store.Read(temp.Path);
        Assert.Equal(1, saved.Revision);
        Assert.Equal(hash, Hash(result.BackupPath!));
        Assert.Empty(saved.ProcessedDays);
        Assert.Empty(saved.Results);
        Assert.Empty(saved.ResultHistory);
        Assert.Equal(3, saved.ImportLogs.Count);
        var audit = Assert.Single(saved.AuditEvents.Skip(original.AuditEvents.Count));
        Assert.Equal(replan ? "ScheduleGenerated" : "DrawReopened", audit.Action);
        foreach (var source in original.ImportLogs.Where(log => log.VoidedAt is null))
        {
            var receipt = Assert.Single(saved.ImportLogs, log => log.Id == source.Id);
            Assert.Equal(source.ContentHash, receipt.ContentHash);
            Assert.Equal(source.Rows, receipt.Rows);
            Assert.Equal(source.SourcePath, receipt.SourcePath);
            Assert.Equal(audit.Id, receipt.VoidedByAuditEventId);
            Assert.Equal(audit.OccurredAt, receipt.VoidedAt);
            Assert.False(string.IsNullOrWhiteSpace(receipt.VoidReason));
        }
        var previous = Assert.Single(original.ImportLogs, log => log.VoidedAt is not null);
        var retained = Assert.Single(saved.ImportLogs, log => log.Id == previous.Id);
        Assert.Equal(previous.VoidedAt, retained.VoidedAt);
        Assert.Equal(previous.VoidedByAuditEventId, retained.VoidedByAuditEventId);
        Assert.Equal(previous.VoidReason, retained.VoidReason);
        if (replan) Assert.Equal(new DateOnly(2026, 9, 16), saved.Resources!.Days[0].Date);
        else
        {
            Assert.Null(saved.Schedule); Assert.Null(saved.Resources); Assert.Null(saved.Projects[0].MatchGraph);
            Assert.Equal(original.Projects[1].MatchGraph!.Revision, saved.Projects[1].MatchGraph!.Revision);
            Assert.Equal(original.Projects[1].Draw!.ConfirmedAt, saved.Projects[1].Draw!.ConfirmedAt);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPublicationCannotVoidReceiptOrClearCoverage(bool replan)
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        var original = store.Create(temp.Path, Fixture()); var hash = Hash(temp.Path);
        var files = new RejectPublish();
        var workflow = new TournamentWorkspaceWorkflow(new TournamentWorkspaceStore(files));
        workflow.OpenWorkspace(temp.Path); var session = workflow.CurrentSession;
        Assert.Throws<WorkspaceCommandException>(() =>
        {
            if (replan) workflow.GenerateSchedule(NewResources(original), original.Schedule!.Policy, 0);
            else workflow.ReopenDraw(original.Projects[0].Id, "核对名单", 0);
        });
        Assert.Equal(hash, Hash(temp.Path));
        Assert.True(files.Attempted);
        Assert.Same(session, workflow.CurrentSession);
        Assert.Null(Pending(store.Read(temp.Path)).VoidedAt);
        Assert.Single(store.Read(temp.Path).ProcessedDays);
    }

    [Fact]
    public void ImpossibleReplanDoesNotCreateAnyCandidateOrInvalidatePendingReceipts()
    {
        using var temp = new WorkspaceStoreTemp(); var store = new TournamentWorkspaceStore();
        var original = store.Create(temp.Path, Fixture()); var hash = Hash(temp.Path);
        var workflow = new TournamentWorkspaceWorkflow(store); workflow.OpenWorkspace(temp.Path);
        var files = Directory.GetFiles(temp.DirectoryPath).Order().ToArray();
        var invalid = original.Resources! with { Days = [new(new(2026, 9, 16), new(9, 0), new(9, 1), ["1"])] };
        Assert.Equal("schedule.generation-failed", Assert.Throws<WorkspaceCommandException>(() => workflow.GenerateSchedule(invalid, original.Schedule!.Policy, 0)).Error.Code);
        Assert.Equal(files, Directory.GetFiles(temp.DirectoryPath).Order().ToArray());
        Assert.Equal(hash, Hash(temp.Path));
        Assert.Null(Pending(store.Read(temp.Path)).VoidedAt);
    }

    [Fact]
    public void DirectCoreReopenCanInvalidatePendingReceiptsBeforeItsCandidateValidation()
    {
        var original = Fixture();
        var next = TournamentWorkspaceRules.ReopenDraw(original, original.Projects[0].Id, "名单核对");
        Assert.Empty(next.ProcessedDays);
        Assert.All(next.ImportLogs, log => Assert.NotNull(log.VoidedAt));
        Assert.Null(original.ImportLogs[0].VoidedAt);
        Assert.Single(original.ProcessedDays);
    }

    private static TournamentResourcePlan NewResources(TournamentWorkspace workspace) => workspace.Resources! with
        { Days = [new(new(2026, 9, 16), new(9, 0), new(18, 0), ["1"])] };
    private static TournamentWorkspace Fixture()
    {
        var workspace = TournamentWorkspaceRulesTests.Fixture(TournamentStage.ScheduleReady) with
            { Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>() };
        var node = workspace.Projects[0].MatchGraph!.Matches[0];
        var secondId = Guid.NewGuid();
        var secondNode = node with { Id = Guid.NewGuid(), ProjectId = secondId };
        var secondProject = workspace.Projects[0] with { Id = secondId, Discipline = EventDiscipline.WomenSingles,
            DisplayName = "女单", SortOrder = 1, MatchGraph = workspace.Projects[0].MatchGraph! with
                { ProjectId = secondId, Matches = [secondNode] } };
        var placements = workspace.Schedule!.Placements.ToDictionary();
        placements.Add(secondNode.Id, new(secondNode.Id, "2026-09-13", new(10, 0), new(10, 30), "1"));
        var revisions = workspace.Schedule.GraphRevisions.ToDictionary(); revisions.Add(secondId, secondProject.MatchGraph!.Revision);
        workspace = workspace with { Projects = [workspace.Projects[0], secondProject],
            Schedule = workspace.Schedule with { Placements = placements, GraphRevisions = revisions } };
        var time = DateTimeOffset.UtcNow.AddHours(-2);
        var row = new WorkspaceImportedRow(new(node.ProjectId, node.Id), new(2026, 9, 13), new("对阵记录表", 6), false);
        var log = new WorkspaceImportLog(Guid.NewGuid(), time, "待填.xlsx", "/synthetic/待填.xlsx", new string('a', 64), [row], 0, 0, []);
        var secondRow = row with { Key = new(secondId, secondNode.Id) };
        var second = log with { Id = Guid.NewGuid(), ContentHash = new string('b', 64), SourcePath = "/synthetic/第二表.xlsx", Rows = [secondRow] };
        var previous = new WorkspaceAuditEvent(Guid.NewGuid(), "ScheduleGenerated", time.AddMinutes(1), Detail: "上次重排");
        var old = log with { Id = Guid.NewGuid(), ContentHash = new string('c', 64), VoidedAt = previous.OccurredAt,
            VoidReason = "上次重排", VoidedByAuditEventId = previous.Id };
        return workspace with { ImportLogs = [log, second, old], ProcessedDays = [new(new(2026, 9, 13), [row.Key, secondRow.Key], time)], AuditEvents = [previous] };
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static WorkspaceImportLog Pending(TournamentWorkspace workspace) =>
        workspace.ImportLogs.Single(log => log.ContentHash == new string('a', 64));
    private sealed class RejectPublish : WorkspaceFileOperations
    {
        public bool Attempted { get; private set; }
        public override void Publish(string candidate, string destination, bool overwrite)
        { Attempted = true; throw new IOException("publish failed"); }
    }
}
