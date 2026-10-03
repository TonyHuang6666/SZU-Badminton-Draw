using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;
using static BadmintonDraw.Workflows.Tournaments.WorkspaceExportPublication;

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
        var plan = Plan(workspace, workspacePath, projectId, request);
        var overwrite = OverwritePolicy(request.OverwriteExisting, request.ConfirmedOverwritePaths);
        foreach (var output in plan.Outputs) ValidateDestination(output.Path, workspace, workspacePath, overwrite(output.Path));
        WithStaging(plan.OutputDirectory, progress, stage =>
        {
            foreach (var project in plan.Projects)
            {
                var context = new DrawExportContext(workspace.Id, workspace.Name, workspace.Revision,
                    project.Id, project.DisplayName, project.Draw!.ConfirmedAt,
                    project.Roster!.SourceFileName, project.Roster.ContentHash, auditId, exportedAt,
                    project.Draw.Result.Audit.RandomSeed, project.Draw.Result.Audit.InputHash);
                var excel = Path.Combine(stage, project.Id + ".xlsx");
                new DrawResultExcelWriter().Write(excel, project.Draw.Result, project.Roster.Participants, context: context);
                foreach (var output in plan.Outputs.Where(p => p.ProjectId == project.Id && p.Format != WorkflowExportFormat.Excel))
                    new DrawResultVisualWriter().Write(StagedPath(stage, output), excel, VisualFormat(output.Format),
                        new(plan.Layout.PdfRows, plan.Layout.PdfColumns), context);
            }
            foreach (var output in plan.Outputs)
            {
                // Recheck every actual target immediately before publication, including explicit overwrites.
                Publish(StagedPath(stage, output), output.Path, workspace, workspacePath, overwrite(output.Path), files.Publish);
                progress.Outputs.Add(output);
            }
        });
    }

    internal IReadOnlyList<string> PreviewConflicts(TournamentWorkspace workspace, string workspacePath,
        Guid? projectId, DrawExportRequest request) =>
        Array.AsReadOnly(Plan(workspace, workspacePath, projectId, request).Outputs.Select(p => p.Path).Where(File.Exists).ToArray());

    private sealed record DrawPackagePlan(string OutputDirectory, IReadOnlyList<TournamentProject> Projects,
        IReadOnlyList<DrawPackageOutput> Outputs, DrawExportLayout Layout);

    private static DrawPackagePlan Plan(TournamentWorkspace workspace, string workspacePath, Guid? projectId, DrawExportRequest request)
    {
        if (request is null) throw new WorkspaceCommandException(new("export.request", "请选择导出范围。"));
        Require(Enum.IsDefined(request.Format) && Enum.IsDefined(request.State), "export.format", "请选择支持的抽签导出格式和状态。");
        var layout = request.Layout ?? new();
        Require(layout.PdfRows >= 1 && layout.PdfColumns >= 1,
            "export.layout", "PDF 横向和纵向分页数必须大于零。");
        var projects = workspace.Projects.Where(p => projectId is null || p.Id == projectId).OrderBy(p => p.SortOrder).ToArray();
        Require(projects.Length > 0, "project.not-found", "当前工作区中找不到该项目。");
        foreach (var project in projects)
        {
            Require(project.Draw is not null, "draw.preview-required", $"{project.DisplayName}尚无抽签结果，请先开始公开抽签。");
            Require((project.Draw!.ConfirmedAt is not null) == (request.State == DrawExportState.Confirmed),
                "draw.export-state", $"{project.DisplayName}的确认状态与所选导出不一致，请选择对应的待确认或已确认抽签结果导出。");
        }
        Require(!string.IsNullOrWhiteSpace(request.OutputDirectory), "export.path", "请选择导出文件夹。");
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        var plans = projects.SelectMany(project => WorkflowExportHelpers.Expand(request.Format).Select(format =>
            new DrawPackageOutput(project.Id, format, Path.Combine(outputDirectory,
                FileStem(project, request.State) + WorkflowExportHelpers.GetExtension(format))))).ToArray();
        foreach (var plan in plans) ValidateDestination(plan.Path, workspace, workspacePath, true);
        return new(outputDirectory, projects, plans, layout);
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
            Publish(staged, destination, workspace, workspacePath, overwriteExisting, files.Publish);
            progress.Outputs.Add(new(projectId, WorkflowExportFormat.Excel, destination));
        });
    }

    private static string StagedPath(string stage, DrawPackageOutput output) =>
        Path.Combine(stage, output.ProjectId + WorkflowExportHelpers.GetExtension(output.Format));
    private static string FileStem(TournamentProject project, DrawExportState state)
    {
        var name = WorkflowFileNames.Sanitize(project.DisplayName);
        if (name.Length > 60) name = name[..60];
        return $"{name}_{project.Id:N}_{(state == DrawExportState.Preview ? "待确认抽签结果" : "已确认抽签结果")}";
    }
    private static DrawResultVisualFormat VisualFormat(WorkflowExportFormat format) => format switch
    {
        WorkflowExportFormat.Png => DrawResultVisualFormat.Png,
        WorkflowExportFormat.Jpeg => DrawResultVisualFormat.Jpeg,
        WorkflowExportFormat.A4Pdf => DrawResultVisualFormat.A4Pdf,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
    private static void Require(bool condition, string code, string message)
    {
        if (!condition) throw new WorkspaceCommandException(new(code, message));
    }
    internal sealed class ExportProgress : ExportPublicationProgress
    {
        internal List<DrawPackageOutput> Outputs { get; } = [];
    }
}
