using System.Text.Json;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;
using static BadmintonDraw.Tests.WorkspaceResultImportFacadeFixture;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceOnsiteResultTests
{
    [Fact]
    public void CompletingRowsPersistsAndResolvesTheNextRoundWithoutChangingPlacements()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 4);
        var original = f.Session.Workspace;
        var nodes = original.Projects.Single().MatchGraph!.Matches.OrderBy(n => n.Order).ToArray();
        var existingFiles = Directory.GetFiles(f.DirectoryPath, "*.xlsx").Order().ToArray();
        var notices = 0;
        f.Workflow.SessionChanged += (_, _) => notices++;

        foreach (var node in nodes)
        {
            var result = f.Workflow.RecordOnsiteResult(Request(f, node), f.Revision);
            Assert.Equal(1, result.Evaluation.ProposedCounts.AddedResultCount);
            Assert.False(result.NoChanges);
            Assert.NotNull(result.Command.BackupPath);
            Assert.Equal(node.Id, Assert.Single(result.Evaluation.Candidate!.ImportLogs.Last().Rows).Key.MatchId);
        }

        var durable = new TournamentWorkspaceStore().Read(f.Session.WorkspacePath);
        Assert.Equal(nodes.Length, durable.Results.Count);
        Assert.Equal(TournamentStage.Completed, durable.Stage);
        Assert.Equal(nodes.Length, f.Store.Mutations);
        Assert.Equal(nodes.Length, notices);
        Assert.All(durable.ImportLogs, log =>
        {
            Assert.Equal("现场记录表", log.SourceFileName);
            Assert.StartsWith("onsite://", log.SourcePath);
            Assert.Equal(64, log.ContentHash.Length);
        });
        Assert.Equal(JsonSerializer.Serialize(original.Schedule), JsonSerializer.Serialize(durable.Schedule));
        Assert.Equal(existingFiles, Directory.GetFiles(f.DirectoryPath, "*.xlsx").Order().ToArray());
        Assert.Equal(TournamentStage.ScheduleReady, original.Stage);
    }

    [Theory]
    [InlineData("21-21", "20", "A")]
    [InlineData("21-19", "0", "A")]
    [InlineData("21-19", "2.5", "A")]
    [InlineData("21-19", "20", "B")]
    [InlineData("21-19", "20", "")]
    public void IncompleteOrContradictoryRowsCannotSave(string score, string duration, string winner)
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2);
        var request = Request(f) with { Score = score, Duration = duration, Winner = winner };
        var hash = Hash(f.Session.WorkspacePath);
        Assert.Throws<WorkspaceCommandException>(() => f.Workflow.RecordOnsiteResult(request, f.Revision));
        Assert.Equal(0, f.Store.Mutations);
        Assert.Equal(hash, Hash(f.Session.WorkspacePath));
        Assert.Empty(f.Session.Workspace.Results);
    }

    [Theory]
    [InlineData("", "20")]
    [InlineData("21-19", "")]
    [InlineData("", "")]
    public void OptionalMetadataCanSaveAndRoundTrip(string score, string duration)
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2);
        f.Workflow.RecordOnsiteResult(Request(f) with { Score = score, Duration = duration }, f.Revision);
        var result = new TournamentWorkspaceStore().Read(f.Session.WorkspacePath).Results.Single().Value;
        Assert.Equal(score, result.Score);
        Assert.Equal(duration == "" ? (int?)null : 20, result.DurationMinutes);
    }

    [Fact]
    public void MissingUpstreamAndStaleRowIdentityCannotSave()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 4);
        var request = Request(f);
        var last = f.Session.Workspace.Projects.Single().MatchGraph!.Matches.OrderBy(n => n.Order).Last();
        var invalid = new[]
        {
            request with { Key = new(request.Key.ProjectId, last.Id) },
            request with { GraphRevision = "old graph" },
            request with { SideAIdentityKey = "another player" },
            request with { SideBIdentityKey = "another opponent" },
            request with { Key = new(Guid.NewGuid(), request.Key.MatchId) },
            request with { ActualPlayedDay = new(2001, 1, 1) },
            request with { Kind = (TournamentResultKind)100 }
        };
        foreach (var row in invalid)
            Assert.Throws<WorkspaceCommandException>(() => f.Workflow.RecordOnsiteResult(row, f.Revision));
        Assert.Equal(0, f.Store.Mutations);
        Assert.Empty(f.Session.Workspace.Results);
    }

    [Fact]
    public void ExplicitWalkoverAcceptsZeroMinutesAfterApprovedPostponement()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2);
        var request = Request(f) with { Kind = TournamentResultKind.Walkover, Duration = "0", Score = "对方未到场", ActualPlayedDay = new(2026, 9, 14) };
        var blocked = Assert.Throws<WorkspaceCommandException>(() => f.Workflow.RecordOnsiteResult(request, f.Revision));
        Assert.Equal("onsite.day-locked", blocked.Error.Code);
        Assert.Equal(0, f.Store.Mutations);
        var preview = f.Workflow.PreviewDayConfirmation(new(new(2026, 9, 13),
            [new(request.Key, "天气原因，经裁判长同意", true)]), f.Revision);
        Assert.True(preview.CanConfirm, string.Join(";", preview.Errors));
        f.Workflow.ConfirmDayResults(preview, f.Revision);
        f.Workflow.RecordOnsiteResult(request, f.Revision);
        var result = Assert.Single(f.Session.Workspace.Results).Value;
        Assert.Equal(TournamentResultKind.Walkover, result.Kind);
        Assert.Equal(0, result.DurationMinutes);
        Assert.Equal("对方未到场", result.Score);
        Assert.Equal(request.ActualPlayedDay, result.ActualPlayedDay);
        Assert.Equal("2026-09-14", f.Session.Workspace.Schedule!.Placements[request.Key.MatchId].DayLabel);
    }

    [Fact]
    public void EquivalentRepeatedSubmissionIsATrueNoOp()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2);
        var request = Request(f);
        f.Workflow.RecordOnsiteResult(request, f.Revision);
        var session = f.Session;
        var hash = Hash(session.WorkspacePath);
        var files = Directory.GetFiles(f.DirectoryPath).Order().ToArray();
        f.Store.Mutations = 0;
        var notices = 0;
        f.Workflow.SessionChanged += (_, _) => notices++;

        var result = f.Workflow.RecordOnsiteResult(request with { Score = "21 — 19", Duration = "20分钟" }, f.Revision);

        Assert.True(result.NoChanges);
        Assert.Null(result.Command.BackupPath);
        Assert.Same(session, f.Session);
        Assert.Equal(0, f.Store.Mutations);
        Assert.Equal(0, notices);
        Assert.Equal(hash, Hash(session.WorkspacePath));
        Assert.Equal(files, Directory.GetFiles(f.DirectoryPath).Order().ToArray());
    }

    [Fact]
    public void CorrectionsNeedReasonAndCanExplicitlyReturnToAnEarlierValue()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2);
        var original = Request(f);
        f.Workflow.RecordOnsiteResult(original, f.Revision);
        var correction = original with { Duration = "25" };
        Assert.Throws<WorkspaceCommandException>(() => f.Workflow.RecordOnsiteResult(correction, f.Revision));
        Assert.Throws<WorkspaceCommandException>(() => f.Workflow.RecordOnsiteResult(correction with { AllowCorrections = true, CorrectionReason = " " }, f.Revision));
        f.Workflow.RecordOnsiteResult(correction with { AllowCorrections = true, CorrectionReason = "核对计时记录" }, f.Revision);
        f.Workflow.RecordOnsiteResult(original with { AllowCorrections = true, CorrectionReason = "复核原始记录为20分钟" }, f.Revision);

        Assert.Equal(20, f.Session.Workspace.Results[original.Key].DurationMinutes);
        Assert.Equal(2, f.Session.Workspace.ResultHistory.Count);
        Assert.Equal(3, f.Session.Workspace.ImportLogs.Select(l => l.ContentHash).Distinct().Count());
        Assert.Equal(25, f.Session.Workspace.ResultHistory[1].Before.DurationMinutes);
        Assert.Equal(20, f.Session.Workspace.ResultHistory[1].After.DurationMinutes);
        Assert.Throws<WorkspaceCommandException>(() => f.Workflow.RecordOnsiteResult(original with
        { Winner = "B", Score = "19-21", AllowCorrections = true, CorrectionReason = "变更胜方" }, f.Revision));
    }

    [Fact]
    public void OldRevisionCannotOverwriteNewerSavedResults()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 4);
        var request = Request(f);
        var revision = f.Revision;
        f.Workflow.RecordOnsiteResult(request, revision);
        var hash = Hash(f.Session.WorkspacePath);
        var error = Assert.Throws<WorkspaceCommandException>(() => f.Workflow.RecordOnsiteResult(request, revision));
        Assert.Equal("RevisionConflict", error.Error.Code);
        Assert.Equal(hash, Hash(f.Session.WorkspacePath));
    }

    [Fact]
    public void PublicationFailureLeavesTheOriginalArchiveAndSessionIntact()
    {
        var files = new OnsiteFaultFiles();
        using var f = new WorkspaceResultImportFacadeFixture(1, 2, new TournamentWorkspaceStore(files));
        var request = Request(f);
        var before = f.Session;
        var hash = Hash(before.WorkspacePath);
        files.Fail = true;
        var error = Assert.Throws<WorkspaceCommandException>(() => f.Workflow.RecordOnsiteResult(request, f.Revision)).Error;
        Assert.False(error.Committed);
        Assert.Same(before, f.Session);
        Assert.Equal(hash, Hash(before.WorkspacePath));
        Assert.Empty(new TournamentWorkspaceStore().Read(before.WorkspacePath).Results);
    }

    private static WorkspaceOnsiteResultRequest Request(WorkspaceResultImportFacadeFixture f, MatchNode? node = null)
    {
        var project = f.Session.Workspace.Projects.Single();
        node ??= project.MatchGraph!.Matches.OrderBy(n => n.Order).First();
        EntrantSource.Participant Resolve(EntrantSource source) => source switch
        {
            EntrantSource.Participant p => p,
            EntrantSource.WinnerOf winner => f.Session.Workspace.Results[new(project.Id, winner.MatchId)].Winner,
            EntrantSource.LoserOf loser => f.Session.Workspace.Results[new(project.Id, loser.MatchId)].Loser,
            _ => throw new InvalidOperationException()
        };
        return new(new(project.Id, node.Id), project.MatchGraph!.Revision, Resolve(node.SideA).IdentityKey,
            Resolve(node.SideB).IdentityKey, "A", "21-19", "20", TournamentResultKind.Played, new(2026, 9, 13));
    }

    private sealed class OnsiteFaultFiles : WorkspaceFileOperations
    {
        internal bool Fail { get; set; }
        public override void Publish(string candidate, string destination, bool overwrite)
        {
            if (Fail) throw new IOException("onsite publish failure");
            base.Publish(candidate, destination, overwrite);
        }
    }
}
