using System.Text.Json;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using static V5AcceptanceEvidence;

internal sealed partial class V5AcceptanceScenario
{
    private void PublicDraw()
    {
        var definition = V5AcceptanceRosterFactory.Small(); Create([definition], TournamentPurpose.PublicDrawOnly, false);
        var id = Workspace.Projects.Single().Id;
        Step("explicit-preview-first", () => workflow.PreviewDraw(id, definition.Settings, Revision));
        var first = Workspace.Projects.Single().Draw!.Result;
        Step("explicit-preview-repeat", () => workflow.PreviewDraw(id, definition.Settings, Revision));
        var second = Workspace.Projects.Single().Draw!.Result;
        Require(first.Audit.InputHash == second.Audit.InputHash && JsonSerializer.Serialize(first with { Audit = second.Audit }) == JsonSerializer.Serialize(second), "Seed/input-identical preview is not deterministic.");
        foreach (var state in new[] { DrawExportState.Preview, DrawExportState.Confirmed })
        {
            if (state == DrawExportState.Confirmed) Step("explicit-confirm", () => workflow.ConfirmDraw(id, Revision));
            Step("public-export-" + state, () =>
            {
                var export = workflow.ExportDrawPackage(id, new(evidence.PathFor("materials/public-" + state), WorkflowExportFormat.All, state), Revision);
                Require(export.Outputs.Select(o => o.Format).ToHashSet().IsSupersetOf([WorkflowExportFormat.Excel, WorkflowExportFormat.A4Pdf, WorkflowExportFormat.Png, WorkflowExportFormat.Jpeg]), "Public supported export formats are missing.");
                foreach (var output in export.Outputs.Where(o => o.Format == WorkflowExportFormat.Excel))
                {
                    using var book = new XLWorkbook(output.Path);
                    var texts = book.Worksheets.SelectMany(s => s.CellsUsed().Where(c => !c.HasFormula).Select(c => c.Value.ToString())).ToArray();
                    Require(texts.Any(s => s.Contains(state == DrawExportState.Preview ? "未确认" : "已确认", StringComparison.Ordinal)), "Public export confirmation label is missing.");
                    Require(!texts.Any(s => s.Contains("14:00", StringComparison.Ordinal) || s == "B1"), "Public draw invented schedule time/court.");
                }
            });
        }
        Step("reopen-public-milestone", () => { workflow = new(); workflow.OpenWorkspace(Archive); });
        Require(Workspace.Stage == TournamentStage.DrawsConfirmed && Workspace.Purpose == TournamentPurpose.PublicDrawOnly && Workspace.Schedule is null && Workspace.Results.Count == 0, "Public-only milestone was not durable.");
        V5ArtifactValidator.Snapshot(evidence, "public-draws-confirmed", Archive);
        var graph = JsonSerializer.Serialize(Workspace.Projects); var hash = Hash(Archive); var rev = Revision;
        Step("schedule-before-upgrade-rejected", () =>
        {
            var error = Reject<WorkspaceCommandException>(() => workflow.GenerateSchedule(V5AcceptanceRosterFactory.SmallResources(), V5AcceptanceRosterFactory.Policy(Workspace, false), Revision));
            Require(error.Error.Code == "stage.draw-only" && Hash(Archive) == hash && Revision == rev, "Draw-only scheduling did not safely reject.");
        });
        Step("explicit-upgrade", () => workflow.UpgradeToFullTournament(Revision));
        Require(graph == JsonSerializer.Serialize(Workspace.Projects), "Upgrade changed confirmed draw/graph.");
        Schedule("upgraded-schedule", V5AcceptanceRosterFactory.SmallResources(), false, Workspace.Projects.Sum(p => p.MatchGraph!.Matches.Count));
    }

    private void Single()
    {
        Create([V5AcceptanceRosterFactory.Small()], TournamentPurpose.FullTournament);
        var nodes = Workspace.Projects.Single().MatchGraph!.Matches;
        Require(nodes.Any(n => n.SideA is EntrantSource.LoserOf || n.SideB is EntrantSource.LoserOf), "Small fixture lacks real loser playoff branches.");
        Schedule("single-schedule", V5AcceptanceRosterFactory.SmallResources(), false, nodes.Count);
        ExerciseEditing();
        var package = Package("initial"); var day = Workspace.Resources!.Days[0].Date;
        var expected = V5AcceptanceResults.Fold(Workspace, day); var root = nodes.First(n => n.Dependencies.Count == 0);
        var rootKey = new WorkspaceMatchKey(Workspace.Projects.Single().Id, root.Id);
        var record = package.Outputs.First(o => o.Kind == OperationalMaterialKind.ProjectRecordExcel && o.RecordDay == day).Path;
        var first = V5AcceptanceResults.CopyFill(record, evidence.PathFor("imports/partial.xlsx"), Workspace, expected, new HashSet<WorkspaceMatchKey> { rootKey });
        Import("import-partial", [first]);
        Require(Workspace.Stage == TournamentStage.InProgress && Workspace.Results.Count == 1 && Workspace.ImportLogs.SelectMany(l => l.Rows).Any(r => !r.HadResult), "Partial result did not preserve pending coverage.");
        AssertNoChanges("identical-hash-noop", first);
        var wrong = EditCopy(first, "opposite-winner", (sheet, row) => sheet.Cell(row, 12).Value = expected[rootKey].ChooseA ? "B" : "A", rootKey);
        AssertRejectedImport("opposite-winner-rejected", wrong);
        var correction = EditCopy(first, "duration-correction", (sheet, row) => sheet.Cell(row, 10).Value = 25, rootKey);
        Step("correction-requires-confirmation", () =>
        {
            var hash = Hash(Archive); var rev = Revision; var preview = workflow.PreviewResultImport([correction]);
            Require(preview.Evaluation.Status == ResultImportEvaluationStatus.RequiresConfirmation && preview.Evaluation.Corrections.Count == 1, "Metadata change did not require explicit confirmation.");
            Reject<WorkspaceCommandException>(() => workflow.ImportResults(preview, new(false, null), Revision));
            Require(Hash(Archive) == hash && Revision == rev, "Unconfirmed correction changed archive.");
        });
        Import("confirmed-duration-correction", [correction], true, "核对原始裁判记录，实际用时 25 分钟");
        var history = Workspace.ResultHistory.Single();
        Require(history.Before.DurationMinutes == 20 && history.After.DurationMinutes == 25 && history.Reason == "核对原始裁判记录，实际用时 25 分钟", "Correction history is not exact.");
        expected[rootKey] = expected[rootKey] with { Minutes = 25 };
        AssertNoChanges("old-hash-after-correction-noop", first);
        Require(Workspace.Results[rootKey].DurationMinutes == 25, "Previously imported hash reversed metadata correction.");
        Step("recorded-match-locked", () =>
        {
            var preview = workflow.PreviewMove(new(rootKey, Workspace.Resources!.Days[1].DayLabel, new(14, 0), "B1", workflow.CaptureScheduleEditBaseline()), Revision);
            Require(!preview.CanApply && preview.Violations.Count > 0, "Recorded result placement was movable.");
        });
        WorkspaceBackupOutcome? backup = null;
        Step("manual-checkpoint-backup", () => backup = workflow.CreateBackup(Revision));
        var checkpoint = evidence.PathFor("checkpoint.szbd"); File.Copy(backup!.Backup.FullPath, checkpoint, false);
        var checkpointBusiness = Business(backup.Backup.Workspace);
        evidence.Json("checkpoint.json", new { backup.Backup.FullPath, backup.Backup.ContentHash, Copy = checkpoint, Sha256 = Hash(checkpoint), backup.Backup.Workspace.Revision });
        var target = Workspace.Resources!.Days[1].Date;
        foreach (var key in expected.Keys.Where(k => k != rootKey).ToArray()) expected[key] = expected[key] with { ActualDay = target };
        var walkover = nodes.First(n => n.Dependencies.Count == 0 && n.Id != root.Id);
        var walkoverKey = new WorkspaceMatchKey(rootKey.ProjectId, walkover.Id);
        expected[walkoverKey] = expected[walkoverKey] with { Kind = TournamentResultKind.Walkover, Minutes = 0, Score = "" };
        CompleteCarryover("carry-first", target, expected);
        V5AcceptanceResults.Verify(Workspace, expected, true);
        Step("fresh-reopen-before-restore", () => { workflow = new(); workflow.OpenWorkspace(Archive); });
        var staleBaseline = workflow.CaptureScheduleEditBaseline(); var observedInvalidation = false;
        workflow.SessionChanged += (_, session) =>
        {
            if (session.Workspace.Stage != TournamentStage.InProgress) return;
            // Facade commands are intentionally forbidden inside notifications; inspect availability here,
            // then assert the specific old-token rejection outside the notification boundary below.
            observedInvalidation = !workflow.CanUndoScheduleEdit;
        };
        Step("opaque-healthy-restore", () =>
        {
            var preview = workflow.PreviewRestoreBackup(checkpoint); workflow.RestoreBackup(preview, "验收恢复部分赛果检查点", Revision);
            Require(Business(Workspace) == checkpointBusiness && observedInvalidation, "Restore did not restore business snapshot/invalidate old edit context before observers.");
            Require(Reject<WorkspaceCommandException>(() => workflow.PreviewMove(new(rootKey, target.ToString("yyyy-MM-dd"), new(14, 0), "B1", staleBaseline), Revision)).Error.Code == "schedule.edit-session-changed", "Restore did not rotate the actual editing session token.");
        });
        CompleteCarryover("carry-after-restore", target, expected);
        V5AcceptanceResults.EmitExpected(evidence, "expected-results.csv", expected.Values); Finish(expected);
    }
    private void CompleteCarryover(string name, DateOnly target, IReadOnlyDictionary<WorkspaceMatchKey, V5ExpectedResult> expected)
    {
        var package = Package(name, [target], target); Require(package.Counts.PendingCarryoverCount > 0, "Explicit carryover was a no-op.");
        var pending = expected.Keys.Where(k => !Workspace.Results.ContainsKey(k)).ToHashSet();
        var files = package.Outputs.Where(o => o.Kind == OperationalMaterialKind.ProjectRecordExcel).Select((output, index) =>
        {
            using (var book = new XLWorkbook(output.Path))
            {
                var sheet = book.Worksheet("对阵记录表");
                foreach (var (row, key) in V5AcceptanceResults.Rows(sheet).Where(x => pending.Contains(x.Key)))
                {
                    var plan = Workspace.Schedule!.Placements[key.MatchId];
                    if (plan.DayLabel == target.ToString("yyyy-MM-dd")) continue;
                    Require(sheet.Cell(row, 3).GetString() == "待安排" && sheet.Cell(row, 11).GetString() == "待安排" && sheet.Cell(row, 13).GetString().Contains(plan.DayLabel, StringComparison.Ordinal) && sheet.Cell(row, 13).GetString().Contains(plan.Court, StringComparison.Ordinal) && sheet.Cell(row, 22).IsEmpty(), "Pending carryover invented timing/actual day or lost original plan.");
                }
            }
            return V5AcceptanceResults.CopyFill(output.Path, evidence.PathFor($"imports/{name}-{index}.xlsx"), Workspace, expected, pending);
        }).ToArray();
        Import("import-" + name, files); V5AcceptanceResults.Verify(Workspace, expected, true);
    }
    private void ExerciseEditing()
    {
        var original = Placements(Workspace); var nodes = Workspace.Projects.SelectMany(p => p.MatchGraph!.Matches).ToArray();
        var leaf = nodes.First(n => nodes.All(other => !other.Dependencies.Contains(n.Id)));
        Step("cross-day-move-and-undo", () =>
        {
            var request = new MoveMatchRequest(new(Workspace.Projects.Single().Id, leaf.Id), Workspace.Resources!.Days[1].DayLabel, new(14, 0), "B1", workflow.CaptureScheduleEditBaseline());
            var preview = workflow.PreviewMove(request, Revision); Require(preview.CanApply && preview.HasChanges, "Actual cross-day move did not have legal changes.");
            workflow.MoveMatch(request, Revision); Require(Placements(Workspace) != original, "Move did not publish placement change.");
            workflow.UndoLastScheduleEdit(Revision); Require(Placements(Workspace) == original, "Undo did not restore exact placement map.");
        });
        Step("nontrivial-cascade-and-undo", () =>
        {
            ScheduleEditPreview? selected = null; var attempts = 0;
            foreach (var root in nodes.Where(n => n.Dependencies.Count == 0))
            foreach (var day in Workspace.Resources!.Days.Take(2))
            foreach (var minute in new[] { 14 * 60 + 30, 15 * 60, 16 * 60, 17 * 60 })
            {
                if (selected is not null) break;
                attempts++;
                var preview = workflow.PreviewCascade(new(new(Workspace.Projects.Single().Id, root.Id), day.DayLabel, new(minute / 60, minute % 60), "B1", workflow.CaptureScheduleEditBaseline()), Revision);
                if (preview.CanApply && preview.Changes.Count > 1) selected = preview;
            }
            Require(selected is not null, "Bounded actual cascade search found no multi-change legal preview.");
            evidence.Json("cascade-preview.json", new { Attempts = attempts, selected!.Changes, selected.Violations });
            workflow.CascadeMove(selected, Revision); Require(Placements(Workspace) != original, "Cascade was a no-op.");
            workflow.UndoLastScheduleEdit(Revision); Require(Placements(Workspace) == original, "Cascade undo did not restore placements.");
        });
    }
    private string EditCopy(string source, string name, Action<IXLWorksheet, int> edit, WorkspaceMatchKey key)
    {
        var hash = Hash(source); var path = evidence.PathFor("imports/" + name + ".xlsx"); File.Copy(source, path, false);
        using (var book = new XLWorkbook(path)) { var sheet = book.Worksheet("对阵记录表"); edit(sheet, V5AcceptanceResults.Rows(sheet).Single(r => r.Key == key).Row); book.Save(); }
        Require(Hash(source) == hash, "Import copy source changed."); return path;
    }
    private void AssertNoChanges(string name, string file)
    {
        var hash = Hash(Archive); var business = Business(Workspace); var revision = Revision; var audits = Workspace.AuditEvents.Count; var notifications = 0;
        void Observer(object? _, WorkspaceSession __) => notifications++;
        workflow.SessionChanged += Observer;
        try { Require(Import(name, [file]).NoChanges && hash == Hash(Archive) && Revision == revision && business == Business(Workspace) && audits == Workspace.AuditEvents.Count && notifications == 0, "NoChanges wrote bytes/evidence or notified observers."); }
        finally { workflow.SessionChanged -= Observer; }
    }
    private void AssertRejectedImport(string name, string path) => Step(name, () =>
    {
        var hash = Hash(Archive); var rev = Revision; var preview = workflow.PreviewResultImport([path]);
        Require(preview.Evaluation.Status == ResultImportEvaluationStatus.Rejected && preview.Evaluation.Candidate is null, "Invalid result batch exposed an applicable candidate.");
        evidence.Json(name + "-diagnostics.json", preview.Evaluation.Diagnostics);
        Reject<WorkspaceCommandException>(() => workflow.ImportResults(preview, new(false, null), Revision));
        Require(Hash(Archive) == hash && Revision == rev, "Rejected result import changed archive.");
    });
}
