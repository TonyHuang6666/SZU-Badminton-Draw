using System.Security.Cryptography;
using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class ScheduleEditingWorkflowTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("schedule-edit-").FullName;
    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectMovePublishesExactlyOnceAndUndoRestoresPlacementsOnly(bool crossDay)
    {
        var (workflow, store) = Create();
        var before = workflow.CurrentSession!.Workspace;
        var request = Move(workflow, First(before), crossDay ? "2026-09-16" : "2026-09-13", new(11, 0), "B");
        var preview = workflow.PreviewMove(request, before.Revision);
        Assert.True(preview.CanApply);
        Assert.Single(preview.Changes);
        Assert.Equal(0, store.Mutations);
        var notices = new List<bool>();
        workflow.SessionChanged += (_, _) => notices.Add(workflow.CanUndoScheduleEdit);
        var moved = workflow.MoveMatch(request, before.Revision);
        Assert.Equal(1, store.Mutations);
        Assert.Equal(before.Revision + 1, moved.Workspace.Revision);
        Assert.Equal(before.Schedule!.Revision + 1, moved.Workspace.Schedule!.Revision);
        Assert.Equal(new TimeOnly(11, 0), moved.Workspace.Schedule.Placements[request.Key.MatchId].StartTime);
        Assert.Equal(preview.Changes[0].After, moved.Workspace.Schedule.Placements[request.Key.MatchId]);
        Assert.Single(moved.Workspace.AuditEvents, a => a.Action == "ScheduleMatchMoved");
        Assert.True(File.Exists(moved.BackupPath));
        var undone = workflow.UndoLastScheduleEdit(moved.Workspace.Revision);
        Assert.Equal(2, store.Mutations);
        Assert.Equal(Json(before.Schedule.Placements), Json(undone.Workspace.Schedule!.Placements));
        Assert.Equal(before.Revision + 2, undone.Workspace.Revision);
        Assert.Equal(before.Schedule.Revision + 2, undone.Workspace.Schedule.Revision);
        Assert.Contains(undone.Workspace.AuditEvents, a => a.Action == "ScheduleMatchMoved");
        Assert.Contains(undone.Workspace.AuditEvents, a => a.Action == "ScheduleEditUndone");
        Assert.Equal(new[] { true, false }, notices);
        Assert.Equal(Json(undone.Workspace), Json(new TournamentWorkspaceStore().Read(undone.WorkspacePath)));
    }

    [Fact]
    public void OccupiedTargetIsBlockedWithWholeFileAndUndoUnchanged()
    {
        var (workflow, store) = Create(projects: 2);
        var before = workflow.CurrentSession!.Workspace;
        var root = First(before);
        var occupied = before.Schedule!.Placements.Values.Single(p => p.MatchId != root.Id);
        var request = Move(workflow, root, occupied.DayLabel, occupied.StartTime, occupied.Court);
        var preview = workflow.PreviewMove(request, before.Revision);
        Assert.False(preview.CanApply);
        Assert.Contains(preview.Violations, v => v.Code == SchedulingConstraintCode.CourtOverlap);
        RejectUnchanged(workflow, store, () => workflow.MoveMatch(request, before.Revision), "schedule.move-blocked");
        Assert.False(workflow.CanUndoScheduleEdit);
    }

    [Fact]
    public void CascadePreviewsExactRootAndDescendantMovesThenCommitsSameMap()
    {
        var (workflow, store) = Create(entrants: 4);
        var before = workflow.CurrentSession!.Workspace;
        var root = First(before);
        var request = Move(workflow, root, "2026-09-16", new(10, 0), "A");
        Assert.False(workflow.PreviewMove(request, before.Revision).CanApply);
        var preview = workflow.PreviewCascade(request, before.Revision);
        Assert.True(preview.CanApply);
        Assert.True(preview.IsCascade);
        Assert.Equal(2, preview.Changes.Count);
        var final = before.Projects[0].MatchGraph!.Matches.Single(n => n.Dependencies.Count == 2);
        var finalChange = Assert.Single(preview.Changes, c => c.Key.MatchId == final.Id);
        Assert.Equal("2026-09-16", finalChange.After.DayLabel);
        Assert.Equal(new TimeOnly(10, 45), finalChange.After.StartTime);
        Assert.Equal(1, finalChange.Depth);
        Assert.Equal(0, store.Mutations);
        var moved = workflow.CascadeMove(preview, before.Revision);
        Assert.Equal(1, store.Mutations);
        Assert.All(preview.Changes, c => Assert.Equal(c.After, moved.Workspace.Schedule!.Placements[c.Key.MatchId]));
        Assert.Single(moved.Workspace.AuditEvents, a => a.Action == "ScheduleCascadeMoved");
        AssertValid(moved.Workspace);
    }

    [Fact]
    public void FailedCascadeNeverMovesOtherProjectOrPublishesPartialMap()
    {
        var (workflow, store) = Create(entrants: 4, projects: 2);
        var before = workflow.CurrentSession!.Workspace;
        var request = Move(workflow, First(before), "2026-09-16", new(14, 30), "A");
        var preview = workflow.PreviewCascade(request, before.Revision);
        Assert.False(preview.CanApply);
        Assert.Contains(preview.Violations, v => v.Code == SchedulingConstraintCode.SearchExhausted);
        RejectUnchanged(workflow, store, () => workflow.CascadeMove(preview, before.Revision), "schedule.cascade-blocked");
    }

    [Fact]
    public void NoOpMoveAndCascadeDoNotCreateRevisionBackupAuditOrUndo()
    {
        var (workflow, store) = Create();
        var before = workflow.CurrentSession!;
        var root = First(before.Workspace);
        var old = before.Workspace.Schedule!.Placements[root.Id];
        var request = Move(workflow, root, old.DayLabel, old.StartTime, old.Court);
        var hash = Hash(before.WorkspacePath);
        var preview = workflow.PreviewCascade(request, before.Workspace.Revision);
        Assert.True(preview.CanApply);
        Assert.False(preview.HasChanges);
        workflow.CascadeMove(preview, before.Workspace.Revision);
        workflow.MoveMatch(request, before.Workspace.Revision);
        Assert.Same(before, workflow.CurrentSession);
        Assert.Equal(hash, Hash(before.WorkspacePath));
        Assert.Equal(0, store.Mutations);
        Assert.False(workflow.CanUndoScheduleEdit);
    }

    [Fact]
    public void BaselineAndPreviewCannotCrossExplicitReopenEvenForIdenticalArchive()
    {
        var (workflow, store) = Create();
        var before = workflow.CurrentSession!;
        var request = Move(workflow, First(before.Workspace), "2026-09-13", new(11, 0), "A");
        var preview = workflow.PreviewCascade(request, before.Workspace.Revision);
        workflow.OpenWorkspace(before.WorkspacePath);
        RejectUnchanged(workflow, store, () => workflow.MoveMatch(request, before.Workspace.Revision), "schedule.edit-session-changed");
        RejectUnchanged(workflow, store, () => workflow.CascadeMove(preview, before.Workspace.Revision), "schedule.edit-session-changed");
    }

    [Fact]
    public void NullOrForeignBaselineCannotBypassTransientEditorIdentity()
    {
        var (workflow, store) = Create();
        var (other, _) = Create(file: "other.szbd");
        var before = workflow.CurrentSession!.Workspace;
        var request = Move(workflow, First(before), "2026-09-13", new(11, 0), "A");
        RejectUnchanged(workflow, store, () => workflow.MoveMatch(request with { Baseline = null! }, before.Revision), "schedule.edit-baseline-required");
        RejectUnchanged(workflow, store, () => workflow.PreviewMove(request with { Baseline = other.CaptureScheduleEditBaseline() }, before.Revision), "schedule.edit-session-changed");
    }

    [Fact]
    public void TwoUndosKeepNewAuditAndUnrelatedCompletedResultInsteadOfRestoringOldAggregate()
    {
        var (workflow, store) = Create(projects: 2);
        var before = workflow.CurrentSession!.Workspace;
        var root = First(before);
        workflow.MoveMatch(Move(workflow, root, "2026-09-13", new(11, 0), "B"), before.Revision);
        workflow.MoveMatch(Move(workflow, root, "2026-09-16", new(12, 0), "C"), workflow.CurrentSession!.Workspace.Revision);
        var completed = before.Projects[1].MatchGraph!.Matches.Single();
        AddResultAndRefresh(workflow, completed);
        var withResult = workflow.CurrentSession!.Workspace;
        Assert.True(workflow.CanUndoScheduleEdit);
        var first = workflow.UndoLastScheduleEdit(withResult.Revision).Workspace;
        Assert.Equal(new TimeOnly(11, 0), first.Schedule!.Placements[root.Id].StartTime);
        Assert.Equal("2026-09-13", first.Schedule.Placements[root.Id].DayLabel);
        Assert.True(workflow.CanUndoScheduleEdit);
        var second = workflow.UndoLastScheduleEdit(first.Revision).Workspace;
        Assert.Equal(Json(before.Schedule!.Placements), Json(second.Schedule!.Placements));
        Assert.Equal(Json(withResult.Results.Values), Json(second.Results.Values));
        Assert.Equal(withResult.Stage, second.Stage);
        Assert.All(withResult.AuditEvents, a => Assert.Contains(a, second.AuditEvents));
        Assert.Equal(2, second.AuditEvents.Count(a => a.Action == "ScheduleEditUndone"));
        Assert.False(workflow.CanUndoScheduleEdit);
        AssertValid(second);
    }

    [Fact]
    public void NewResultLocksMovedMatchAndUndoFailsWithoutPoppingHistory()
    {
        var (workflow, store) = Create();
        var root = First(workflow.CurrentSession!.Workspace);
        workflow.MoveMatch(Move(workflow, root, "2026-09-13", new(11, 0), "B"), workflow.CurrentSession.Workspace.Revision);
        AddResultAndRefresh(workflow, root);
        var revision = workflow.CurrentSession!.Workspace.Revision;
        var error = RejectUnchanged(workflow, store, () => workflow.UndoLastScheduleEdit(revision), "schedule.undo-blocked");
        Assert.Contains(error.SchedulingFailure!.Violations, v => v.Code == SchedulingConstraintCode.LockedPlacement);
        Assert.True(workflow.CanUndoScheduleEdit);
        var request = Move(workflow, root, "2026-09-13", new(12, 0), "C");
        Assert.Contains(workflow.PreviewMove(request, revision).Violations, v => v.Code == SchedulingConstraintCode.LockedPlacement);
        Assert.Contains(workflow.PreviewCascade(request, revision).Violations, v => v.Code == SchedulingConstraintCode.LockedPlacement);
    }

    [Fact]
    public void AuditOnlyRefreshPreservesManualFieldsButNotAlreadyConfirmedCascadePreview()
    {
        var (workflow, store) = Create();
        var before = workflow.CurrentSession!.Workspace;
        var request = Move(workflow, First(before), "2026-09-13", new(11, 0), "B");
        var preview = workflow.PreviewCascade(request, before.Revision);
        ExportTemplate(workflow, before.Revision);
        var revision = workflow.CurrentSession!.Workspace.Revision;
        RejectUnchanged(workflow, store, () => workflow.CascadeMove(preview, revision), "schedule.preview-stale");
        Assert.True(workflow.PreviewMove(request, revision).CanApply);
        workflow.MoveMatch(request, revision);
        Assert.True(workflow.CanUndoScheduleEdit);
        ExportTemplate(workflow, workflow.CurrentSession!.Workspace.Revision);
        Assert.True(workflow.CanUndoScheduleEdit);
        workflow.UndoLastScheduleEdit(workflow.CurrentSession!.Workspace.Revision);
    }

    [Fact]
    public void PriorManualBaselineCannotAdoptRevisionAfterAnotherEditOrRegeneration()
    {
        var (workflow, store) = Create();
        var before = workflow.CurrentSession!.Workspace;
        var old = Move(workflow, First(before), "2026-09-13", new(12, 0), "C");
        workflow.MoveMatch(old with { StartTime = new(11, 0) }, before.Revision);
        RejectUnchanged(workflow, store, () => workflow.MoveMatch(old, workflow.CurrentSession!.Workspace.Revision), "schedule.edit-stale");
        var baseline = Move(workflow, First(before), "2026-09-13", new(13, 0), "A");
        workflow.GenerateSchedule(Resources(), new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), workflow.CurrentSession!.Workspace.Revision);
        Assert.False(workflow.CanUndoScheduleEdit);
        RejectUnchanged(workflow, store, () => workflow.PreviewMove(baseline, workflow.CurrentSession!.Workspace.Revision), "schedule.edit-stale");
    }

    [Fact]
    public void ReopenClearsUndoAndMissingScheduleCannotProduceAnEditingBaseline()
    {
        Assert.Equal("workspace.not-open", Assert.Throws<WorkspaceCommandException>(() => new TournamentWorkspaceWorkflow().CaptureScheduleEditBaseline()).Error.Code);
        var (workflow, store) = Create();
        var before = workflow.CurrentSession!.Workspace;
        workflow.MoveMatch(Move(workflow, First(before), "2026-09-13", new(11, 0), "A"), before.Revision);
        workflow.ReopenDraw(before.Projects[0].Id, "重新公开抽签", workflow.CurrentSession!.Workspace.Revision);
        Assert.False(workflow.CanUndoScheduleEdit);
        RejectUnchanged(workflow, store, () => workflow.CaptureScheduleEditBaseline(), "schedule.not-ready");
    }

    [Theory]
    [InlineData("unavailable", SchedulingConstraintCode.CourtUnavailable)]
    [InlineData("referee", SchedulingConstraintCode.RefereeCapacity)]
    [InlineData("shared-player", SchedulingConstraintCode.PlayerOverlap)]
    [InlineData("rest", SchedulingConstraintCode.MinimumRest)]
    [InlineData("daily", SchedulingConstraintCode.DailyMatchLimit)]
    public void OneFullValidatorEnforcesGlobalConstraintsDuringMoveAndCascade(string scenario, SchedulingConstraintCode expected)
    {
        var resources = Resources();
        if (scenario == "unavailable") resources = resources with { Days = resources.Days.Select(d => d with
            { UnavailableCourtWindows = [new(new(12, 0), new(13, 0), ["B"])] }).ToArray() };
        if (scenario == "referee") resources = resources with { RefereeCount = 1 };
        if (scenario == "daily") resources = resources with { MaxPlayerMatchesPerDay = 1 };
        var (workflow, store) = Create(projects: 2, resources: resources, sharedPlayers: scenario is "shared-player" or "rest" or "daily");
        var before = workflow.CurrentSession!.Workspace;
        var root = First(before);
        var other = before.Schedule!.Placements.Values.Single(p => p.MatchId != root.Id);
        var time = scenario switch { "unavailable" => new TimeOnly(12, 0), "rest" => other.EndTime.Add(TimeSpan.FromSeconds(899)), _ => other.StartTime };
        var request = Move(workflow, root, other.DayLabel, time, "B");
        if (scenario == "daily") request = request with { StartTime = other.EndTime.AddMinutes(60) };
        Assert.Contains(workflow.PreviewMove(request, before.Revision).Violations, v => v.Code == expected);
        var cascade = workflow.PreviewCascade(request, before.Revision);
        Assert.Contains(cascade.Violations, v => v.Code == expected);
        RejectUnchanged(workflow, store, () => workflow.CascadeMove(cascade, before.Revision), "schedule.cascade-blocked");
        RejectUnchanged(workflow, store, () => workflow.MoveMatch(request, before.Revision), "schedule.move-blocked");
    }

    [Fact]
    public void CascadeKeepsOtherProjectsAndUsesExactSecondRestBoundary()
    {
        var (workflow, _) = Create(entrants: 4, projects: 2);
        var before = workflow.CurrentSession!.Workspace;
        var preview = workflow.PreviewCascade(Move(workflow, First(before), "2026-09-16", new(10, 0, 30), "B"), before.Revision);
        Assert.True(preview.CanApply);
        Assert.All(preview.Changes, c => Assert.Equal(before.Projects[0].Id, c.Key.ProjectId));
        Assert.Equal(new TimeOnly(10, 45, 30), preview.Changes.Single(c => c.Depth > 0).After.StartTime);
        var saved = workflow.CascadeMove(preview, before.Revision).Workspace;
        foreach (var node in before.Projects[1].MatchGraph!.Matches)
            Assert.Equal(before.Schedule!.Placements[node.Id], saved.Schedule!.Placements[node.Id]);
    }

    [Fact]
    public void PreviewCollectionsCannotBeChangedAfterConfirmation()
    {
        var (workflow, _) = Create();
        var before = workflow.CurrentSession!.Workspace;
        var preview = workflow.PreviewCascade(Move(workflow, First(before), "2026-09-13", new(11, 0), "A"), before.Revision);
        var changes = Assert.IsAssignableFrom<IList<ScheduleEditChange>>(preview.Changes);
        Assert.Throws<NotSupportedException>(() => changes.Clear());
        var violations = Assert.IsAssignableFrom<IList<SchedulingViolation>>(preview.Violations);
        Assert.Throws<NotSupportedException>(() => violations.Add(new(SchedulingConstraintCode.DayBounds, null, null, null, "伪造")));
        Assert.Equal(new TimeOnly(11, 0), workflow.CascadeMove(preview, before.Revision).Workspace.Schedule!.Placements[preview.Root.Key.MatchId].StartTime);
    }

    [Fact]
    public void FilePublicationAndSqlTransactionFailurePreserveLastUndoAndLegalMap()
    {
        var files = new ControlledFiles();
        var (workflow, store) = Create(store: new(new TournamentWorkspaceStore(files)));
        var root = First(workflow.CurrentSession!.Workspace);
        workflow.MoveMatch(Move(workflow, root, "2026-09-13", new(11, 0), "A"), workflow.CurrentSession.Workspace.Revision);
        var request = Move(workflow, root, "2026-09-13", new(12, 0), "A");
        files.FailPublish = true;
        var failure = RejectUnchanged(workflow, store, () => workflow.MoveMatch(request, workflow.CurrentSession!.Workspace.Revision), "WorkspaceWriteFailed", 1);
        Assert.True(File.Exists(failure.CandidatePath));
        Assert.True(File.Exists(failure.BackupPath));
        Assert.True(workflow.CanUndoScheduleEdit);
        files.FailPublish = false;
        using (var connection = new SqliteConnection("Data Source=" + workflow.CurrentSession!.WorkspacePath + ";Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_edit BEFORE DELETE ON schedule BEGIN SELECT RAISE(ABORT, 'edit fault'); END;";
            command.ExecuteNonQuery();
        }
        RejectUnchanged(workflow, store, () => workflow.MoveMatch(request, workflow.CurrentSession!.Workspace.Revision), "WorkspaceWriteFailed", 1);
        Assert.True(workflow.CanUndoScheduleEdit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedReadFailureClearsUndoBeforeRecoveryNotificationAndNeverDoublePublishes(bool persistent)
    {
        var files = new ControlledFiles(); var inner = new ReadFaultStore(files);
        var (workflow, store) = Create(store: new(inner));
        var root = First(workflow.CurrentSession!.Workspace);
        workflow.MoveMatch(Move(workflow, root, "2026-09-13", new(11, 0), "A"), workflow.CurrentSession.Workspace.Revision);
        var before = workflow.CurrentSession!;
        var request = Move(workflow, root, "2026-09-13", new(12, 0), "B");
        var notifications = new List<bool>();
        workflow.SessionChanged += (_, _) => notifications.Add(workflow.CanUndoScheduleEdit);
        files.AfterPublish = () => { inner.FailPath = before.WorkspacePath; inner.FailuresRemaining = persistent ? 10 : 1; };
        store.Mutations = 0;
        var error = Assert.Throws<WorkspaceCommandException>(() => workflow.MoveMatch(request, before.Workspace.Revision)).Error;
        Assert.Equal("CommittedReadFailed", error.Code); Assert.True(error.Committed);
        Assert.Equal(new[] { false }, notifications);
        Assert.False(workflow.CanUndoScheduleEdit);
        Assert.Equal(persistent, workflow.CurrentSession!.RequiresReload);
        var saved = new TournamentWorkspaceStore().Read(before.WorkspacePath);
        Assert.Equal(new TimeOnly(12, 0), saved.Schedule!.Placements[root.Id].StartTime);
        Assert.Equal(before.Workspace.Revision + 1, saved.Revision);
        Assert.Equal(1, store.Mutations);
        RejectUnchanged(workflow, store, () => workflow.MoveMatch(request, before.Workspace.Revision), persistent ? "workspace.reload-required" : "RevisionConflict");
    }

    [Theory]
    [InlineData(false, "revision", "RevisionConflict")]
    [InlineData(false, "identity", "workspace.session-changed")]
    [InlineData(false, "graph", "schedule.source-changed")]
    [InlineData(true, "revision", "RevisionConflict")]
    [InlineData(true, "identity", "workspace.session-changed")]
    [InlineData(true, "graph", "schedule.source-changed")]
    public void DurableReadAndMutationBothRejectExternallyChangedSources(bool atCommit, string kind, string code)
    {
        var (workflow, store) = Create();
        var before = workflow.CurrentSession!;
        var request = Move(workflow, First(before.Workspace), "2026-09-13", new(11, 0), "A");
        var preview = workflow.PreviewCascade(request, before.Workspace.Revision);
        byte[]? externalHash = null;
        void Replace()
        {
            var replacement = kind switch
            {
                "revision" => before.Workspace with { Revision = before.Workspace.Revision + 1 },
                "identity" => before.Workspace with { Id = Guid.NewGuid() },
                _ => before.Workspace with { Projects = before.Workspace.Projects.Select(p => p with
                { MatchGraph = p.MatchGraph! with { Matches = p.MatchGraph.Matches.Select(n => n with { DisplayName = "外部修改但未改图版本" }).ToArray() } }).ToArray() }
            };
            ReplaceArchive(before.WorkspacePath, replacement);
            externalHash = Hash(before.WorkspacePath);
        }
        if (atCommit) store.BeforeMutation = Replace;
        else store.BeforeRead = _ => { store.BeforeRead = null; Replace(); };
        var error = Assert.Throws<WorkspaceCommandException>(() => workflow.CascadeMove(preview, before.Workspace.Revision)).Error;
        Assert.Equal(code, error.Code);
        Assert.False(error.Committed);
        Assert.Same(before, workflow.CurrentSession);
        Assert.Equal(atCommit ? 1 : 0, store.Mutations);
        Assert.Equal(externalHash, Hash(before.WorkspacePath));
        Assert.DoesNotContain(new TournamentWorkspaceStore().Read(before.WorkspacePath).AuditEvents, a => a.Action == "ScheduleCascadeMoved");
    }

    [Fact]
    public void ObservedExternalScheduleChangeInvalidatesUndoBeforeRejectingStaleRevision()
    {
        var (workflow, store) = Create();
        var root = First(workflow.CurrentSession!.Workspace);
        workflow.MoveMatch(Move(workflow, root, "2026-09-13", new(11, 0), "A"), workflow.CurrentSession.Workspace.Revision);
        var before = workflow.CurrentSession!;
        var old = before.Workspace.Schedule!;
        new TournamentWorkspaceStore().Mutate(before.WorkspacePath, before.Workspace.Revision, w => w with
        { Schedule = old with { Revision = old.Revision + 1, Placements = old.Placements.ToDictionary(p => p.Key,
            p => p.Value with { StartTime = new(12, 0), EndTime = new(12, 30) }) } });
        Assert.True(workflow.CanUndoScheduleEdit);
        RejectUnchanged(workflow, store, () => workflow.UndoLastScheduleEdit(before.Workspace.Revision), "RevisionConflict");
        Assert.False(workflow.CanUndoScheduleEdit);
    }

    [Fact]
    public void InvalidExistingFullMapAndWrongCompositeProjectFailTypedWithoutWriting()
    {
        var (workflow, store) = Create();
        var before = workflow.CurrentSession!;
        var root = First(before.Workspace);
        var request = Move(workflow, root, "2026-09-13", new(11, 0), "A") with { Key = new(Guid.NewGuid(), root.Id) };
        RejectUnchanged(workflow, store, () => workflow.PreviewMove(request, before.Workspace.Revision), "schedule.match-not-found");
        var invalid = before.Workspace with { Schedule = before.Workspace.Schedule! with
        { Placements = before.Workspace.Schedule.Placements.ToDictionary(p => p.Key, p => p.Value with { EndTime = p.Value.EndTime.AddMinutes(1) }) } };
        ReplaceArchive(before.WorkspacePath, invalid);
        workflow.OpenWorkspace(before.WorkspacePath);
        var error = RejectUnchanged(workflow, store, () => workflow.CaptureScheduleEditBaseline(), "schedule.invalid");
        Assert.Contains(error.SchedulingFailure!.Violations, v => v.Code == SchedulingConstraintCode.Duration);
    }

    [Fact]
    public void MidnightWrappedMoveAndCascadeCannotPublishShorterFalseDuration()
    {
        var resources = new TournamentResourcePlan([new(new(2026, 9, 13), new(22, 0), new(23, 59, 59), ["A"])], 1, 15, 6);
        var (workflow, store) = Create(resources: resources);
        var before = workflow.CurrentSession!.Workspace;
        var request = Move(workflow, First(before), "2026-09-13", new(23, 45), "A");
        Assert.Contains(workflow.PreviewMove(request, before.Revision).Violations, v => v.Code == SchedulingConstraintCode.DayBounds);
        Assert.Contains(workflow.PreviewCascade(request, before.Revision).Violations, v => v.Code == SchedulingConstraintCode.DayBounds);
        RejectUnchanged(workflow, store, () => workflow.MoveMatch(request, before.Revision), "schedule.move-blocked");
    }

    [Fact]
    public void FailedCustomRegenerationKeepsLastLegalEditAndItsUndo()
    {
        var (workflow, store) = Create(entrants: 4);
        var before = workflow.CurrentSession!.Workspace;
        var final = before.Projects[0].MatchGraph!.Matches.Single(n => n.Dependencies.Count == 2);
        workflow.MoveMatch(Move(workflow, final, "2026-09-13", new(11, 0), "A"), before.Revision);
        Assert.True(workflow.CanUndoScheduleEdit);
        var error = RejectUnchanged(workflow, store, () => workflow.GenerateSchedule(Resources() with
            { Days = [new(new(2026, 9, 13), new(9, 0), new(9, 31), ["A"])] },
            new(ScheduleAutoSchedulingStrategy.Custom, [], true, [], []), workflow.CurrentSession!.Workspace.Revision), "schedule.generation-failed");
        Assert.NotNull(error.SchedulingFailure);
        Assert.True(workflow.CanUndoScheduleEdit);
        var restored = workflow.UndoLastScheduleEdit(workflow.CurrentSession!.Workspace.Revision).Workspace;
        Assert.Equal(Json(before.Schedule!.Placements), Json(restored.Schedule!.Placements));
    }

    [Fact]
    public async Task QueuedPreviewCannotFollowOpenToAnotherArchiveAtSameRevision()
    {
        var (workflow, store) = Create();
        var before = workflow.CurrentSession!;
        var request = Move(workflow, First(before.Workspace), "2026-09-13", new(11, 0), "A");
        var (other, _) = Create(file: "other.szbd");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        store.BeforeRead = path => { if (path == other.CurrentSession!.WorkspacePath) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        var opening = Task.Run(() => workflow.OpenWorkspace(other.CurrentSession!.WorkspacePath));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Exception? error = null;
        var queued = new Thread(() => { try { workflow.PreviewMove(request, before.Workspace.Revision); } catch (Exception e) { error = e; } });
        queued.Start();
        var waiting = SpinWait.SpinUntil(() => queued.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(10));
        release.Set(); Assert.True(waiting); await opening; Assert.True(queued.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal("workspace.session-changed", Assert.IsType<WorkspaceCommandException>(error).Error.Code);
        Assert.Equal(0, store.Mutations);
    }

    [Fact]
    public async Task RunningPreviewKeepsCapturedSessionUntilReadAndValidationFinish()
    {
        var (workflow, store) = Create();
        var before = workflow.CurrentSession!;
        var request = Move(workflow, First(before.Workspace), "2026-09-13", new(11, 0), "A");
        var (other, _) = Create(file: "other.szbd");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        store.BeforeRead = path => { if (path == before.WorkspacePath) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        var previewing = Task.Run(() => workflow.PreviewMove(request, before.Workspace.Revision));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Exception? error = null;
        var opening = new Thread(() => { try { workflow.OpenWorkspace(other.CurrentSession!.WorkspacePath); } catch (Exception e) { error = e; } });
        opening.Start();
        var waiting = SpinWait.SpinUntil(() => opening.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(10));
        release.Set(); Assert.True(waiting); var preview = await previewing; Assert.True(opening.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(error); Assert.True(preview.CanApply); Assert.Equal(before.Workspace.Revision, preview.SourceRevision);
        Assert.Equal(other.CurrentSession!.Workspace.Id, workflow.CurrentSession!.Workspace.Id);
        Assert.Equal(0, store.Mutations);
    }

    [Fact]
    public void ObserverReentryCannotPerformQueryAndObserverFailureCannotMisreportSavedMove()
    {
        var (workflow, _) = Create();
        var before = workflow.CurrentSession!.Workspace;
        var request = Move(workflow, First(before), "2026-09-13", new(11, 0), "A");
        string? queryError = null;
        workflow.SessionChanged += (_, _) =>
        {
            queryError = Assert.Throws<WorkspaceCommandException>(() => workflow.CaptureScheduleEditBaseline()).Error.Code;
            throw new IOException("模拟界面异常");
        };
        var saved = workflow.MoveMatch(request, before.Revision);
        Assert.Equal("workspace.notification-busy", queryError);
        Assert.True(workflow.CanUndoScheduleEdit);
        Assert.Equal(before.Revision + 1, saved.Workspace.Revision);
    }

    [Fact]
    public async Task UndoAvailabilityDoesNotBlockUiBehindSlowReadonlyPreview()
    {
        var (workflow, store) = Create();
        var root = First(workflow.CurrentSession!.Workspace);
        workflow.MoveMatch(Move(workflow, root, "2026-09-13", new(11, 0), "A"), workflow.CurrentSession.Workspace.Revision);
        var request = Move(workflow, root, "2026-09-13", new(12, 0), "A");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var queried = new ManualResetEventSlim();
        store.BeforeRead = _ => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var previewing = Task.Run(() => workflow.PreviewMove(request, workflow.CurrentSession!.Workspace.Revision));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var canUndo = false;
        var ui = new Thread(() => { canUndo = workflow.CanUndoScheduleEdit; queried.Set(); });
        ui.Start();
        var nonblocking = queried.Wait(TimeSpan.FromSeconds(1));
        release.Set(); await previewing; Assert.True(ui.Join(TimeSpan.FromSeconds(10)));
        Assert.True(nonblocking); Assert.True(canUndo);
    }

    private (TournamentWorkspaceWorkflow Workflow, ObservedStore Store) Create(int entrants = 2, int projects = 1,
        TournamentResourcePlan? resources = null, ObservedStore? store = null, string file = "workspace.szbd", bool sharedPlayers = false)
    {
        store ??= new();
        var workflow = new TournamentWorkspaceWorkflow(store);
        workflow.CreateWorkspace(new("编辑测试", TournamentKind.Individual, TournamentPurpose.FullTournament,
            new[] { EventDiscipline.MenSingles, EventDiscipline.WomenSingles, EventDiscipline.MenDoubles }.Take(projects)
                .Select(d => new WorkspaceProjectRequest(d, CompetitionMode.SinglesKnockout)).ToArray(), Path.Combine(directory, file)));
        foreach (var project in workflow.CurrentSession!.Workspace.Projects)
        {
            var path = Path.Combine(directory, project.Id + ".xlsx");
            using var workbook = new XLWorkbook();
            var sheet = workbook.AddWorksheet("名单");
            sheet.Cell(1, 1).Value = "姓名"; sheet.Cell(1, 2).Value = "学号";
            for (var i = 0; i < entrants; i++)
            { sheet.Cell(i + 2, 1).Value = project.DisplayName + i; sheet.Cell(i + 2, 2).Value = (sharedPlayers ? "shared" : project.Id.ToString()) + "-" + i; }
            workbook.SaveAs(path);
            workflow.ImportRoster(project.Id, path, workflow.CurrentSession.Workspace.Revision);
        }
        foreach (var project in workflow.CurrentSession.Workspace.Projects)
        {
            workflow.PreviewDraw(project.Id, new(CompetitionMode.SinglesKnockout, EventKind.Singles, 1, "edit-seed"), workflow.CurrentSession.Workspace.Revision);
            workflow.ConfirmDraw(project.Id, workflow.CurrentSession.Workspace.Revision);
        }
        workflow.GenerateSchedule(resources ?? Resources(), new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), workflow.CurrentSession.Workspace.Revision);
        store.Mutations = 0;
        return (workflow, store);
    }

    private static TournamentResourcePlan Resources() => new([
        new(new(2026, 9, 13), new(9, 0), new(15, 0), ["A", "B", "C"]),
        new(new(2026, 9, 16), new(9, 0), new(15, 0), ["A", "B", "C"])], 3, 15, 6);
    private static MatchNode First(TournamentWorkspace w) => w.Projects[0].MatchGraph!.Matches.OrderBy(n => n.Order).First();
    private static MoveMatchRequest Move(TournamentWorkspaceWorkflow w, MatchNode n, string day, TimeOnly time, string court) =>
        new(new(n.ProjectId, n.Id), day, time, court, w.CaptureScheduleEditBaseline());
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));
    private void ExportTemplate(TournamentWorkspaceWorkflow w, long revision) => w.ExportRosterTemplate(w.CurrentSession!.Workspace.Projects[0].Id,
        Path.Combine(directory, Guid.NewGuid() + ".xlsx"), revision);
    private void ReplaceArchive(string path, TournamentWorkspace workspace)
    {
        var temporary = Path.Combine(directory, Guid.NewGuid() + ".szbd");
        new TournamentWorkspaceStore().Create(temporary, workspace);
        File.Move(temporary, path, true);
    }
    private void AddResultAndRefresh(TournamentWorkspaceWorkflow w, MatchNode node)
    {
        var saved = new TournamentWorkspaceStore().Mutate(w.CurrentSession!.WorkspacePath, w.CurrentSession.Workspace.Revision, workspace =>
        {
            var result = new TournamentMatchResult(new(node.ProjectId, node.Id), (EntrantSource.Participant)node.SideA,
                (EntrantSource.Participant)node.SideB, "21:10 21:10", 30, DateTimeOffset.UtcNow);
            return workspace with { Results = workspace.Results.Append(new(result.Key, result)).ToDictionary(), Stage = TournamentStage.InProgress };
        }).Workspace;
        // An existing ordinary workflow publication refreshes the newly durable aggregate without Open's explicit session reset.
        ExportTemplate(w, saved.Revision);
    }
    private static void AssertValid(TournamentWorkspace w) => Assert.True(new TournamentPlacementValidator(new(
        w.Projects.Select(p => p.MatchGraph!).ToArray(), w.Resources!, w.Schedule!.Policy)
        { Results = w.Results, BaselinePlacements = w.Schedule.Placements }).ValidateSchedule(w.Schedule.Placements).IsValid);
    private static WorkspaceError RejectUnchanged(TournamentWorkspaceWorkflow w, ObservedStore store, Action action, string code, int mutations = 0)
    {
        var before = w.CurrentSession!;
        var hash = Hash(before.WorkspacePath);
        store.Mutations = 0;
        var error = Assert.Throws<WorkspaceCommandException>(action).Error;
        Assert.Equal(code, error.Code);
        Assert.False(error.Committed);
        Assert.Same(before, w.CurrentSession);
        Assert.Equal(hash, Hash(before.WorkspacePath));
        Assert.Equal(mutations, store.Mutations);
        return error;
    }
    private sealed class ObservedStore(ITournamentWorkspaceStore? inner = null) : ITournamentWorkspaceStore
    {
        private readonly ITournamentWorkspaceStore inner = inner ?? new TournamentWorkspaceStore();
        public int Mutations { get; set; }
        public Action? BeforeMutation { get; set; }
        public Action<string>? BeforeRead { get; set; }
        public TournamentWorkspace Create(string path, TournamentWorkspace workspace) => inner.Create(path, workspace);
        public TournamentWorkspace Read(string path) { BeforeRead?.Invoke(path); return inner.Read(path); }
        public WorkspaceMutationResult Mutate(string path, long revision, Func<TournamentWorkspace, TournamentWorkspace> mutation)
        { Mutations++; var before = BeforeMutation; BeforeMutation = null; before?.Invoke(); return inner.Mutate(path, revision, mutation); }
        public string CreateBackup(string path) => inner.CreateBackup(path);
        public WorkspaceBackupSnapshot InspectBackup(string path) => inner.InspectBackup(path);
        public WorkspaceRecoveryInspection InspectRecovery(string path, string backup) => inner.InspectRecovery(path, backup);
        public WorkspaceMutationResult RestoreBackup(string path, WorkspaceRestoreRequest request) => inner.RestoreBackup(path, request);
        public WorkspaceMutationResult RecoverFromBackup(string path, WorkspaceRecoveryRequest request) => inner.RecoverFromBackup(path, request);
    }
    private sealed class ControlledFiles : WorkspaceFileOperations
    {
        public bool FailPublish { get; set; }
        public Action? AfterPublish { get; set; }
        public override void Publish(string candidate, string destination, bool overwrite)
        {
            if (FailPublish) throw new IOException("模拟保存失败");
            base.Publish(candidate, destination, overwrite); AfterPublish?.Invoke();
        }
    }
    private sealed class ReadFaultStore(WorkspaceFileOperations files) : TournamentWorkspaceStore(files)
    {
        public string? FailPath { get; set; }
        public int FailuresRemaining { get; set; }
        public override TournamentWorkspace Read(string path)
        {
            if (path == FailPath && FailuresRemaining-- > 0) throw new WorkspaceStoreException("InvalidWorkspace", "模拟重新读取失败");
            return base.Read(path);
        }
    }
}
