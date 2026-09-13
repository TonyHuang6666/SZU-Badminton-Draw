using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;
using static BadmintonDraw.Tests.WorkspaceOperationalPackageFixture;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceOperationalPackageFaultTests
{
    [Fact]
    public async Task Queued_export_cannot_follow_an_open_to_another_archive_at_the_same_revision()
    {
        var store = new ObservedStore(new TournamentWorkspaceStore());
        using var f = new WorkspaceOperationalPackageFixture(store: store);
        var before = f.Workspace; var otherPath = Path.Combine(f.DirectoryPath, "other.szbd");
        new TournamentWorkspaceStore().Create(otherPath, before with { Id = Guid.NewGuid(), Name = "other workspace" });
        var oldHash = Hash(f.Archive); var otherHash = Hash(otherPath);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        store.BeforeRead = path => { if (path == otherPath) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        var opening = Task.Run(() => f.Workflow.OpenWorkspace(otherPath));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Exception? failure = null;
        var queued = new Thread(() => { try { f.Workflow.ExportOperationalPackage(new(f.Output), before.Revision); } catch (Exception ex) { failure = ex; } });
        queued.Start();
        var waiting = SpinWait.SpinUntil(() => queued.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(10));
        release.Set(); await opening; Assert.True(queued.Join(TimeSpan.FromSeconds(10))); Assert.True(waiting);
        var error = Assert.IsType<OperationalPackageExportException>(failure);
        Assert.Equal("workspace.session-changed", error.Error.Code); Assert.Equal(0, store.Mutations);
        Assert.False(Directory.Exists(f.Output)); Assert.Equal(oldHash, Hash(f.Archive)); Assert.Equal(otherHash, Hash(otherPath));
    }

    [Fact]
    public async Task Running_package_uses_captured_days_and_source_until_its_single_commit_then_open_can_proceed()
    {
        var store = new ObservedStore(new TournamentWorkspaceStore());
        using var f = new WorkspaceOperationalPackageFixture(store: store);
        var before = f.Workspace; var otherPath = Path.Combine(f.DirectoryPath, "other.szbd");
        new TournamentWorkspaceStore().Create(otherPath, before with { Id = Guid.NewGuid(), Name = "other workspace" });
        var otherHash = Hash(otherPath);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        store.BeforeMutation = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var days = new List<DateOnly> { FirstDay };
        var request = new OperationalExportRequest(f.Output, Days: days);
        var exporting = Task.Run(() => f.Workflow.ExportOperationalPackage(request, before.Revision));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        days.Clear(); days.Add(FirstDay.AddDays(1));
        Exception? openFailure = null;
        var opening = new Thread(() => { try { f.Workflow.OpenWorkspace(otherPath); } catch (Exception ex) { openFailure = ex; } });
        opening.Start();
        var waiting = SpinWait.SpinUntil(() => opening.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(10));
        release.Set(); var result = await exporting; Assert.True(opening.Join(TimeSpan.FromSeconds(10))); Assert.True(waiting);
        Assert.Null(openFailure); Assert.Equal(1, store.Mutations);
        Assert.Equal(before.Id, result.Command.Workspace.Id); Assert.Equal(new[] { FirstDay }, result.Scope.Days);
        Assert.Equal(otherHash, Hash(otherPath)); Assert.Equal(otherPath, f.Workflow.CurrentSession!.WorkspacePath);
        Assert.Single(new TournamentWorkspaceStore().Read(f.Archive).AuditEvents, a => a.Id == result.AuditId);
    }

    [Theory]
    [InlineData("read", "export.artifact-invalid")]
    [InlineData("empty", "export.artifact-invalid")]
    [InlineData("missing", "export.artifact-invalid")]
    [InlineData("changed", "export.artifact-changed")]
    [InlineData("manifest-empty", "export.artifact-invalid")]
    public void Every_real_artifact_is_verified_before_first_publication(string fault, string code)
    {
        var files = new ExportFaultFiles { ReadFault = fault };
        using var f = new WorkspaceOperationalPackageFixture(files: files);
        var hash = Hash(f.Archive);
        var error = Assert.Throws<OperationalPackageExportException>(() => f.Workflow.ExportOperationalPackage(new(f.Output), f.Workspace.Revision));
        Assert.Equal(code, error.Error.Code); Assert.False(error.AuditRecorded); Assert.Empty(error.Outputs);
        Assert.Equal(0, files.PublishCalls); Assert.Equal(hash, Hash(f.Archive));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Output));
        Assert.True(File.Exists(error.Error.CandidatePath));
        Assert.Null(error.AttemptedOutputPath); Assert.Null(error.RetainedStagingDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Publication_failure_keeps_verified_prefix_separate_from_attempted_target(bool throwAfterMove)
    {
        var files = new ExportFaultFiles { FailPublishAt = 2, ThrowAfterMove = throwAfterMove };
        using var f = new WorkspaceOperationalPackageFixture(files: files);
        var hash = Hash(f.Archive);
        var error = Assert.Throws<OperationalPackageExportException>(() => f.Workflow.ExportOperationalPackage(new(f.Output), f.Workspace.Revision));
        Assert.False(error.AuditRecorded); var published = Assert.Single(error.Outputs);
        Assert.Equal(published.Sha256, Hash(published.Path)); Assert.Equal(hash, Hash(f.Archive));
        Assert.NotNull(error.AttemptedOutputPath); Assert.NotEqual(published.Path, error.AttemptedOutputPath);
        Assert.Equal(throwAfterMove, File.Exists(error.AttemptedOutputPath));
        Assert.Equal(throwAfterMove ? 2 : 1, Directory.GetFiles(f.Output).Length);
        Assert.DoesNotContain(new TournamentWorkspaceStore().Read(f.Archive).AuditEvents, a => a.Id == error.AuditId);
        Assert.Null(error.RetainedStagingDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Owned_cleanup_failure_keeps_directory_and_never_masks_original_failure(bool publishFails)
    {
        var files = new ExportFaultFiles { FailCleanup = true, FailPublishAt = publishFails ? 2 : 0 };
        using var f = new WorkspaceOperationalPackageFixture(files: files);
        var hash = Hash(f.Archive);
        var error = Assert.Throws<OperationalPackageExportException>(() => f.Workflow.ExportOperationalPackage(new(f.Output), f.Workspace.Revision));
        Assert.Equal(publishFails ? 1 : 9, error.Outputs.Count); Assert.False(error.AuditRecorded);
        Assert.True(Directory.Exists(error.RetainedStagingDirectory));
        Assert.StartsWith(f.Output + Path.DirectorySeparatorChar, error.RetainedStagingDirectory!);
        Assert.Equal(hash, Hash(f.Archive));
        if (publishFails) Assert.Contains("injected publish", error.Message);
        else { Assert.Equal("export.cleanup", error.Error.Code); Assert.Null(error.AttemptedOutputPath); }
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("directory")]
    [InlineData("symlink")]
    public void Immediate_publication_recheck_preserves_a_late_target_created_after_preflight(string race)
    {
        var files = new ExportFaultFiles(); using var f = new WorkspaceOperationalPackageFixture(files: files);
        var victim = Path.Combine(f.DirectoryPath, "user-data.txt"); File.WriteAllText(victim, "keep this user data");
        var target = Path.Combine(f.Output, $"{f.Workspace.Id:D}_Manifest.json");
        files.AfterAllChecks = () =>
        {
            if (race == "directory") Directory.CreateDirectory(target);
            else if (race == "symlink") File.CreateSymbolicLink(target, victim);
            else File.WriteAllText(target, "late user manifest");
        };
        var error = Assert.Throws<OperationalPackageExportException>(() => f.Workflow.ExportOperationalPackage(
            new(f.Output, OverwriteExisting: race != "existing"), f.Workspace.Revision));
        Assert.Equal(race == "existing" ? "export.exists" : "export.protected-path", error.Error.Code);
        Assert.Equal(8, error.Outputs.Count); Assert.Equal(target, error.AttemptedOutputPath); Assert.False(error.AuditRecorded);
        Assert.Equal("keep this user data", File.ReadAllText(victim));
        if (race == "existing") Assert.Equal("late user manifest", File.ReadAllText(target));
    }

    [Fact]
    public void Existing_files_require_explicit_overwrite_and_protected_roster_basename_still_wins()
    {
        using var f = new WorkspaceOperationalPackageFixture();
        var first = f.Workflow.ExportOperationalPackage(new(f.Output), f.Workspace.Revision);
        var original = first.Outputs.ToDictionary(o => o.Path, o => Hash(o.Path));
        var error = Assert.Throws<OperationalPackageExportException>(() => f.Workflow.ExportOperationalPackage(new(f.Output), f.Workspace.Revision));
        Assert.Equal("export.exists", error.Error.Code); Assert.Empty(error.Outputs);
        Assert.All(original, pair => Assert.Equal(pair.Value, Hash(pair.Key)));
        var overwritten = f.Workflow.ExportOperationalPackage(new(f.Output, OverwriteExisting: true), f.Workspace.Revision);
        Assert.Equal(9, overwritten.Outputs.Count); Assert.True(overwritten.AuditRecorded);
        var protectedName = Path.GetFileName(first.Outputs[0].Path);
        f.Store.Mutate(f.Archive, f.Workspace.Revision, w => w with
        { Projects = w.Projects.Select(p => p with { Roster = p.Roster! with { SourceFileName = protectedName } }).ToArray() });
        f.Workflow.OpenWorkspace(f.Archive);
        error = Assert.Throws<OperationalPackageExportException>(() => f.Workflow.ExportOperationalPackage(new(f.Output, OverwriteExisting: true), f.Workspace.Revision));
        Assert.Equal("export.protected-path", error.Error.Code); Assert.Empty(error.Outputs);
    }

    [Theory]
    [InlineData("candidate-copy", 0)]
    [InlineData("candidate-read", 9)]
    [InlineData("backup", 9)]
    [InlineData("publish", 9)]
    public void Real_SQLite_failure_reports_artifacts_candidate_and_partial_backup_without_audit_claim(string fault, int outputs)
    {
        var files = new ArchiveFaultFiles(); var store = new ArchiveFaultStore(files);
        using var f = new WorkspaceOperationalPackageFixture(store: store);
        var before = f.Workflow.CurrentSession; var hash = Hash(f.Archive);
        files.Fault = fault; store.FailCandidateRead = fault == "candidate-read";
        var error = Assert.Throws<OperationalPackageExportException>(() => f.Workflow.ExportOperationalPackage(new(f.Output), f.Workspace.Revision));
        Assert.False(error.AuditRecorded); Assert.Equal(outputs, error.Outputs.Count);
        Assert.Equal(hash, Hash(f.Archive)); Assert.Same(before, f.Workflow.CurrentSession);
        Assert.True(File.Exists(error.Error.CandidatePath));
        Assert.DoesNotContain(new TournamentWorkspaceStore().Read(f.Archive).AuditEvents, a => a.Id == error.AuditId);
        if (fault is "backup" or "publish") Assert.True(File.Exists(error.Error.BackupPath));
        if (fault == "backup") { Assert.Contains("完整性未验证", error.Message); Assert.Equal("partial backup", File.ReadAllText(error.Error.BackupPath!)); }
        if (fault == "publish") Assert.Equal(hash, Hash(error.Error.BackupPath!));
        Assert.Null(error.AttemptedOutputPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Committed_read_failure_preserves_saved_audit_truth_and_refresh_or_reload_boundary(bool persistent)
    {
        var files = new ArchiveFaultFiles(); var store = new ArchiveFaultStore(files) { PersistentReadFailure = persistent };
        using var f = new WorkspaceOperationalPackageFixture(store: store);
        store.Target = f.Archive; files.Fault = "postcommit"; var before = f.Workspace;
        var error = Assert.Throws<OperationalPackageExportException>(() => f.Workflow.ExportOperationalPackage(new(f.Output), before.Revision));
        Assert.Equal("CommittedReadFailed", error.Error.Code); Assert.True(error.AuditRecorded); Assert.Equal(9, error.Outputs.Count);
        Assert.Equal(persistent, f.Workflow.CurrentSession!.RequiresReload);
        var durable = new TournamentWorkspaceStore().Read(f.Archive);
        Assert.Equal(before.Revision + 1, durable.Revision); Assert.Single(durable.AuditEvents, a => a.Id == error.AuditId);
        Assert.Equal(Business(before), Business(durable));
        if (persistent)
        {
            var next = Assert.Throws<OperationalPackageExportException>(() => f.Workflow.ExportOperationalPackage(new(f.Output), before.Revision));
            Assert.Equal("workspace.reload-required", next.Error.Code); Assert.Empty(next.Outputs);
        }
        else Assert.Equal(durable.Revision, f.Workspace.Revision);
    }

    [Fact]
    public void Observer_failure_cannot_invent_a_rollback()
    {
        using var f = new WorkspaceOperationalPackageFixture();
        f.Workflow.SessionChanged += (_, _) => throw new InvalidOperationException("observer failed");
        var result = f.Workflow.ExportOperationalPackage(new(f.Output), f.Workspace.Revision);
        Assert.True(result.AuditRecorded); Assert.Single(new TournamentWorkspaceStore().Read(f.Archive).AuditEvents, a => a.Id == result.AuditId);
    }

    [Fact]
    public void Same_ID_same_revision_external_replacement_is_rejected_inside_the_one_mutation()
    {
        var store = new ObservedStore(new TournamentWorkspaceStore());
        using var f = new WorkspaceOperationalPackageFixture(store: store); var before = f.Workspace; string? replacementHash = null;
        store.BeforeMutation = () =>
        {
            var replacement = Path.Combine(f.DirectoryPath, "replacement.szbd");
            new TournamentWorkspaceStore().Create(replacement, before with { Name = "same identity but different source" });
            File.Move(replacement, f.Archive, true); replacementHash = Hash(f.Archive);
        };
        var error = Assert.Throws<OperationalPackageExportException>(() => f.Workflow.ExportOperationalPackage(new(f.Output), before.Revision));
        Assert.Equal("export.source-changed", error.Error.Code); Assert.Equal(1, store.Mutations);
        Assert.Equal(replacementHash, Hash(f.Archive)); Assert.Null(error.SourceRevision); Assert.False(Directory.Exists(f.Output));
        Assert.True(File.Exists(error.Error.CandidatePath)); Assert.Empty(error.Outputs);
    }

    [Fact]
    public void Later_real_leaf_layout_failure_leaves_no_published_prefix()
    {
        using var f = new WorkspaceOperationalPackageFixture(transform: source =>
        {
            var court = new string('长', 180);
            var resources = source.Resources! with { Days = source.Resources.Days.Select(d => d with { Courts = [court] }).ToArray() };
            return source with { Resources = resources, Schedule = source.Schedule! with { Resources = resources,
                Placements = source.Schedule.Placements.ToDictionary(p => p.Key, p => p.Value with { Court = court }) } };
        });
        f.Import(f.Record("pending", [new(f.FirstKey, FirstDay)]));
        var hash = Hash(f.Archive);
        var error = Assert.Throws<OperationalPackageExportException>(() => f.Workflow.ExportOperationalPackage(
            new(f.Output, Days: [FirstDay.AddDays(1)], PendingCarryoverDay: FirstDay.AddDays(1)), f.Workspace.Revision));
        Assert.Equal("export.layout", error.Error.Code); Assert.Empty(error.Outputs); Assert.Equal(hash, Hash(f.Archive));
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Output));
    }

    private sealed class ExportFaultFiles : OperationalPackageFileOperations
    {
        internal string? ReadFault;
        internal int FailPublishAt;
        internal bool ThrowAfterMove;
        internal bool FailCleanup;
        internal Action? AfterAllChecks;
        internal int PublishCalls;
        private int reads;
        public override Stream OpenRead(string path)
        {
            reads++;
            // Minimal package has eight real non-manifest artifacts followed by the nine-file sweep.
            if (reads == 9)
            {
                if (ReadFault == "read") throw new IOException("injected real artifact read failure");
                if (ReadFault == "empty") File.WriteAllBytes(path, []);
                if (ReadFault == "missing") File.Delete(path);
                if (ReadFault == "changed") File.AppendAllText(path, "changed staged bytes");
                if (ReadFault == "manifest-empty")
                    File.WriteAllBytes(Directory.GetFiles(Path.GetDirectoryName(path)!, "*_Manifest.json").Single(), []);
            }
            if (reads == 17) AfterAllChecks?.Invoke();
            return base.OpenRead(path);
        }
        public override void Publish(string stagedPath, string destination, bool overwrite)
        {
            PublishCalls++;
            if (PublishCalls == FailPublishAt && !ThrowAfterMove) throw new IOException("injected publish failure");
            base.Publish(stagedPath, destination, overwrite);
            if (PublishCalls == FailPublishAt) throw new IOException("injected publish failure after move");
        }
        public override void DeleteStagingDirectory(string path)
        { if (FailCleanup) throw new IOException("injected cleanup failure"); base.DeleteStagingDirectory(path); }
    }

    private sealed class ArchiveFaultFiles : WorkspaceFileOperations
    {
        internal string? Fault;
        internal bool Published;
        public override void Copy(string source, string destination)
        {
            if ((Fault == "candidate-copy" && destination.EndsWith(".candidate.szbd")) ||
                (Fault == "backup" && destination.EndsWith(".backup.szbd")))
            { File.WriteAllText(destination, Fault == "backup" ? "partial backup" : "partial candidate"); throw new IOException("injected archive copy failure"); }
            base.Copy(source, destination);
        }
        public override void Publish(string candidate, string destination, bool overwrite)
        {
            if (Fault == "publish") throw new IOException("injected archive publish failure");
            base.Publish(candidate, destination, overwrite); if (Fault == "postcommit") Published = true;
        }
    }
    private sealed class ArchiveFaultStore(ArchiveFaultFiles files) : TournamentWorkspaceStore(files)
    {
        internal bool FailCandidateRead;
        internal bool PersistentReadFailure;
        internal string? Target;
        private bool failed;
        public override TournamentWorkspace Read(string path)
        {
            if (FailCandidateRead && path.EndsWith(".candidate.szbd")) throw new WorkspaceStoreException("InvalidWorkspace", "injected candidate read failure");
            if (files.Published && path == Target && (!failed || PersistentReadFailure))
            { failed = true; throw new WorkspaceStoreException("InvalidWorkspace", "injected committed read failure"); }
            return base.Read(path);
        }
    }
    internal sealed class ObservedStore(ITournamentWorkspaceStore inner) : ITournamentWorkspaceStore
    {
        internal Action? BeforeMutation;
        internal Action<string>? BeforeRead;
        internal int Mutations;
        public TournamentWorkspace Create(string path, TournamentWorkspace workspace) => inner.Create(path, workspace);
        public TournamentWorkspace Read(string path) { BeforeRead?.Invoke(path); return inner.Read(path); }
        public WorkspaceMutationResult Mutate(string path, long expectedRevision, Func<TournamentWorkspace, TournamentWorkspace> mutation)
        { Mutations++; var callback = BeforeMutation; BeforeMutation = null; callback?.Invoke(); return inner.Mutate(path, expectedRevision, mutation); }
        public string CreateBackup(string path) => inner.CreateBackup(path);
        public WorkspaceBackupSnapshot InspectBackup(string path) => inner.InspectBackup(path);
        public WorkspaceRecoveryInspection InspectRecovery(string path, string backup) => inner.InspectRecovery(path, backup);
        public WorkspaceMutationResult RestoreBackup(string path, WorkspaceRestoreRequest request) => inner.RestoreBackup(path, request);
        public WorkspaceMutationResult RecoverFromBackup(string path, WorkspaceRecoveryRequest request) => inner.RecoverFromBackup(path, request);
    }
}
