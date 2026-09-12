using System.Security.Cryptography;
using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class TournamentSchedulingWorkflowTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("schedule-workflow-").FullName;
    private string PathFor(string name) => Path.Combine(directory, name);
    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ConfirmedProjectsGenerateOneDurableGlobalSchedule(int projectCount)
    {
        var store = new ObservedStore();
        var workflow = Confirmed(store, projectCount);
        var before = workflow.CurrentSession!;
        var sources = JsonSerializer.Serialize(before.Workspace.Projects);
        store.Mutations = 0;
        var result = workflow.GenerateSchedule(Resources(), Policy(), before.Workspace.Revision);

        Assert.Equal(1, store.Mutations);
        Assert.Equal(before.Workspace.Revision + 1, result.Workspace.Revision);
        Assert.Equal(before.Workspace.AuditEvents.Count + 1, result.Workspace.AuditEvents.Count);
        Assert.Single(result.Workspace.AuditEvents, a => a.Action == "ScheduleGenerated");
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(sources, JsonSerializer.Serialize(result.Workspace.Projects));
        var reopened = new TournamentWorkspaceWorkflow().OpenWorkspace(result.WorkspacePath).Workspace;
        Assert.Equal(TournamentStage.ScheduleReady, reopened.Stage);
        Assert.Empty(reopened.Results);
        Assert.Equal(projectCount, reopened.Schedule!.GraphRevisions.Count);
        Assert.Equal(reopened.Projects.Sum(p => p.MatchGraph!.Matches.Count), reopened.Schedule.Placements.Count);
        Assert.Equal(JsonSerializer.Serialize(result.Workspace.Schedule), JsonSerializer.Serialize(reopened.Schedule));
        Assert.Equal(JsonSerializer.Serialize(reopened.Resources), JsonSerializer.Serialize(reopened.Schedule.Resources));
        Assert.NotEmpty(reopened.Schedule.Policy.DayLoadTargets); // Persist effective defaults, not raw empty settings.
        Assert.Equal(projectCount * 4, reopened.Schedule.Policy.FinalDayRules.Count);
        Assert.True(new TournamentPlacementValidator(new(reopened.Projects.Select(p => p.MatchGraph!).ToArray(),
            reopened.Resources!, reopened.Schedule.Policy)).ValidateSchedule(reopened.Schedule.Placements).IsValid);
    }

    [Fact]
    public void DrawOnlyFailsWithoutMutationAndUpgradePreservesDrawsThenEnablesScheduling()
    {
        var store = new ObservedStore();
        var workflow = Confirmed(store, purpose: TournamentPurpose.PublicDrawOnly);
        var before = workflow.CurrentSession!.Workspace;
        var sources = JsonSerializer.Serialize(before.Projects);
        RejectUnchanged(workflow, store, () => workflow.GenerateSchedule(Resources(), Policy(), before.Revision), "stage.draw-only");
        workflow.UpgradeToFullTournament(before.Revision);
        Assert.Equal(sources, JsonSerializer.Serialize(workflow.CurrentSession!.Workspace.Projects));
        var saved = workflow.GenerateSchedule(Resources(), Policy(), workflow.CurrentSession.Workspace.Revision);
        Assert.Equal(sources, JsonSerializer.Serialize(saved.Workspace.Projects));
        Assert.Equal(TournamentStage.ScheduleReady, saved.Workspace.Stage);
    }

    [Fact]
    public void ProjectIdRulesAndDurationOverridesRoundTripWithoutChangingConfirmedGraphs()
    {
        var store = new ObservedStore();
        var workflow = Confirmed(store, 3);
        var before = workflow.CurrentSession!.Workspace;
        var sources = JsonSerializer.Serialize(before.Projects);
        var resources = Resources() with { Days = [.. Resources().Days, new(new(2026, 9, 14), new(9, 0), new(15, 0), ["1", "2", "3"])] };
        var id = before.Projects[2].Id;
        var policy = Policy() with
        {
            Strategy = ScheduleAutoSchedulingStrategy.Custom,
            DayLoadTargets = [new("2026-09-13", .42, .72), new("2026-09-14", .37, .67)],
            FinalDayRules = [new(id, TournamentFinalDayMatchCategory.Final, TournamentFinalDayPreference.StronglyPreferFinalDay)],
            ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [id] = new(45) }
        };
        var result = workflow.GenerateSchedule(resources, policy, before.Revision);
        var stored = new TournamentWorkspaceStore().Read(result.WorkspacePath);
        Assert.Equal(sources, JsonSerializer.Serialize(stored.Projects));
        Assert.Equal(policy.DayLoadTargets, stored.Schedule!.Policy.DayLoadTargets);
        Assert.Contains(policy.FinalDayRules[0], stored.Schedule.Policy.FinalDayRules);
        Assert.Equal(new ProjectMatchTiming(45), stored.Schedule.Policy.ProjectTimings[id]);
        var placement = stored.Schedule.Placements[stored.Projects[2].MatchGraph!.Matches.Single().Id];
        Assert.Equal("2026-09-14", placement.DayLabel);
        Assert.Equal(45, (placement.EndTime - placement.StartTime).TotalMinutes);
    }

    [Fact]
    public void FourHoursFailWithTypedDetailsAndZeroWritesThenSixHoursSucceed()
    {
        var store = new ObservedStore();
        var workflow = Confirmed(store, roundRobin: true);
        var before = workflow.CurrentSession!.Workspace;
        var policy = Policy() with { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [before.Projects[0].Id] = new(45) } };
        var error = RejectUnchanged(workflow, store, () => workflow.GenerateSchedule(Resources(endHour: 13, courts: ["1"]), policy,
            before.Revision), "schedule.generation-failed");
        var detail = Assert.IsType<SchedulingFailure>(error.SchedulingFailure);
        Assert.NotEmpty(detail.UnplacedMatches);
        Assert.All(detail.UnplacedMatches, m => Assert.Equal(before.Projects[0].Id, m.ProjectId));
        Assert.All(detail.UnplacedMatches, m => Assert.Equal(before.Projects[0].DisplayName, m.ProjectName));
        Assert.NotEmpty(detail.Suggestions);
        Assert.NotEmpty(detail.Capacity);
        var result = workflow.GenerateSchedule(Resources(courts: ["1"]), policy, before.Revision);
        Assert.Equal(1, store.Mutations);
        Assert.Equal(6, result.Workspace.Schedule!.Placements.Count);
    }

    [Fact]
    public void FailedRegenerationRetainsExactPriorScheduleAndArchiveWithoutMutation()
    {
        var store = new ObservedStore();
        var workflow = Confirmed(store, roundRobin: true);
        workflow.GenerateSchedule(Resources(), Policy(), workflow.CurrentSession!.Workspace.Revision);
        var before = workflow.CurrentSession!.Workspace;
        var schedule = JsonSerializer.Serialize(before.Schedule);
        var error = RejectUnchanged(workflow, store, () => workflow.GenerateSchedule(Resources() with { MaxPlayerMatchesPerDay = 1 },
            Policy(), before.Revision), "schedule.generation-failed");
        Assert.NotNull(error.SchedulingFailure);
        var stored = new TournamentWorkspaceStore().Read(workflow.CurrentSession!.WorkspacePath);
        Assert.Equal(schedule, JsonSerializer.Serialize(stored.Schedule));
        Assert.Equal(before.Revision, stored.Revision);
        Assert.Equal(before.Schedule!.Revision, stored.Schedule!.Revision);
    }

    [Fact]
    public void RegenerationUsesGenuineBaselineAndCanChangeDatesCourtsAndDurations()
    {
        var store = new ObservedStore();
        var workflow = Confirmed(store);
        var first = workflow.GenerateSchedule(Resources(startHour: 12), Policy(), workflow.CurrentSession!.Workspace.Revision).Workspace;
        Assert.Equal(new TimeOnly(12, 0), first.Schedule!.Placements.Values.Single().StartTime);
        store.Mutations = 0;
        var second = workflow.GenerateSchedule(Resources(), Policy(), first.Revision).Workspace;
        Assert.Equal(1, store.Mutations);
        Assert.Equal(new TimeOnly(12, 0), second.Schedule!.Placements.Values.Single().StartTime); // Fresh Compact would choose 09:00.
        Assert.Equal(first.Schedule.Revision + 1, second.Schedule.Revision);
        var changed = Resources() with { Days = [new(new(2026, 9, 15), new(9, 0), new(15, 0), ["新场地"])] };
        var timing = Policy() with { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [first.Projects[0].Id] = new(45) } };
        var third = workflow.GenerateSchedule(changed, timing, second.Revision).Workspace;
        var moved = third.Schedule!.Placements.Values.Single();
        Assert.Equal("2026-09-15", moved.DayLabel);
        Assert.Equal("新场地", moved.Court);
        Assert.Equal(45, (moved.EndTime - moved.StartTime).TotalMinutes);
        Assert.Equal(second.Schedule.Revision + 1, third.Schedule.Revision);
        Assert.Equal(3, third.AuditEvents.Count(a => a.Action == "ScheduleGenerated"));
    }

    [Fact]
    public void NewDrawAfterReopenDoesNotReuseAnEarlierScheduleRevision()
    {
        var store = new ObservedStore();
        var workflow = Confirmed(store);
        var first = workflow.GenerateSchedule(Resources(), Policy(), workflow.CurrentSession!.Workspace.Revision).Workspace;
        var id = first.Projects[0].Id;
        workflow.ReopenDraw(id, "更正抽签", first.Revision);
        workflow.PreviewDraw(id, new(CompetitionMode.SinglesKnockout, EventKind.Singles, 1, "replacement"), workflow.CurrentSession!.Workspace.Revision);
        workflow.ConfirmDraw(id, workflow.CurrentSession!.Workspace.Revision);
        var next = workflow.GenerateSchedule(Resources(), Policy(), workflow.CurrentSession.Workspace.Revision).Workspace;
        Assert.True(next.Schedule!.Revision > first.Schedule!.Revision);
    }

    [Fact]
    public void MissingDrawsStaleCallerRevisionAndNoSessionAreRejectedBeforeMutation()
    {
        var store = new ObservedStore();
        var workflow = new TournamentWorkspaceWorkflow(store);
        Assert.Equal("workspace.not-open", Assert.Throws<WorkspaceCommandException>(() => workflow.GenerateSchedule(Resources(), Policy(), 0)).Error.Code);
        workflow.CreateWorkspace(new("未抽签", TournamentKind.Individual, TournamentPurpose.FullTournament,
            [new(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout)], PathFor("draft.szbd")));
        RejectUnchanged(workflow, store, () => workflow.GenerateSchedule(Resources(), Policy(), 0), "stage.draws");
        var confirmed = Confirmed(store);
        RejectUnchanged(confirmed, store, () => confirmed.GenerateSchedule(Resources(), Policy(), 0), "RevisionConflict");
    }

    [Theory]
    [InlineData(TournamentStage.ScheduleReady)]
    [InlineData(TournamentStage.InProgress)]
    [InlineData(TournamentStage.Completed)]
    public void AnyRecordedResultForbidsRegenerationEvenWhileScheduleReady(TournamentStage stage)
    {
        var store = new ObservedStore();
        var workflow = Confirmed(store);
        var scheduled = workflow.GenerateSchedule(Resources(), Policy(), workflow.CurrentSession!.Workspace.Revision);
        var node = scheduled.Workspace.Projects[0].MatchGraph!.Matches.Single();
        var key = new WorkspaceMatchKey(node.ProjectId, node.Id);
        store.Mutate(scheduled.WorkspacePath, scheduled.Workspace.Revision, w => w with
        {
            Stage = stage,
            Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>
            { [key] = new(key, Assert.IsType<EntrantSource.Participant>(node.SideA), Assert.IsType<EntrantSource.Participant>(node.SideB), "21-10", 30, DateTimeOffset.UtcNow) }
        });
        workflow.OpenWorkspace(scheduled.WorkspacePath);
        RejectUnchanged(workflow, store, () => workflow.GenerateSchedule(Resources(), Policy(), workflow.CurrentSession!.Workspace.Revision), "schedule.results");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidGlobalSettingsRetainTypedConstraintCodesAndNeverStartMutation(bool invalidPolicy)
    {
        var store = new ObservedStore();
        var workflow = Confirmed(store);
        var resources = invalidPolicy ? Resources() : Resources() with { MaxPlayerMatchesPerDay = 0 };
        var policy = invalidPolicy ? Policy() with { ProjectTimings = new Dictionary<Guid, ProjectMatchTiming> { [Guid.NewGuid()] = new(45) } } : Policy();
        var error = RejectUnchanged(workflow, store, () => workflow.GenerateSchedule(resources, policy, workflow.CurrentSession!.Workspace.Revision),
            "schedule.generation-failed");
        Assert.Contains(error.SchedulingFailure!.Violations, v => v.Code ==
            (invalidPolicy ? SchedulingConstraintCode.InvalidPolicy : SchedulingConstraintCode.InvalidResources));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DurableChangesAfterSearchCannotPublishStaleSchedule(bool replaceIdentity)
    {
        var store = new ObservedStore();
        var workflow = Confirmed(store);
        var before = workflow.CurrentSession!;
        byte[]? changedHash = null;
        store.BeforeMutation = () =>
        {
            if (replaceIdentity) ReplaceArchive(before.WorkspacePath, before.Workspace with { Id = Guid.NewGuid() });
            else new TournamentWorkspaceStore().Mutate(before.WorkspacePath, before.Workspace.Revision, w => w with { Name = "外部更新" });
            changedHash = Hash(before.WorkspacePath);
        };
        var error = Assert.Throws<WorkspaceCommandException>(() => workflow.GenerateSchedule(Resources(), Policy(), before.Workspace.Revision)).Error;
        Assert.Equal(replaceIdentity ? "workspace.session-changed" : "RevisionConflict", error.Code);
        Assert.False(error.Committed);
        Assert.Same(before, workflow.CurrentSession);
        Assert.Equal(changedHash, Hash(before.WorkspacePath));
        Assert.Null(new TournamentWorkspaceStore().Read(before.WorkspacePath).Schedule);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameRevisionGraphContentReplacementIsRejectedBeforeAndAtPublication(bool afterSearch)
    {
        var store = new ObservedStore();
        var workflow = Confirmed(store);
        var before = workflow.CurrentSession!;
        void ReplaceGraph()
        {
            var project = before.Workspace.Projects[0];
            var graph = project.MatchGraph!;
            ReplaceArchive(before.WorkspacePath, before.Workspace with { Projects = [project with
            { MatchGraph = graph with { Matches = [graph.Matches[0] with { ExpectedDurationMinutes = 45 }] } }] });
        }
        if (afterSearch) store.BeforeMutation = ReplaceGraph; else ReplaceGraph();
        store.Mutations = 0;
        var error = Assert.Throws<WorkspaceCommandException>(() => workflow.GenerateSchedule(Resources(), Policy(), before.Workspace.Revision)).Error;
        Assert.Equal("schedule.source-changed", error.Code);
        Assert.Equal(afterSearch ? 1 : 0, store.Mutations);
        Assert.Same(before, workflow.CurrentSession);
        var stored = new TournamentWorkspaceStore().Read(before.WorkspacePath);
        Assert.Null(stored.Schedule);
        Assert.Equal(45, stored.Projects[0].MatchGraph!.Matches[0].ExpectedDurationMinutes);
    }

    [Fact]
    public void PublicationFaultPreservesArchiveAndRecoveryDiagnostics()
    {
        var files = new ControlledFiles();
        var store = new ObservedStore(new ReadFaultStore(files));
        var workflow = Confirmed(store);
        files.FailPublish = true;
        var error = RejectUnchanged(workflow, store, () => workflow.GenerateSchedule(Resources(), Policy(), workflow.CurrentSession!.Workspace.Revision),
            "WorkspaceWriteFailed", expectedMutations: 1, unchangedFiles: false);
        Assert.True(File.Exists(error.BackupPath));
        Assert.True(File.Exists(error.CandidatePath));
        Assert.Null(error.SchedulingFailure);
        var candidate = new TournamentWorkspaceStore().Read(error.CandidatePath!);
        Assert.Equal(TournamentStage.ScheduleReady, candidate.Stage);
        Assert.Single(candidate.AuditEvents, a => a.Action == "ScheduleGenerated");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedReadFailurePublishesOnlyOnceAndRefreshesOrRequiresReload(bool persistent)
    {
        var files = new ControlledFiles();
        var inner = new ReadFaultStore(files);
        var store = new ObservedStore(inner);
        var workflow = Confirmed(store);
        var before = workflow.CurrentSession!;
        files.AfterPublish = () => { inner.FailPath = before.WorkspacePath; inner.FailuresRemaining = persistent ? 10 : 1; };
        store.Mutations = 0;
        var error = Assert.Throws<WorkspaceCommandException>(() => workflow.GenerateSchedule(Resources(), Policy(), before.Workspace.Revision)).Error;
        Assert.Equal("CommittedReadFailed", error.Code);
        Assert.True(error.Committed);
        Assert.True(File.Exists(error.BackupPath));
        Assert.Equal(1, store.Mutations);
        Assert.Equal(persistent, workflow.CurrentSession!.RequiresReload);
        var saved = new TournamentWorkspaceStore().Read(before.WorkspacePath);
        Assert.Equal(before.Workspace.Revision + 1, saved.Revision);
        Assert.Single(saved.AuditEvents, a => a.Action == "ScheduleGenerated");
        Assert.Equal(TournamentStage.ScheduleReady, saved.Stage);
        if (persistent)
            RejectUnchanged(workflow, store, () => workflow.GenerateSchedule(Resources(), Policy(), before.Workspace.Revision), "workspace.reload-required");
        else
            RejectUnchanged(workflow, store, () => workflow.GenerateSchedule(Resources(), Policy(), before.Workspace.Revision), "RevisionConflict");
    }

    [Fact]
    public async Task QueuedGenerationCannotMigrateAcrossAnOpenWithSameRevision()
    {
        var store = new ObservedStore();
        var workflow = Confirmed(store);
        var before = workflow.CurrentSession!;
        var other = Confirmed(new ObservedStore(), file: "other.szbd").CurrentSession!;
        Assert.Equal(before.Workspace.Revision, other.Workspace.Revision);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.BeforeRead = path => { if (path == other.WorkspacePath) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        store.Mutations = 0;
        var opening = Task.Run(() => workflow.OpenWorkspace(other.WorkspacePath));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Exception? failure = null;
        var queued = new Thread(() => { try { workflow.GenerateSchedule(Resources(), Policy(), before.Workspace.Revision); } catch (Exception e) { failure = e; } });
        queued.Start();
        var waiting = SpinWait.SpinUntil(() => queued.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(10));
        release.Set();
        Assert.True(waiting);
        await opening;
        Assert.True(queued.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal("workspace.session-changed", Assert.IsType<WorkspaceCommandException>(failure).Error.Code);
        Assert.Equal(0, store.Mutations);
        Assert.Equal(other.Workspace.Id, workflow.CurrentSession!.Workspace.Id);
        Assert.Null(new TournamentWorkspaceStore().Read(before.WorkspacePath).Schedule);
        Assert.Null(new TournamentWorkspaceStore().Read(other.WorkspacePath).Schedule);
    }

    [Fact]
    public async Task RunningGenerationKeepsSessionGateUntilItsSinglePublicationCompletes()
    {
        var store = new ObservedStore();
        var workflow = Confirmed(store);
        var before = workflow.CurrentSession!;
        var other = Confirmed(new ObservedStore(), file: "other.szbd").CurrentSession!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.BeforeMutation = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        store.Mutations = 0;
        var generating = Task.Run(() => workflow.GenerateSchedule(Resources(), Policy(), before.Workspace.Revision));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Exception? failure = null;
        var opening = new Thread(() => { try { workflow.OpenWorkspace(other.WorkspacePath); } catch (Exception e) { failure = e; } });
        opening.Start();
        var waiting = SpinWait.SpinUntil(() => opening.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(10));
        release.Set();
        Assert.True(waiting);
        var result = await generating;
        Assert.True(opening.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
        Assert.Equal(1, store.Mutations);
        Assert.Equal(before.Workspace.Id, result.Workspace.Id);
        Assert.Equal(TournamentStage.ScheduleReady, new TournamentWorkspaceStore().Read(before.WorkspacePath).Stage);
        Assert.Equal(other.Workspace.Id, workflow.CurrentSession!.Workspace.Id);
        Assert.Null(workflow.CurrentSession.Workspace.Schedule);
    }

    private TournamentWorkspaceWorkflow Confirmed(ObservedStore store, int projectCount = 1,
        TournamentPurpose purpose = TournamentPurpose.FullTournament, bool roundRobin = false, string file = "workspace.szbd")
    {
        var workflow = new TournamentWorkspaceWorkflow(store);
        var disciplines = new[] { EventDiscipline.MenSingles, EventDiscipline.WomenSingles, EventDiscipline.MixedDoubles };
        var mode = roundRobin ? CompetitionMode.SinglesRoundRobin : CompetitionMode.SinglesKnockout;
        workflow.CreateWorkspace(new("集成测试赛事", TournamentKind.Individual, purpose,
            disciplines.Take(projectCount).Select(d => new WorkspaceProjectRequest(d, mode)).ToArray(), PathFor(file)));
        foreach (var project in workflow.CurrentSession!.Workspace.Projects)
        {
            var input = PathFor(project.Id + ".xlsx");
            using var workbook = new XLWorkbook();
            var sheet = workbook.AddWorksheet("名单");
            sheet.Cell(1, 1).Value = "姓名"; sheet.Cell(1, 2).Value = "学号";
            sheet.Cell(1, 3).Value = "搭档姓名"; sheet.Cell(1, 4).Value = "搭档学号";
            for (var i = 0; i < (roundRobin ? 4 : 2); i++)
            {
                sheet.Cell(i + 2, 1).Value = "测试选手" + i; sheet.Cell(i + 2, 2).Value = "00" + i;
                if (project.Discipline == EventDiscipline.MixedDoubles)
                { sheet.Cell(i + 2, 3).Value = "测试搭档" + i; sheet.Cell(i + 2, 4).Value = "10" + i; }
            }
            workbook.SaveAs(input);
            workflow.ImportRoster(project.Id, input, workflow.CurrentSession.Workspace.Revision);
        }
        foreach (var project in workflow.CurrentSession.Workspace.Projects)
        {
            workflow.PreviewDraw(project.Id, new(mode, project.Discipline == EventDiscipline.MixedDoubles ? EventKind.Doubles : EventKind.Singles,
                1, "integration-seed"), workflow.CurrentSession.Workspace.Revision);
            workflow.ConfirmDraw(project.Id, workflow.CurrentSession.Workspace.Revision);
        }
        return workflow;
    }

    private static TournamentSchedulingPolicy Policy() => new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []);
    private static TournamentResourcePlan Resources(int startHour = 9, int endHour = 15, string[]? courts = null) =>
        new([new(new(2026, 9, 13), new(startHour, 0), new(endHour, 0), courts ?? ["1", "2", "3"])], 3, 15, 6);
    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));
    private WorkspaceError RejectUnchanged(TournamentWorkspaceWorkflow workflow, ObservedStore store, Action action, string code,
        int expectedMutations = 0, bool unchangedFiles = true)
    {
        var before = workflow.CurrentSession!;
        var hash = Hash(before.WorkspacePath);
        var files = Directory.GetFiles(directory).Order().ToArray();
        store.Mutations = 0;
        var error = Assert.Throws<WorkspaceCommandException>(action).Error;
        Assert.Equal(code, error.Code);
        Assert.False(error.Committed);
        Assert.Same(before, workflow.CurrentSession);
        Assert.Equal(hash, Hash(before.WorkspacePath));
        Assert.Equal(expectedMutations, store.Mutations);
        if (unchangedFiles) Assert.Equal(files, Directory.GetFiles(directory).Order().ToArray());
        return error;
    }
    private void ReplaceArchive(string path, TournamentWorkspace replacement)
    {
        var candidate = PathFor(Guid.NewGuid() + ".szbd");
        new TournamentWorkspaceStore().Create(candidate, replacement);
        File.Move(candidate, path, true);
    }
    private sealed class ObservedStore(ITournamentWorkspaceStore? inner = null) : ITournamentWorkspaceStore
    {
        private readonly ITournamentWorkspaceStore inner = inner ?? new TournamentWorkspaceStore();
        public int Mutations { get; set; }
        public Action? BeforeMutation { get; set; }
        public Action<string>? BeforeRead { get; set; }
        public TournamentWorkspace Create(string path, TournamentWorkspace workspace) => inner.Create(path, workspace);
        public TournamentWorkspace Read(string path) { BeforeRead?.Invoke(path); return inner.Read(path); }
        public WorkspaceMutationResult Mutate(string path, long expectedRevision, Func<TournamentWorkspace, TournamentWorkspace> mutation)
        {
            Mutations++;
            var before = BeforeMutation; BeforeMutation = null; before?.Invoke();
            return inner.Mutate(path, expectedRevision, mutation);
        }
        public string CreateBackup(string path) => inner.CreateBackup(path);
        public TournamentWorkspace RestoreBackup(string path, string backupPath) => inner.RestoreBackup(path, backupPath);
        public TournamentWorkspace RecoverFromBackup(string path, string backupPath) => inner.RecoverFromBackup(path, backupPath);
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
