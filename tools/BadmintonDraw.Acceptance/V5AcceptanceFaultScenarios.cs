using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Excel;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using static V5AcceptanceEvidence;

internal sealed class V5AcceptanceFaultScenarios(V5AcceptanceEvidence evidence)
{
    private string checkpoint = "";
    private string resultFile = "";
    private string pendingFile = "";
    private void Case(string name, Action action) => evidence.Phase(name, () => { action(); return new { Assertion = "Observed", Case = name }; });
    internal void Run()
    {
        Case("fault-baseline-public-lifecycle", BuildCheckpoint);
        foreach (var fault in new[] { "candidate-copy", "transaction", "publish", "partial-backup" }) Case("uncommitted-" + fault, () => Uncommitted(fault));
        foreach (var persistent in new[] { false, true }) Case("committed-result-read-" + persistent, () => CommittedRead(persistent, false));
        Case("manual-copy-audit-failure", () => ManualBackup(false)); Case("manual-partial-copy", () => ManualBackup(true));
        Case("package-one-output-then-failure", () => PackageFailure(false)); Case("package-all-outputs-before-audit-failure", () => PackageFailure(true));
        Case("package-committed-read-failure", () => CommittedRead(true, true));
        Case("healthy-stale-revision", StaleRevision); Case("healthy-backup-bytes-replaced", BackupReplaced);
        Case("healthy-same-id-revision-different-source", SameRevisionSource);
        foreach (var corrupt in new[] { false, true }) Case("recovery-invalidates-live-undo-" + corrupt, () => LiveUndoRecovery(corrupt));
        foreach (var change in new[] { "target", "backup", "none" }) Case("corrupt-recovery-" + change, () => CorruptRecovery(change));
        foreach (var invalid in new[] { "boolean", "error", "incoherent-number", "provenance" }) Case("invalid-pending-" + invalid, () => InvalidPending(invalid));
        V5ArtifactValidator.Snapshot(evidence, "fault-baseline-preserved", checkpoint);
    }
    private void BuildCheckpoint()
    {
        var definition = new V5RosterDefinition(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout, 2, 1, "v5-fault-real-two");
        var roster = V5AcceptanceRosterFactory.Write(evidence, definition, 0); var w = new TournamentWorkspaceWorkflow();
        checkpoint = evidence.PathFor("tournament.szbd");
        w.CreateWorkspace(new("故障验收合成赛事", TournamentKind.Individual, TournamentPurpose.FullTournament, [new(definition.Discipline, definition.Mode)], checkpoint));
        var id = w.CurrentSession!.Workspace.Projects.Single().Id;
        w.ImportRoster(id, roster, w.CurrentSession.Workspace.Revision); w.PreviewDraw(id, definition.Settings, w.CurrentSession.Workspace.Revision); w.ConfirmDraw(id, w.CurrentSession.Workspace.Revision);
        w.GenerateSchedule(V5AcceptanceRosterFactory.SmallResources(), V5AcceptanceRosterFactory.Policy(w.CurrentSession.Workspace, false), w.CurrentSession.Workspace.Revision);
        var package = w.ExportOperationalPackage(new(evidence.PathFor("materials/fault-baseline")), w.CurrentSession.Workspace.Revision);
        pendingFile = package.Outputs.Single(o => o.Kind == OperationalMaterialKind.ProjectRecordExcel).Path;
        var expected = V5AcceptanceResults.Fold(w.CurrentSession.Workspace, w.CurrentSession.Workspace.Resources!.Days[0].Date);
        resultFile = V5AcceptanceResults.CopyFill(pendingFile, evidence.PathFor("imports/fault-complete.xlsx"), w.CurrentSession.Workspace, expected);
        evidence.Json("fault-baseline.json", new { Sha256 = Hash(checkpoint), package.AuditId, package.SourceRevision, package.Outputs });
    }
    private string CopyCheckpoint(string name)
    {
        var path = evidence.PathFor("faults/" + name + "/tournament.szbd"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(checkpoint, path, false); return path;
    }
    private static TournamentWorkspaceWorkflow Open(string path, ITournamentWorkspaceStore? store = null, OperationalPackageWorkflow? packages = null)
    { var w = new TournamentWorkspaceWorkflow(store, operationalPackages: packages); w.OpenWorkspace(path); return w; }
    private static long Rev(TournamentWorkspaceWorkflow w) => w.CurrentSession!.Workspace.Revision;
    private static void AssertUnchanged(string path, string hash, WorkspaceSession before, TournamentWorkspaceWorkflow w, int notices)
    {
        Require(Hash(path) == hash && ReferenceEquals(before, w.CurrentSession) && notices == 0, "Uncommitted command changed bytes/session/observers.");
        var fresh = new TournamentWorkspaceStore().Read(path);
        Require(fresh.Revision == before.Workspace.Revision && V5AcceptanceScenario.Business(fresh) == V5AcceptanceScenario.Business(before.Workspace) && fresh.AuditEvents.Count == before.Workspace.AuditEvents.Count,
            "Uncommitted command changed durable business/evidence.");
    }
    private void Uncommitted(string fault)
    {
        var path = CopyCheckpoint(fault); var files = new FaultFiles(); var w = Open(path, new TournamentWorkspaceStore(files));
        var before = w.CurrentSession!; var hash = Hash(path); var notices = 0; w.SessionChanged += (_, _) => notices++;
        var preview = w.PreviewResultImport([resultFile]); files.Fault = fault;
        var error = Reject<WorkspaceCommandException>(() => w.ImportResults(preview, new(false, null), Rev(w))).Error;
        Require(!error.Committed && File.Exists(error.CandidatePath), "Uncommitted failure did not retain its actual candidate.");
        if (fault is "publish" or "partial-backup") Require(File.Exists(error.BackupPath), "Actual prepublish backup path was lost.");
        if (fault == "partial-backup") Require(File.ReadAllText(error.BackupPath!) == "partial acceptance backup" && error.Message.Contains("完整性未验证", StringComparison.Ordinal), "Partial backup was mislabeled/removed.");
        if (fault == "publish") Require(Hash(error.BackupPath!) == hash, "Atomic-publish failure backup differs from source.");
        AssertUnchanged(path, hash, before, w, notices); evidence.Json("fault-" + fault + ".json", new { error, BeforeSha256 = hash, AfterSha256 = Hash(path), notices });
    }
    private void CommittedRead(bool persistent, bool package)
    {
        var name = (package ? "package" : "result") + "-committed-" + persistent; var path = CopyCheckpoint(name);
        var files = new FaultFiles(); var store = new ReadFaultStore(files, path, persistent); var w = Open(path, store);
        var before = w.CurrentSession!.Workspace; var hash = Hash(path); var notices = 0; w.SessionChanged += (_, _) => notices++;
        WorkspaceError error; Guid? auditId = null; IReadOnlyList<OperationalPackageOutput>? outputs = null;
        if (package)
        {
            files.Fault = "postcommit";
            var failure = Reject<OperationalPackageExportException>(() => w.ExportOperationalPackage(new(Path.Combine(Path.GetDirectoryName(path)!, "materials")), Rev(w)));
            error = failure.Error; auditId = failure.AuditId; outputs = failure.Outputs;
            Require(failure.AuditRecorded && outputs.Count > 0 && outputs.All(o => Hash(o.Path).Equals(o.Sha256, StringComparison.OrdinalIgnoreCase)), "Committed package files/audit claim is false.");
        }
        else
        {
            var preview = w.PreviewResultImport([resultFile]); files.Fault = "postcommit";
            error = Reject<WorkspaceCommandException>(() => w.ImportResults(preview, new(false, null), Rev(w))).Error;
        }
        var durable = new TournamentWorkspaceStore().Read(path);
        Require(error.Committed && error.Code == "CommittedReadFailed" && Hash(error.BackupPath!) == hash && notices == 1 && w.CurrentSession!.RequiresReload == persistent && durable.Revision == before.Revision + 1, "Committed/read-refresh boundary is not truthful.");
        if (package) Require(durable.AuditEvents.Count(a => a.Id == auditId) == 1, "Package audit missing after committed error.");
        else Require(durable.Stage == TournamentStage.Completed && durable.Results.Count == 1 && durable.ImportLogs.Count == 1 && durable.AuditEvents.Count(a => a.Action == "ResultsImported") == 1, "Result/evidence not durable after committed error.");
        if (persistent) Require(Reject<WorkspaceCommandException>(() => w.PreviewResultImport([resultFile])).Error.Code == "workspace.reload-required", "Persistent read failure did not guard next ordinary command.");
        else Require(Rev(w) == durable.Revision, "Transient read failure did not refresh durable session.");
        evidence.Json("fault-" + name + ".json", new { error, outputs, auditId, notices, w.CurrentSession!.RequiresReload, durable.Revision, durable.Stage, durable.AuditEvents });
    }
    private void ManualBackup(bool partial)
    {
        var name = partial ? "manual-partial" : "manual-audit-failure"; var path = CopyCheckpoint(name);
        var files = new FaultFiles { Fault = partial ? "partial-backup" : "publish" }; var w = Open(path, new TournamentWorkspaceStore(files));
        var hash = Hash(path); var error = Reject<WorkspaceBackupException>(() => w.CreateBackup(Rev(w)));
        Require(!error.Error.Committed && Hash(path) == hash && File.Exists(error.ManualBackupPath), "Manual failure lost original or actual copy.");
        if (partial) Require(error.Backup is null && File.ReadAllText(error.ManualBackupPath!) == "partial acceptance backup", "Partial manual copy claimed verified backup.");
        else Require(error.Backup is not null && Hash(error.Backup.FullPath) == hash && error.ManualBackupPath == error.Backup.FullPath && error.Error.BackupPath != error.ManualBackupPath && File.Exists(error.Error.CandidatePath) && File.Exists(error.Error.BackupPath), "Verified manual backup/audit-save paths were conflated.");
        evidence.Json("fault-" + name + ".json", new { error.Error, error.ManualBackupPath, VerifiedBackup = error.Backup is null ? null : new { error.Backup.FullPath, error.Backup.ContentHash }, BeforeSha256 = hash, AfterSha256 = Hash(path) });
    }
    private void PackageFailure(bool all)
    {
        var name = all ? "package-all" : "package-one"; var path = CopyCheckpoint(name); var archiveFiles = new FaultFiles { Fault = all ? "publish" : null };
        var outputFiles = new PackageFaultFiles { FailAt = all ? 0 : 2, FailCleanup = !all };
        var w = Open(path, new TournamentWorkspaceStore(archiveFiles), new OperationalPackageWorkflow(outputFiles)); var hash = Hash(path);
        var failure = Reject<OperationalPackageExportException>(() => w.ExportOperationalPackage(new(Path.Combine(Path.GetDirectoryName(path)!, "materials")), Rev(w)));
        Require(!failure.AuditRecorded && Hash(path) == hash && (all ? failure.Outputs.Count > 1 : failure.Outputs.Count == 1) && failure.Outputs.All(o => Hash(o.Path).Equals(o.Sha256, StringComparison.OrdinalIgnoreCase)), "Partial real package publication evidence is false.");
        Require(!new TournamentWorkspaceStore().Read(path).AuditEvents.Any(a => a.Id == failure.AuditId), "Failed audit save claimed package audit.");
        if (!all) Require(failure.AttemptedOutputPath is not null && !File.Exists(failure.AttemptedOutputPath) && Directory.Exists(failure.RetainedStagingDirectory), "Attempted target/staging not truthfully retained.");
        else Require(File.Exists(failure.Error.CandidatePath) && File.Exists(failure.Error.BackupPath) && failure.Outputs.Any(o => o.Kind == OperationalMaterialKind.Manifest), "All-files-before-audit-save boundary not reached.");
        evidence.Json("fault-" + name + ".json", new { failure.Error, failure.AuditId, failure.Outputs, failure.AttemptedOutputPath, failure.RetainedStagingDirectory, BeforeSha256 = hash, AfterSha256 = Hash(path) });
    }
    private void StaleRevision()
    {
        var path = CopyCheckpoint("stale-revision"); var w = Open(path); var preview = w.PreviewRestoreBackup(checkpoint);
        w.CreateBackup(Rev(w)); var hash = Hash(path); var rev = Rev(w);
        var error = Reject<WorkspaceCommandException>(() => w.RestoreBackup(preview, "过期预览必须拒绝", rev)).Error;
        Require(!error.Committed && Hash(path) == hash && Rev(w) == rev, "Stale restore preview changed source."); evidence.Json("fault-stale-revision.json", error);
    }
    private void BackupReplaced()
    {
        var path = CopyCheckpoint("backup-replaced"); var backup = Path.Combine(Path.GetDirectoryName(path)!, "backup.szbd"); File.Copy(checkpoint, backup, false);
        var w = Open(path); var preview = w.PreviewRestoreBackup(backup); var other = Open(backup); other.CreateBackup(Rev(other)); var hash = Hash(path);
        var error = Reject<WorkspaceCommandException>(() => w.RestoreBackup(preview, "备份已更换", Rev(w))).Error;
        Require(!error.Committed && Hash(path) == hash, "Replaced backup was accepted."); evidence.Json("fault-backup-replaced.json", error);
    }
    private void SameRevisionSource()
    {
        var a = CopyCheckpoint("same-revision-a"); var b = CopyCheckpoint("same-revision-b"); var wa = Open(a); var wb = Open(b);
        wa.CreateBackup(Rev(wa)); wb.CreateBackup(Rev(wb));
        Require(Rev(wa) == Rev(wb) && wa.CurrentSession!.Workspace.Id == wb.CurrentSession!.Workspace.Id && Hash(a) != Hash(b), "Independent public mutations did not create equal-revision distinct source evidence.");
        var preview = wa.PreviewRestoreBackup(checkpoint); File.Copy(b, a, true); var hash = Hash(a);
        var error = Reject<WorkspaceCommandException>(() => wa.RestoreBackup(preview, "同修订来源已变化", Rev(wa))).Error;
        Require(error.Code == "recovery.source-changed" && Hash(a) == hash && !error.Committed, "Full same-ID/revision source binding failed."); evidence.Json("fault-same-revision-source.json", error);
    }
    private void CorruptRecovery(string change)
    {
        var path = CopyCheckpoint("corrupt-" + change); var backup = Path.Combine(Path.GetDirectoryName(path)!, "backup.szbd"); File.Copy(checkpoint, backup, false);
        File.WriteAllText(path, "exact corrupt acceptance bytes \0 " + change); var hash = Hash(path); var w = new TournamentWorkspaceWorkflow();
        Reject<WorkspaceCommandException>(() => w.OpenWorkspace(path)); Require(w.CurrentSession is null, "Corrupt open yielded a usable session.");
        var preview = w.PreviewRecovery(path, backup);
        if (change == "target") File.AppendAllText(path, " changed after preview");
        if (change == "backup") { var other = Open(backup); other.CreateBackup(Rev(other)); }
        if (change != "none")
        {
            var changedHash = Hash(path); var error = Reject<WorkspaceCommandException>(() => w.RecoverFromBackup(preview, "更换后应拒绝")).Error;
            Require(!error.Committed && Hash(path) == changedHash && w.CurrentSession is null, "Changed corrupt target/backup was accepted."); evidence.Json("fault-corrupt-" + change + ".json", error); return;
        }
        var notices = 0; w.SessionChanged += (_, _) => { Require(!w.CanUndoScheduleEdit, "Recovery observer saw stale undo."); notices++; };
        var result = w.RecoverFromBackup(preview, "确认恢复合成故障目标");
        Require(result.BackupPath is not null && Hash(result.BackupPath) == hash && notices == 1 && w.CurrentSession is { RequiresReload: false }, "Recovery did not preserve exact old corrupt bytes/adopt new session.");
        Require(new TournamentWorkspaceStore().Read(path).AuditEvents.Any(a => a.Action == "WorkspaceRecovered"), "Successful recovery audit absent.");
        evidence.Json("fault-corrupt-recovered.json", new { result.BackupPath, OriginalCorruptSha256 = hash, BackupSha256 = Hash(result.BackupPath!), notices });
    }
    private void LiveUndoRecovery(bool corrupt)
    {
        var path = CopyCheckpoint("live-undo-" + corrupt); var w = Open(path);
        var source = w.CurrentSession!.Workspace; var node = source.Projects.Single().MatchGraph!.Matches.Single();
        var request = new MoveMatchRequest(new(node.ProjectId, node.Id), source.Resources!.Days[1].DayLabel, new(14, 0), "B1", w.CaptureScheduleEditBaseline());
        w.MoveMatch(request, Rev(w)); Require(w.CanUndoScheduleEdit, "Recovery undo test did not first create a real non-no-op live edit.");
        var stale = request with { Baseline = w.CaptureScheduleEditBaseline() }; var notified = false;
        w.SessionChanged += (_, _) => { Require(!w.CanUndoScheduleEdit, "Recovery observer saw preexisting live undo."); notified = true; };
        if (corrupt)
        {
            File.WriteAllText(path, "corrupt a previously open edit session");
            w.RecoverFromBackup(w.PreviewRecovery(path, checkpoint), "恢复并废弃原编辑上下文");
        }
        else w.RestoreBackup(w.PreviewRestoreBackup(checkpoint), "恢复并废弃原编辑上下文", Rev(w));
        Require(notified && !w.CanUndoScheduleEdit && Reject<WorkspaceCommandException>(() => w.PreviewMove(stale, Rev(w))).Error.Code == "schedule.edit-session-changed", "Recovery did not invalidate actual undo/token.");
        Require(w.CurrentSession!.Workspace.Schedule!.Placements[node.Id] == source.Schedule!.Placements[node.Id], "Recovery retained moved placement instead of checkpoint.");
        evidence.Json("fault-live-undo-" + corrupt + ".json", new { HadLiveUndoBefore = true, ObserverSawUndo = false, OldTokenError = "schedule.edit-session-changed", w.CurrentSession.Workspace.Revision });
    }
    private void InvalidPending(string kind)
    {
        var path = CopyCheckpoint("invalid-" + kind); var w = Open(path); var file = Path.Combine(Path.GetDirectoryName(path)!, "rejected-input.xlsx"); File.Copy(pendingFile, file, false);
        using (var book = new XLWorkbook(file))
        {
            var sheet = book.Worksheet("对阵记录表"); var row = V5AcceptanceResults.Rows(sheet)[0].Row;
            if (kind == "boolean") sheet.Cell(row, 9).Value = true;
            else if (kind == "error") sheet.Cell(row, 9).Value = XLError.DivisionByZero;
            else if (kind == "incoherent-number") sheet.Cell(row, 9).Value = 21;
            else sheet.Cell(row, 19).Value = "tampered-graph-revision";
            book.Save();
        }
        if (kind == "incoherent-number")
        {
            using var zip = ZipFile.Open(file, ZipArchiveMode.Update); var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!; XDocument document;
            using (var stream = entry.Open()) document = XDocument.Load(stream);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            document.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "I6").Element(ns + "v")!.Value = "not-a-finite-number";
            entry.Delete(); using var replacement = zip.CreateEntry("xl/worksheets/sheet1.xml").Open(); document.Save(replacement);
        }
        var hash = Hash(path); var preview = w.PreviewResultImport([file]);
        evidence.Json("invalid-" + kind + "-raw-evaluation.json", new { Raw = new WorkspaceMatchRecordReader().ReadWorkspaceRecord(File.ReadAllBytes(file)), preview.Evaluation.Status, preview.Evaluation.Diagnostics, preview.Evaluation.ProposedCounts, CandidatePresent = preview.Evaluation.Candidate is not null });
        Require(preview.Evaluation.Status == ResultImportEvaluationStatus.Rejected && preview.Evaluation.Candidate is null, "Invalid actual pending/provenance cell did not block whole batch.");
        Reject<WorkspaceCommandException>(() => w.ImportResults(preview, new(false, null), Rev(w))); Require(Hash(path) == hash, "Rejected malformed record changed source.");
        CreateText(file + ".expected-rejection.json", JsonSerializer.Serialize(new { Rejected = true, Sha256 = Hash(file), preview.Evaluation.Diagnostics }));
    }
    private sealed class FaultFiles : WorkspaceFileOperations
    {
        internal string? Fault; internal bool Published;
        public override void Copy(string source, string destination)
        {
            if (Fault == "candidate-copy" && destination.EndsWith(".candidate.szbd", StringComparison.Ordinal)) { File.WriteAllText(destination, "partial candidate"); throw new IOException("injected candidate copy failure"); }
            if (Fault == "partial-backup" && destination.EndsWith(".backup.szbd", StringComparison.Ordinal)) { File.WriteAllText(destination, "partial acceptance backup"); throw new IOException("injected partial backup copy failure"); }
            base.Copy(source, destination);
            if (Fault == "transaction" && destination.EndsWith(".candidate.szbd", StringComparison.Ordinal))
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()); connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "CREATE TRIGGER acceptance_transaction_failure BEFORE DELETE ON audit_events BEGIN SELECT RAISE(ABORT, 'injected real transaction failure'); END;"; command.ExecuteNonQuery();
            }
        }
        public override void Publish(string candidate, string destination, bool overwrite)
        { if (Fault == "publish") throw new IOException("injected archive atomic publication failure"); base.Publish(candidate, destination, overwrite); if (Fault == "postcommit") Published = true; }
    }
    private sealed class ReadFaultStore(FaultFiles files, string target, bool persistent) : TournamentWorkspaceStore(files)
    {
        private bool failed;
        public override TournamentWorkspace Read(string path)
        { if (files.Published && path == target && (!failed || persistent)) { failed = true; throw new WorkspaceStoreException("InvalidWorkspace", "injected post-publication read failure"); } return base.Read(path); }
    }
    private sealed class PackageFaultFiles : OperationalPackageFileOperations
    {
        internal int FailAt; internal bool FailCleanup; private int publishes;
        public override void Publish(string source, string destination, bool overwrite)
        { publishes++; if (publishes == FailAt) throw new IOException("injected real output publication failure"); base.Publish(source, destination, overwrite); }
        public override void DeleteStagingDirectory(string path)
        { if (FailCleanup) throw new IOException("injected owned staging cleanup failure"); base.DeleteStagingDirectory(path); }
    }
}
