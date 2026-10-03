using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceExportConflictTests
{
    [Fact]
    public void Draw_preview_lists_only_exact_existing_targets_across_projects_and_formats_without_writes()
    {
        using var f = new WorkspaceOperationalPackageFixture(projects: 2);
        Directory.CreateDirectory(f.Output);
        var first = DrawPath(f, 0, ".pdf"); var second = DrawPath(f, 1, ".jpg");
        File.WriteAllText(first, "existing pdf"); File.WriteAllText(second, "existing image");
        File.WriteAllText(Path.Combine(f.Output, "unrelated.xlsx"), "unrelated");
        var before = Snapshot(f); var session = f.Workflow.CurrentSession; var notifications = 0;
        f.Workflow.SessionChanged += (_, _) => notifications++;

        var conflicts = f.Workflow.PreviewDrawExportConflicts(null, new(f.Output, WorkflowExportFormat.All), f.Workspace.Revision);

        Assert.Equal(new[] { first, second }, conflicts);
        Assert.Equal(before, Snapshot(f)); Assert.Same(session, f.Workflow.CurrentSession); Assert.Equal(0, notifications);
        Assert.Equal(new[] { second }, f.Workflow.PreviewDrawExportConflicts(f.Workspace.Projects[1].Id,
            new(f.Output, WorkflowExportFormat.All), f.Workspace.Revision));
    }

    [Fact]
    public void Operational_preview_uses_actual_project_day_scope_and_includes_package_metadata_without_writes()
    {
        using var f = new WorkspaceOperationalPackageFixture(projects: 2);
        Directory.CreateDirectory(f.Output);
        var project = f.Workspace.Projects[1].Id;
        var paths = new[] { OperationalPath(f, project, "TimedDrawA4Pdf.pdf"),
            OperationalPath(f, project, "ProjectRecordExcel_2026-09-20.xlsx"),
            OperationalPath(f, f.Workspace.Id, "Manifest.json") };
        foreach (var path in paths) File.WriteAllText(path, "existing");
        File.WriteAllText(OperationalPath(f, f.Workspace.Projects[0].Id, "TimedDrawExcel.xlsx"), "other project");
        File.WriteAllText(OperationalPath(f, project, "ProjectRecordExcel_2026-09-21.xlsx"), "other day");
        var before = Snapshot(f); var session = f.Workflow.CurrentSession; var notifications = 0;
        f.Workflow.SessionChanged += (_, _) => notifications++;

        var conflicts = f.Workflow.PreviewOperationalExportConflicts(new(f.Output, project,
            [WorkspaceOperationalPackageFixture.FirstDay]), f.Workspace.Revision);

        Assert.Equal(paths, conflicts); Assert.Equal(before, Snapshot(f));
        Assert.Same(session, f.Workflow.CurrentSession); Assert.Equal(0, notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preview_without_export_does_not_create_output_directory_or_audit(bool operational)
    {
        using var f = new WorkspaceOperationalPackageFixture(); var before = Snapshot(f);
        Assert.Empty(Preview(f, operational));
        Assert.False(Directory.Exists(f.Output)); Assert.Equal(before, Snapshot(f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Confirmed_paths_allow_only_the_previewed_overwrite_and_normalize_path_segments(bool operational)
    {
        using var f = new WorkspaceOperationalPackageFixture(); Directory.CreateDirectory(f.Output);
        var target = Target(f, operational); File.WriteAllText(target, "existing user output");
        var conflicts = Preview(f, operational);
        Assert.Equal(new[] { target }, conflicts);
        var approved = new[] { Path.Combine(f.Output, ".", Path.GetFileName(target)) };

        Export(f, operational, approved);

        Assert.NotEqual("existing user output", File.ReadAllText(target));
        Assert.Single(f.Workspace.AuditEvents, a => a.Action == (operational ? "OperationalPackageExported" : "DrawPackageExported"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Newly_existing_unapproved_target_after_preview_prevents_all_publication_even_with_legacy_bool(bool operational)
    {
        using var f = new WorkspaceOperationalPackageFixture();
        var approved = Preview(f, operational); Directory.CreateDirectory(f.Output);
        var target = Target(f, operational); File.WriteAllText(target, "new user output"); var hash = WorkspaceOperationalPackageFixture.Hash(f.Archive);

        Assert.Equal("export.exists", ExportError(() => Export(f, operational, approved, overwrite: true), operational));

        Assert.Equal("new user output", File.ReadAllText(target)); Assert.Equal(hash, WorkspaceOperationalPackageFixture.Hash(f.Archive));
        Assert.Single(Directory.GetFiles(f.Output)); Assert.Empty(Directory.GetDirectories(f.Output));
    }

    [Theory]
    [InlineData(false, "symlink")]
    [InlineData(true, "symlink")]
    [InlineData(false, "directory")]
    [InlineData(true, "directory")]
    [InlineData(false, "source")]
    [InlineData(true, "source")]
    public void Preview_and_confirmed_export_reject_protected_targets(bool operational, string protection)
    {
        using var f = new WorkspaceOperationalPackageFixture(); Directory.CreateDirectory(f.Output);
        var target = Target(f, operational);
        if (protection == "symlink") File.CreateSymbolicLink(target, f.Archive);
        else if (protection == "directory") Directory.CreateDirectory(target);
        else
        {
            f.Store.Mutate(f.Archive, f.Workspace.Revision, w => w with
            { Projects = w.Projects.Select(p => p with { Roster = p.Roster! with { SourceFileName = Path.GetFileName(target) } }).ToArray() });
            f.Workflow.OpenWorkspace(f.Archive); File.WriteAllText(target, "original roster");
        }
        var before = Snapshot(f);
        var error = Assert.Throws<WorkspaceCommandException>(() => Preview(f, operational));
        Assert.Equal("export.protected-path", error.Error.Code);
        Assert.Equal(before, Snapshot(f));
        Assert.Equal("export.protected-path", ExportError(() => Export(f, operational, [target]), operational));
        Assert.Equal(before.Single(entry => entry.StartsWith(f.Archive + ":", StringComparison.Ordinal)),
            Snapshot(f).Single(entry => entry.StartsWith(f.Archive + ":", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preview_rejects_stale_revision_without_creating_any_files(bool operational)
    {
        using var f = new WorkspaceOperationalPackageFixture(); var before = Snapshot(f);
        var error = Assert.Throws<WorkspaceCommandException>(() => Preview(f, operational, f.Workspace.Revision - 1));
        Assert.Equal("RevisionConflict", error.Error.Code); Assert.Equal(before, Snapshot(f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preview_rejects_archive_replaced_with_another_identity_at_same_revision(bool operational)
    {
        using var f = new WorkspaceOperationalPackageFixture();
        var replacement = Path.Combine(f.DirectoryPath, "replacement.szbd");
        new TournamentWorkspaceStore().Create(replacement, f.Workspace with { Id = Guid.NewGuid() });
        File.Move(replacement, f.Archive, true); var before = Snapshot(f);
        var error = Assert.Throws<WorkspaceCommandException>(() => Preview(f, operational));
        Assert.Equal("workspace.session-changed", error.Error.Code); Assert.Equal(before, Snapshot(f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Export_rejects_same_identity_archive_content_changed_after_preview(bool operational)
    {
        using var f = new WorkspaceOperationalPackageFixture(); var approved = Preview(f, operational);
        var replacement = Path.Combine(f.DirectoryPath, "replacement.szbd");
        new TournamentWorkspaceStore().Create(replacement, f.Workspace with { Name = "externally changed source" });
        File.Move(replacement, f.Archive, true); var hash = WorkspaceOperationalPackageFixture.Hash(f.Archive);

        Assert.Equal("export.source-changed", ExportError(() => Export(f, operational, approved), operational));

        Assert.Equal(hash, WorkspaceOperationalPackageFixture.Hash(f.Archive)); Assert.False(Directory.Exists(f.Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Per_publication_recheck_refuses_unapproved_late_file_and_reports_prefix_without_retry(bool operational)
    {
        using var f = new WorkspaceOperationalPackageFixture();
        var target = operational ? Target(f, true) : DrawPath(f, 0, ".jpg");
        var drawFiles = new DrawRaceFiles(target); var operationalFiles = new OperationalRaceFiles(target);
        var workflow = new TournamentWorkspaceWorkflow(drawPackages: new(drawFiles), operationalPackages: new(operationalFiles));
        workflow.OpenWorkspace(f.Archive);
        var hash = WorkspaceOperationalPackageFixture.Hash(f.Archive);
        if (operational)
        {
            var approved = workflow.PreviewOperationalExportConflicts(new(f.Output), f.Workspace.Revision);
            var error = Assert.Throws<OperationalPackageExportException>(() => workflow.ExportOperationalPackage(
                new(f.Output, OverwriteExisting: true) { ConfirmedOverwritePaths = approved }, f.Workspace.Revision));
            Assert.Equal("export.exists", error.Error.Code); Assert.Equal(8, error.Outputs.Count);
            Assert.Equal(8, operationalFiles.PublishCalls); Assert.Equal(target, error.AttemptedOutputPath);
        }
        else
        {
            var request = new DrawExportRequest(f.Output, WorkflowExportFormat.All, OverwriteExisting: true);
            var approved = workflow.PreviewDrawExportConflicts(null, request, f.Workspace.Revision);
            var error = Assert.Throws<DrawPackageExportException>(() => workflow.ExportDrawPackage(null,
                request with { ConfirmedOverwritePaths = approved }, f.Workspace.Revision));
            Assert.Equal("export.exists", error.Error.Code); Assert.Single(error.Outputs); Assert.Equal(1, drawFiles.PublishCalls);
        }
        Assert.Equal("late user output", File.ReadAllText(target)); Assert.Equal(hash, WorkspaceOperationalPackageFixture.Hash(f.Archive));
        Assert.Empty(Directory.GetDirectories(f.Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preview_validates_actual_export_state_and_scope(bool operational)
    {
        using var f = new WorkspaceOperationalPackageFixture(); var before = Snapshot(f);
        var error = Assert.Throws<WorkspaceCommandException>(() =>
        {
            if (operational) f.Workflow.PreviewOperationalExportConflicts(new(f.Output, Days: []), f.Workspace.Revision);
            else f.Workflow.PreviewDrawExportConflicts(null, new(f.Output, WorkflowExportFormat.All, DrawExportState.Preview), f.Workspace.Revision);
        });
        Assert.Equal(operational ? "export.days" : "draw.export-state", error.Error.Code); Assert.Equal(before, Snapshot(f));
    }

    private sealed class DrawRaceFiles(string target) : DrawPackageFileOperations
    {
        internal int PublishCalls;
        public override void Publish(string stagedPath, string destination, bool overwrite)
        {
            base.Publish(stagedPath, destination, overwrite);
            if (++PublishCalls == 1) File.WriteAllText(target, "late user output");
        }
    }
    private sealed class OperationalRaceFiles(string target) : OperationalPackageFileOperations
    {
        internal int PublishCalls;
        public override void Publish(string stagedPath, string destination, bool overwrite)
        {
            base.Publish(stagedPath, destination, overwrite);
            if (++PublishCalls == 1) File.WriteAllText(target, "late user output");
        }
    }

    private static string DrawPath(WorkspaceOperationalPackageFixture f, int project, string extension) =>
        Path.Combine(f.Output, $"同名项目_{f.Workspace.Projects[project].Id:N}_已确认抽签结果{extension}");
    private static string OperationalPath(WorkspaceOperationalPackageFixture f, Guid id, string suffix) => Path.Combine(f.Output, $"{id:D}_{suffix}");
    private static string Target(WorkspaceOperationalPackageFixture f, bool operational) => operational
        ? OperationalPath(f, f.Workspace.Id, "Manifest.json") : DrawPath(f, 0, ".xlsx");
    private static IReadOnlyList<string> Preview(WorkspaceOperationalPackageFixture f, bool operational, long? revision = null) => operational
        ? f.Workflow.PreviewOperationalExportConflicts(new(f.Output), revision ?? f.Workspace.Revision)
        : f.Workflow.PreviewDrawExportConflicts(null, new(f.Output, WorkflowExportFormat.Excel), revision ?? f.Workspace.Revision);
    private static void Export(WorkspaceOperationalPackageFixture f, bool operational, IReadOnlyList<string> confirmed, bool overwrite = false)
    {
        if (operational) f.Workflow.ExportOperationalPackage(new(f.Output, OverwriteExisting: overwrite)
            { ConfirmedOverwritePaths = confirmed }, f.Workspace.Revision);
        else f.Workflow.ExportDrawPackage(null, new(f.Output, WorkflowExportFormat.Excel, OverwriteExisting: overwrite)
            { ConfirmedOverwritePaths = confirmed }, f.Workspace.Revision);
    }
    private static string ExportError(Action export, bool operational) => operational
        ? Assert.Throws<OperationalPackageExportException>(export).Error.Code
        : Assert.Throws<DrawPackageExportException>(export).Error.Code;
    private static string[] Snapshot(WorkspaceOperationalPackageFixture f) => Directory.EnumerateFileSystemEntries(f.DirectoryPath, "*", SearchOption.AllDirectories)
        .Order().Select(path => path + ":" + (Directory.Exists(path) ? "directory" : new FileInfo(path).LinkTarget is { } link
            ? "link=" + link : WorkspaceOperationalPackageFixture.Hash(path))).ToArray();
}
