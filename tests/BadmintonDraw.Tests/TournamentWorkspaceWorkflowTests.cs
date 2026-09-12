using System.Security.Cryptography;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class TournamentWorkspaceWorkflowTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("workspace-workflow-").FullName;
    private string PathFor(string name) => Path.Combine(directory, name);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public void CreateAndOpenPersistDraftWithoutImplicitWork()
    {
        var workflow = new TournamentWorkspaceWorkflow();
        var created = Create(workflow);
        Assert.Equal(0, created.Workspace.Revision);
        Assert.Equal(TournamentStage.Draft, created.Workspace.Stage);
        Assert.Null(created.Workspace.Projects[0].Roster);
        Assert.Null(created.Workspace.Projects[0].Draw);
        Assert.Null(created.Workspace.Schedule);
        var reopened = new TournamentWorkspaceWorkflow().OpenWorkspace(created.WorkspacePath);
        Assert.Equal(created.Workspace.Id, reopened.Workspace.Id);
        Assert.Equal(0, reopened.Workspace.Revision);
    }

    [Fact]
    public void ImportRosterSavesExactSourceIdentityAndDoesNotDrawOrSchedule()
    {
        var workflow = new TournamentWorkspaceWorkflow();
        var created = Create(workflow);
        var input = WriteRoster("名单.xlsx");
        var bytes = File.ReadAllBytes(input);
        var imported = workflow.ImportRoster(created.Workspace.Projects[0].Id, input, 0);
        var project = imported.Workspace.Projects[0];
        Assert.Equal(1, imported.Workspace.Revision);
        Assert.Equal(TournamentStage.RostersReady, imported.Workspace.Stage);
        Assert.Equal("名单.xlsx", project.Roster!.SourceFileName);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), project.Roster.ContentHash);
        Assert.Equal(new[] { "001", "002" }, project.Roster.Participants.Select(p => p.PrimaryStudentId));
        Assert.Null(project.Draw);
        Assert.Null(project.MatchGraph);
        Assert.Null(imported.Workspace.Schedule);
        Assert.True(File.Exists(imported.BackupPath));
        Assert.Equal(1, new TournamentWorkspaceStore().Read(imported.WorkspacePath).Revision);
    }

    [Fact]
    public void ImportLastRosterEnablesDrawsAndPartialConfirmationsSurviveOtherRosterReplacement()
    {
        var workflow = new TournamentWorkspaceWorkflow();
        var created = Create(workflow, twoProjects: true);
        var first = created.Workspace.Projects[0].Id;
        var second = created.Workspace.Projects[1].Id;
        var input = WriteRoster("名单.xlsx");
        Assert.Equal(TournamentStage.Draft, workflow.ImportRoster(first, input, 0).Workspace.Stage);
        AssertUnchanged(workflow, () => workflow.PreviewDraw(first, Settings(), 1), "stage.rosters");
        Assert.Equal(TournamentStage.RostersReady, workflow.ImportRoster(second, input, 1).Workspace.Stage);
        workflow.PreviewDraw(first, Settings(), 2);
        var confirmed = workflow.ConfirmDraw(first, 3);
        var confirmedGraph = confirmed.Workspace.Projects[0].MatchGraph!.Revision;
        Assert.Equal(TournamentStage.RostersReady, confirmed.Workspace.Stage);
        workflow.PreviewDraw(second, Settings(), 4);
        var updated = workflow.ImportRoster(second, input, 5);
        Assert.Equal(6, updated.Workspace.Revision);
        Assert.Equal(confirmedGraph, updated.Workspace.Projects[0].MatchGraph!.Revision);
        Assert.NotNull(updated.Workspace.Projects[0].Draw!.ConfirmedAt);
        Assert.Null(updated.Workspace.Projects[1].Draw);
        Assert.Null(updated.Workspace.Schedule);
        workflow.PreviewDraw(second, Settings(), 6);
        var complete = workflow.ConfirmDraw(second, 7);
        Assert.Equal(TournamentStage.DrawsConfirmed, complete.Workspace.Stage);
        Assert.Equal(8, complete.Workspace.Revision);
        Assert.Equal(confirmedGraph, complete.Workspace.Projects[0].MatchGraph!.Revision);
        Assert.Null(complete.Workspace.Schedule);
    }

    [Fact]
    public void PreviewUsesSavedRosterAndConfirmStopsAtDrawsConfirmed()
    {
        var workflow = Ready();
        var session = workflow.CurrentSession!;
        var projectId = session.Workspace.Projects[0].Id;
        File.Delete(PathFor("名单.xlsx"));
        var preview = workflow.PreviewDraw(projectId, Settings(), 1);
        Assert.Equal("test-seed", preview.Workspace.Projects[0].Draw!.Result.Audit.RandomSeed);
        Assert.Null(preview.Workspace.Projects[0].Draw!.ConfirmedAt);
        Assert.Null(preview.Workspace.Projects[0].MatchGraph);
        var confirmed = workflow.ConfirmDraw(projectId, 2);
        Assert.Equal(3, confirmed.Workspace.Revision);
        Assert.Equal(TournamentStage.DrawsConfirmed, confirmed.Workspace.Stage);
        Assert.Single(confirmed.Workspace.Projects[0].MatchGraph!.Matches);
        Assert.Null(confirmed.Workspace.Schedule);
        Assert.Null(confirmed.Workspace.Resources);
        var reopened = new TournamentWorkspaceWorkflow().OpenWorkspace(session.WorkspacePath);
        Assert.Equal(TournamentStage.DrawsConfirmed, reopened.Workspace.Stage);
        Assert.Null(reopened.Workspace.Schedule);
    }

    [Fact]
    public void FixedDisciplineAndModeRejectMismatchesWithoutChangingArchive()
    {
        var workflow = Ready();
        var id = workflow.CurrentSession!.Workspace.Projects[0].Id;
        AssertUnchanged(workflow, () => workflow.ImportRoster(id, WriteRoster("双打.xlsx", doubles: true), 1), "roster.discipline");
        AssertUnchanged(workflow, () => workflow.PreviewDraw(id, Settings() with { EventKind = EventKind.Doubles }, 1), "draw.settings");
        AssertUnchanged(workflow, () => workflow.PreviewDraw(id, Settings() with { CompetitionMode = CompetitionMode.SinglesRoundRobin }, 1), "draw.settings");
    }

    [Fact]
    public void DoublesRetainsBothPartnersAndWarnings()
    {
        var workflow = new TournamentWorkspaceWorkflow();
        var created = Create(workflow, discipline: EventDiscipline.MixedDoubles);
        var input = WriteRoster("双打.xlsx", doubles: true, sameNames: true);
        var imported = workflow.ImportRoster(created.Workspace.Projects[0].Id, input, 0);
        var roster = imported.Workspace.Projects[0].Roster!;
        Assert.Equal("003", roster.Participants[0].PartnerStudentId);
        Assert.Equal("004", roster.Participants[1].PartnerStudentId);
        Assert.Equal("搭档甲", roster.Participants[0].PartnerName);
        Assert.Contains(roster.Warnings, w => w.Code == "roster.duplicate-player-name");
        Assert.Contains(imported.Notices, n => n.Code == "roster.duplicate-player-name");
        workflow.PreviewDraw(created.Workspace.Projects[0].Id, Settings() with { EventKind = EventKind.Doubles }, 1);
        var graph = workflow.ConfirmDraw(created.Workspace.Projects[0].Id, 2).Workspace.Projects[0].MatchGraph!;
        var side = Assert.IsType<BadmintonDraw.Core.Matches.EntrantSource.Participant>(graph.Matches[0].SideA);
        Assert.Equal(2, side.Players.Count);
    }

    [Fact]
    public void ConfirmedDrawNeedsExplicitReopenAndPurposeUpgradePreservesGraph()
    {
        var workflow = Confirmed();
        var initial = workflow.CurrentSession!;
        var id = initial.Workspace.Projects[0].Id;
        AssertUnchanged(workflow, () => workflow.ImportRoster(id, PathFor("名单.xlsx"), 3), "draw.frozen");
        AssertUnchanged(workflow, () => workflow.ReopenDraw(id, " ", 3), "draw.reason");
        var upgraded = workflow.UpgradeToFullTournament(3);
        Assert.Equal(4, upgraded.Workspace.Revision);
        Assert.Equal(TournamentPurpose.FullTournament, upgraded.Workspace.Purpose);
        Assert.Equal(initial.Workspace.Projects[0].MatchGraph!.Revision, upgraded.Workspace.Projects[0].MatchGraph!.Revision);
        Assert.Single(upgraded.Workspace.AuditEvents, a => a.Action == "PurposeUpgraded");
        Assert.Null(upgraded.Workspace.Schedule);
        var reopened = workflow.ReopenDraw(id, "名单更正", 4);
        Assert.Equal(TournamentStage.RostersReady, reopened.Workspace.Stage);
        Assert.Null(reopened.Workspace.Projects[0].Draw);
        Assert.Null(reopened.Workspace.Projects[0].MatchGraph);
        Assert.Null(reopened.Workspace.Resources);
        Assert.Single(reopened.Workspace.AuditEvents, a => a.Action == "DrawReopened");
    }

    [Fact]
    public void ReopenRejectsResultsAndPreservesFormalArchive()
    {
        var store = new TournamentWorkspaceStore();
        var workspace = TournamentWorkspaceRulesTests.Fixture(TournamentStage.Completed);
        var path = PathFor("completed.szbd");
        store.Create(path, workspace);
        var workflow = new TournamentWorkspaceWorkflow(store);
        workflow.OpenWorkspace(path);
        AssertUnchanged(workflow, () => workflow.ReopenDraw(workspace.Projects[0].Id, "更正", workspace.Revision), "draw.frozen");
    }

    [Fact]
    public void ConfigurationRetainsIdsAndRosterButClearsChangedModePreview()
    {
        var workflow = Ready();
        var id = workflow.CurrentSession!.Workspace.Projects[0].Id;
        workflow.PreviewDraw(id, Settings(), 1);
        var changed = workflow.UpdateConfiguration(new("新名称", [new(EventDiscipline.MenSingles,
            CompetitionMode.SinglesRoundRobin, "男单循环", id)]), 2);
        Assert.Equal(3, changed.Workspace.Revision);
        Assert.Equal(id, changed.Workspace.Projects[0].Id);
        Assert.Equal("新名称", changed.Workspace.Name);
        Assert.Equal(CompetitionMode.SinglesRoundRobin, changed.Workspace.Projects[0].CompetitionMode);
        Assert.NotNull(changed.Workspace.Projects[0].Roster);
        Assert.Null(changed.Workspace.Projects[0].Draw);
        Assert.Equal(TournamentStage.RostersReady, changed.Workspace.Stage);
    }

    [Fact]
    public void AddingAndRemovingDraftProjectPreservesHistoricalAuditAndFutureSaves()
    {
        var workflow = Ready();
        var id = workflow.CurrentSession!.Workspace.Projects[0].Id;
        workflow.PreviewDraw(id, Settings(), 1);
        var added = workflow.UpdateConfiguration(new("杯赛", [new(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout, ProjectId: id),
            new(EventDiscipline.WomenSingles, CompetitionMode.SinglesKnockout)]), 2);
        Assert.Equal(TournamentStage.Draft, added.Workspace.Stage);
        Assert.NotNull(added.Workspace.Projects[0].Roster);
        Assert.Null(added.Workspace.Projects[0].Draw);
        var retained = added.Workspace.Projects[1].Id;
        var removed = workflow.UpdateConfiguration(new("杯赛", [new(EventDiscipline.WomenSingles,
            CompetitionMode.SinglesKnockout, ProjectId: retained)]), 3);
        Assert.Single(removed.Workspace.Projects);
        Assert.Contains(removed.Workspace.AuditEvents, a => a.ProjectId == id && a.Action == "RosterImported");
        Assert.Equal(TournamentStage.Draft, new TournamentWorkspaceStore().Read(removed.WorkspacePath).Stage);
        Assert.Equal(TournamentStage.RostersReady, workflow.ImportRoster(retained, PathFor("名单.xlsx"), 4).Workspace.Stage);
    }

    [Fact]
    public void ConfigurationRejectsFrozenOrForeignProjectsAndDisciplineChanges()
    {
        var workflow = Ready();
        var id = workflow.CurrentSession!.Workspace.Projects[0].Id;
        AssertUnchanged(workflow, () => workflow.UpdateConfiguration(new("杯赛", [new(EventDiscipline.WomenSingles,
            CompetitionMode.SinglesKnockout, ProjectId: id)]), 1), "project.discipline");
        AssertUnchanged(workflow, () => workflow.UpdateConfiguration(new("杯赛", [new(EventDiscipline.MenSingles,
            CompetitionMode.SinglesKnockout, ProjectId: Guid.NewGuid())]), 1), "project.not-found");
        workflow.PreviewDraw(id, Settings(), 1);
        workflow.ConfirmDraw(id, 2);
        AssertUnchanged(workflow, () => workflow.UpdateConfiguration(new("改名", [new(EventDiscipline.MenSingles,
            CompetitionMode.SinglesKnockout, ProjectId: id)]), 3), "configuration.frozen");
    }

    [Fact]
    public void DuplicateConfigurationProjectsReturnDomainErrorWithoutChangingArchive()
    {
        var workflow = Ready();
        var id = workflow.CurrentSession!.Workspace.Projects[0].Id;
        var item = new WorkspaceProjectRequest(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout, ProjectId: id);
        AssertUnchanged(workflow, () => workflow.UpdateConfiguration(new("杯赛", [item, item]), 1), "projects.duplicate");
    }

    [Fact]
    public void StaleRevisionAndInvalidInputPreserveSessionAndArchive()
    {
        var workflow = Ready();
        var session = workflow.CurrentSession!;
        var second = new TournamentWorkspaceWorkflow();
        second.OpenWorkspace(session.WorkspacePath);
        second.UpgradeToFullTournament(1);
        AssertUnchanged(workflow, () => workflow.PreviewDraw(session.Workspace.Projects[0].Id, Settings(), 1), "RevisionConflict");
        workflow.OpenWorkspace(session.WorkspacePath);
        AssertUnchanged(workflow, () => workflow.ImportRoster(session.Workspace.Projects[0].Id, PathFor("missing.xlsx"), 2), "roster.import");
        AssertUnchanged(workflow, () => workflow.OpenWorkspace(PathFor("missing.szbd")), "InvalidWorkspace");
    }

    [Fact]
    public void PublicationFailureRetainsCandidateAndBackupDetailsAndUnchangedSession()
    {
        var files = new ControlledFiles();
        var workflow = Ready(new TournamentWorkspaceStore(files));
        files.FailPublish = true;
        var error = AssertUnchanged(workflow, () => workflow.UpgradeToFullTournament(1), "WorkspaceWriteFailed");
        Assert.True(File.Exists(error.BackupPath));
        Assert.True(File.Exists(error.CandidatePath));
        Assert.False(error.Committed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedReadFailureRefreshesOrRequiresReloadAndKeepsRecoveryDetails(bool persistent)
    {
        var files = new ControlledFiles();
        var store = new FailingReadStore(files);
        var workflow = Ready(store);
        var path = workflow.CurrentSession!.WorkspacePath;
        files.AfterPublish = () => { store.FailPath = path; store.FailuresRemaining = persistent ? 10 : 1; };
        var exception = Assert.Throws<WorkspaceCommandException>(() => workflow.UpgradeToFullTournament(1));
        Assert.Equal("CommittedReadFailed", exception.Error.Code);
        Assert.True(exception.Error.Committed);
        Assert.True(File.Exists(exception.Error.BackupPath));
        Assert.Equal(persistent, workflow.CurrentSession!.RequiresReload);
        Assert.Equal(persistent ? 1 : 2, workflow.CurrentSession.Workspace.Revision);
        Assert.Equal(2, new TournamentWorkspaceStore().Read(path).Revision);
        if (persistent) Assert.Equal("workspace.reload-required", Assert.Throws<WorkspaceCommandException>(() =>
            workflow.PreviewDraw(workflow.CurrentSession.Workspace.Projects[0].Id, Settings(), 1)).Error.Code);
        store.FailuresRemaining = 0;
        workflow.OpenWorkspace(path);
        Assert.False(workflow.CurrentSession!.RequiresReload);
    }

    [Fact]
    public void SessionObserverCannotTurnCommittedCommandIntoFailure()
    {
        var workflow = Ready();
        WorkspaceSession? observed = null;
        workflow.SessionChanged += (_, _) => throw new InvalidOperationException("broken view");
        workflow.SessionChanged += (_, session) => observed = session;
        var result = workflow.UpgradeToFullTournament(1);
        Assert.Equal(2, result.Workspace.Revision);
        Assert.Same(workflow.CurrentSession, observed);
        Assert.Equal(2, new TournamentWorkspaceStore().Read(result.WorkspacePath).Revision);
    }

    [Fact]
    public void SessionObserverCannotReenterCommandsAndReplaceThePublishedSession()
    {
        var workflow = Ready();
        var originalPath = workflow.CurrentSession!.WorkspacePath;
        var other = Create(new TournamentWorkspaceWorkflow(), file: "other.szbd");
        var attempted = false;
        WorkspaceSession? observed = null;
        workflow.SessionChanged += (_, _) =>
        {
            if (attempted) return;
            attempted = true;
            workflow.OpenWorkspace(other.WorkspacePath);
        };
        workflow.SessionChanged += (_, session) => observed = session;
        var result = workflow.UpgradeToFullTournament(1);
        Assert.True(attempted);
        Assert.Equal(originalPath, workflow.CurrentSession!.WorkspacePath);
        Assert.Same(workflow.CurrentSession, observed);
        Assert.Equal(2, result.Workspace.Revision);
        Assert.Equal(2, new TournamentWorkspaceStore().Read(originalPath).Revision);
    }

    [Fact]
    public async Task OpeningAnotherWorkspaceWaitsForRunningMutationAndCannotBeOverwrittenByIt()
    {
        var files = new ControlledFiles();
        var workflow = Ready(new TournamentWorkspaceStore(files));
        var oldPath = workflow.CurrentSession!.WorkspacePath;
        var other = Create(new TournamentWorkspaceWorkflow(), file: "other.szbd");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        files.BeforePublish = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var mutation = Task.Run(() => workflow.UpgradeToFullTournament(1));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var opening = Task.Run(() => workflow.OpenWorkspace(other.WorkspacePath));
        release.Set();
        await Task.WhenAll(mutation, opening);
        Assert.Equal(other.Workspace.Id, workflow.CurrentSession!.Workspace.Id);
        Assert.Equal(other.WorkspacePath, workflow.CurrentSession.WorkspacePath);
        Assert.Equal(TournamentPurpose.FullTournament, new TournamentWorkspaceStore().Read(oldPath).Purpose);
    }

    [Fact]
    public async Task CommandQueuedBehindOpenCannotApplyToDifferentWorkspaceWithSameRevision()
    {
        var store = new FailingReadStore(new ControlledFiles());
        var workflow = Ready(store);
        var other = Create(new TournamentWorkspaceWorkflow(), file: "other.szbd");
        var otherWorkflow = new TournamentWorkspaceWorkflow();
        otherWorkflow.OpenWorkspace(other.WorkspacePath);
        otherWorkflow.ImportRoster(other.Workspace.Projects[0].Id, PathFor("名单.xlsx"), 0);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.BeforeRead = path =>
        {
            if (path == other.WorkspacePath) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
        };
        var opening = Task.Run(() => workflow.OpenWorkspace(other.WorkspacePath));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Exception? failure = null;
        var queued = new Thread(() =>
        {
            try { workflow.UpgradeToFullTournament(1); }
            catch (Exception exception) { failure = exception; }
        });
        queued.Start();
        var waiting = SpinWait.SpinUntil(() => queued.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(10));
        release.Set();
        Assert.True(waiting);
        await opening;
        Assert.True(queued.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal("workspace.session-changed", Assert.IsType<WorkspaceCommandException>(failure).Error.Code);
        Assert.Equal(other.Workspace.Id, workflow.CurrentSession!.Workspace.Id);
        var saved = new TournamentWorkspaceStore().Read(other.WorkspacePath);
        Assert.Equal(1, saved.Revision);
        Assert.Equal(TournamentPurpose.PublicDrawOnly, saved.Purpose);
    }

    private TournamentWorkspaceWorkflow Ready(ITournamentWorkspaceStore? store = null)
    {
        var workflow = new TournamentWorkspaceWorkflow(store);
        var created = Create(workflow);
        workflow.ImportRoster(created.Workspace.Projects[0].Id, WriteRoster("名单.xlsx"), 0);
        return workflow;
    }
    private TournamentWorkspaceWorkflow Confirmed()
    {
        var workflow = Ready();
        var id = workflow.CurrentSession!.Workspace.Projects[0].Id;
        workflow.PreviewDraw(id, Settings(), 1);
        workflow.ConfirmDraw(id, 2);
        return workflow;
    }
    private WorkspaceCommandResult Create(TournamentWorkspaceWorkflow workflow, bool twoProjects = false,
        EventDiscipline discipline = EventDiscipline.MenSingles, string file = "workspace.szbd") =>
        workflow.CreateWorkspace(new("杯赛", TournamentKind.Individual, TournamentPurpose.PublicDrawOnly,
            twoProjects ? [new(discipline, CompetitionMode.SinglesKnockout), new(EventDiscipline.WomenSingles, CompetitionMode.SinglesKnockout)]
                : [new(discipline, CompetitionMode.SinglesKnockout)], PathFor(file)));
    private static DrawSettings Settings() => new(CompetitionMode.SinglesKnockout, EventKind.Singles, 1, "test-seed");
    private string WriteRoster(string name, bool doubles = false, bool sameNames = false)
    {
        var path = PathFor(name);
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("名单");
        sheet.Cell(1, 1).Value = "姓名"; sheet.Cell(1, 2).Value = "学号";
        sheet.Cell(1, 3).Value = "搭档姓名"; sheet.Cell(1, 4).Value = "搭档学号";
        sheet.Cell(2, 1).Value = "甲"; sheet.Cell(3, 1).Value = sameNames ? "甲" : "乙";
        sheet.Cell(2, 2).Value = "001"; sheet.Cell(3, 2).Value = "002";
        if (doubles)
        {
            sheet.Cell(2, 3).Value = "搭档甲"; sheet.Cell(3, 3).Value = "搭档乙";
            sheet.Cell(2, 4).Value = "003"; sheet.Cell(3, 4).Value = "004";
        }
        workbook.SaveAs(path);
        return path;
    }
    private static WorkspaceError AssertUnchanged(TournamentWorkspaceWorkflow workflow, Action action, string code)
    {
        var session = workflow.CurrentSession!;
        var hash = SHA256.HashData(File.ReadAllBytes(session.WorkspacePath));
        var error = Assert.Throws<WorkspaceCommandException>(action).Error;
        Assert.Equal(code, error.Code);
        Assert.False(error.Committed);
        Assert.Same(session, workflow.CurrentSession);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(session.WorkspacePath)));
        return error;
    }
    private sealed class ControlledFiles : WorkspaceFileOperations
    {
        public bool FailPublish { get; set; }
        public Action? BeforePublish { get; set; }
        public Action? AfterPublish { get; set; }
        public override void Publish(string candidate, string destination, bool overwrite)
        {
            BeforePublish?.Invoke();
            if (FailPublish) throw new IOException("模拟发布失败");
            base.Publish(candidate, destination, overwrite);
            AfterPublish?.Invoke();
        }
    }
    private sealed class FailingReadStore(WorkspaceFileOperations files) : TournamentWorkspaceStore(files)
    {
        public string? FailPath { get; set; }
        public int FailuresRemaining { get; set; }
        public Action<string>? BeforeRead { get; set; }
        public override TournamentWorkspace Read(string path)
        {
            BeforeRead?.Invoke(path);
            if (path == FailPath && FailuresRemaining-- > 0) throw new WorkspaceStoreException("InvalidWorkspace", "模拟重新读取失败");
            return base.Read(path);
        }
    }
}
