using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows.Tournaments;

/// <summary>Same-filesystem output publication boundary; archive mutation belongs to the workspace facade.</summary>
public class DrawPackageFileOperations
{
    public virtual void Publish(string stagedPath, string destination, bool overwrite) => File.Move(stagedPath, destination, overwrite);
}

/// <summary>Stages all formats before publishing. Only the workspace facade supplies its captured, revision-checked snapshot.</summary>
public sealed class DrawPackageWorkflow(DrawPackageFileOperations? files = null)
{
    private readonly DrawPackageFileOperations files = files ?? new();

    internal void Export(TournamentWorkspace workspace, string workspacePath, Guid? projectId, DrawExportRequest request,
        Guid auditId, DateTimeOffset exportedAt, ExportProgress progress)
    {
        Require(Enum.IsDefined(request.Format) && Enum.IsDefined(request.State), "export.format", "请选择支持的抽签导出格式和状态。");
        var layout = request.Layout ?? new();
        Require(layout.PdfRows >= 1 && layout.PdfColumns >= 1,
            "export.layout", "PDF 横向和纵向分页数必须大于零。");
        var projects = workspace.Projects.Where(p => projectId is null || p.Id == projectId).OrderBy(p => p.SortOrder).ToArray();
        Require(projects.Length > 0, "project.not-found", "当前工作区中找不到该项目。");
        foreach (var project in projects)
        {
            Require(project.Draw is not null, "draw.preview-required", $"{project.DisplayName}尚无抽签，请先主动生成预览。");
            Require((project.Draw!.ConfirmedAt is not null) == (request.State == DrawExportState.Confirmed),
                "draw.export-state", $"{project.DisplayName}的确认状态与所选导出不一致，请选择对应的预览或正式导出。");
        }
        Require(!string.IsNullOrWhiteSpace(request.OutputDirectory), "export.path", "请选择导出文件夹。");
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        var plans = projects.SelectMany(project => WorkflowExportHelpers.Expand(request.Format).Select(format =>
            new DrawPackageOutput(project.Id, format, Path.Combine(outputDirectory,
                FileStem(project, request.State) + WorkflowExportHelpers.GetExtension(format))))).ToArray();
        foreach (var plan in plans) ValidateDestination(plan.Path, workspace, workspacePath, request.OverwriteExisting);
        WithStaging(outputDirectory, progress, stage =>
        {
            foreach (var project in projects)
            {
                var context = new DrawExportContext(workspace.Id, workspace.Name, workspace.Revision,
                    project.Id, project.DisplayName, project.Draw!.ConfirmedAt,
                    project.Roster!.SourceFileName, project.Roster.ContentHash, auditId, exportedAt,
                    project.Draw.Result.Audit.RandomSeed, project.Draw.Result.Audit.InputHash);
                var excel = Path.Combine(stage, project.Id + ".xlsx");
                new DrawResultExcelWriter().Write(excel, project.Draw.Result, project.Roster.Participants, context: context);
                foreach (var plan in plans.Where(p => p.ProjectId == project.Id && p.Format != WorkflowExportFormat.Excel))
                    new DrawResultVisualWriter().Write(StagedPath(stage, plan), excel, VisualFormat(plan.Format),
                        new(layout.PdfRows, layout.PdfColumns), context);
            }
            foreach (var plan in plans)
            {
                // Recheck every actual target immediately before publication, including explicit overwrites.
                ValidateDestination(plan.Path, workspace, workspacePath, request.OverwriteExisting);
                files.Publish(StagedPath(stage, plan), plan.Path, request.OverwriteExisting);
                progress.Outputs.Add(plan);
            }
        });
    }

    internal void ExportTemplate(TournamentWorkspace workspace, string workspacePath, Guid projectId,
        string outputPath, bool overwriteExisting, ExportProgress progress)
    {
        Require(workspace.Projects.Any(p => p.Id == projectId), "project.not-found", "当前工作区中找不到该项目。");
        Require(!string.IsNullOrWhiteSpace(outputPath), "export.path", "请选择模板文件保存位置。");
        var destination = Path.GetFullPath(outputPath);
        ValidateDestination(destination, workspace, workspacePath, overwriteExisting);
        Require(Path.GetExtension(destination).Equals(".xlsx", StringComparison.OrdinalIgnoreCase),
            "export.format", "名单模板必须保存为 .xlsx。");
        WithStaging(Path.GetDirectoryName(destination)!, progress, stage =>
        {
            var staged = Path.Combine(stage, "template.xlsx");
            new ParticipantTemplateWriter().Write(staged);
            ValidateDestination(destination, workspace, workspacePath, overwriteExisting);
            files.Publish(staged, destination, overwriteExisting);
            progress.Outputs.Add(new(projectId, WorkflowExportFormat.Excel, destination));
        });
    }

    private static void WithStaging(string outputDirectory, ExportProgress progress, Action<string> writeAndPublish)
    {
        Directory.CreateDirectory(outputDirectory);
        // Stage beside outputs so publication also works when the selected folder is on another volume.
        var sibling = Path.Combine(outputDirectory, ".draw-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sibling);
        progress.StagingDirectory = sibling;
        try { writeAndPublish(sibling); }
        finally
        {
            try { Directory.Delete(sibling, true); progress.StagingDirectory = null; }
            catch (IOException) { /* Report the owned staging path; never hide the original failure. */ }
            catch (UnauthorizedAccessException) { /* The caller can recover or remove the reported staging directory. */ }
        }
        Require(progress.StagingDirectory is null, "export.cleanup", "导出文件已生成，但无法清理暂存目录：" + sibling);
    }

    private static string StagedPath(string stage, DrawPackageOutput output) =>
        Path.Combine(stage, output.ProjectId + WorkflowExportHelpers.GetExtension(output.Format));
    private static string FileStem(TournamentProject project, DrawExportState state)
    {
        var name = WorkflowFileNames.Sanitize(project.DisplayName);
        if (name.Length > 60) name = name[..60];
        return $"{name}_{project.Id:N}_{(state == DrawExportState.Preview ? "未确认抽签预览" : "已确认抽签结果")}";
    }
    private static DrawResultVisualFormat VisualFormat(WorkflowExportFormat format) => format switch
    {
        WorkflowExportFormat.Png => DrawResultVisualFormat.Png,
        WorkflowExportFormat.Jpeg => DrawResultVisualFormat.Jpeg,
        WorkflowExportFormat.A4Pdf => DrawResultVisualFormat.A4Pdf,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
    private static void ValidateDestination(string path, TournamentWorkspace workspace, string workspacePath, bool overwrite)
    {
        var name = Path.GetFileName(path);
        // Roster persistence retains filenames, not original absolute paths. Protect matching basenames conservatively.
        Require(!path.Equals(workspacePath, StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".szbd", StringComparison.OrdinalIgnoreCase) &&
            !workspace.Projects.Any(p => p.Roster?.SourceFileName.Equals(name, StringComparison.OrdinalIgnoreCase) == true) &&
            new FileInfo(path).LinkTarget is null && !Directory.Exists(path),
            "export.protected-path", "导出不能覆盖赛事工作区、备份、原始名单、符号链接或文件夹。");
        Require(overwrite || !File.Exists(path), "export.exists", "导出文件已存在，请明确确认覆盖或选择其他文件夹：" + path);
    }
    private static void Require(bool condition, string code, string message)
    {
        if (!condition) throw new WorkspaceCommandException(new(code, message));
    }
    internal sealed class ExportProgress
    {
        internal List<DrawPackageOutput> Outputs { get; } = [];
        internal string? StagingDirectory { get; set; }
    }
}
