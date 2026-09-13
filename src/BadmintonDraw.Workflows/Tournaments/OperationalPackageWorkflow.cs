using System.Security.Cryptography;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Excel;

namespace BadmintonDraw.Workflows.Tournaments;

public sealed partial class OperationalPackageWorkflow(OperationalPackageFileOperations? files = null)
{
    private readonly OperationalPackageFileOperations files = files ?? new();

    internal void Export(TournamentWorkspace source, string workspacePath, OperationalExportRequest request,
        Guid auditId, DateTimeOffset exportedAt, ExportProgress progress)
    {
        var plan = Plan(source, workspacePath, request, progress);
        WorkspaceExportPublication.WithStaging(plan.OutputDirectory, progress, stage =>
        {
            foreach (var material in plan.Materials.Where(m => m.Kind is not OperationalMaterialKind.Description and not OperationalMaterialKind.Manifest))
                Render(Path.Combine(stage, material.FileName), material, plan, stage, request.DrawLayout ?? new(), auditId, exportedAt);
            var description = plan.Materials.Single(m => m.Kind == OperationalMaterialKind.Description);
            File.WriteAllText(Path.Combine(stage, description.FileName), Description(source, auditId, exportedAt, progress));
            var verified = plan.Materials.Where(m => m.Kind != OperationalMaterialKind.Manifest)
                .ToDictionary(m => m.FileName, m => Inspect(stage, m, plan.OutputDirectory));
            var manifest = plan.Materials.Single(m => m.Kind == OperationalMaterialKind.Manifest);
            File.WriteAllText(Path.Combine(stage, manifest.FileName), Manifest(source, plan, auditId, exportedAt, progress, verified.Values));
            // All renderers and the manifest have finished. No target can publish before this complete sweep.
            foreach (var material in plan.Materials)
            {
                var current = Inspect(stage, material, plan.OutputDirectory);
                if (verified.TryGetValue(material.FileName, out var previous))
                    Require(current.ByteLength == previous.ByteLength && current.Sha256 == previous.Sha256,
                        "export.artifact-changed", "暂存材料在校验期间发生变化：" + material.FileName);
                verified[material.FileName] = current;
            }
            foreach (var material in plan.Materials)
            {
                var output = verified[material.FileName];
                progress.AttemptedOutputPath = output.Path;
                WorkspaceExportPublication.Publish(Path.Combine(stage, material.FileName), output.Path, source,
                    workspacePath, request.OverwriteExisting, files.Publish);
                progress.Outputs.Add(output);
                progress.AttemptedOutputPath = null;
            }
        }, files.DeleteStagingDirectory);
    }

    private static void Render(string path, MaterialPlan material, PackagePlan plan, string stage,
        DrawExportLayout layout, Guid auditId, DateTimeOffset exportedAt)
    {
        var source = plan.Context.Workspace;
        DrawExportContext DrawContext(TournamentProject project) => new(source.Id, source.Name, source.Revision,
            project.Id, project.DisplayName, project.Draw!.ConfirmedAt, project.Roster!.SourceFileName,
            project.Roster.ContentHash, auditId, exportedAt, project.Draw.Result.Audit.RandomSeed, project.Draw.Result.Audit.InputHash);
        switch (material.Kind)
        {
            case OperationalMaterialKind.TimedDrawExcel:
                new DrawResultExcelWriter().WriteTimed(path, plan.Context, material.ProjectId!.Value,
                    DrawContext(plan.Projects.Single(p => p.Id == material.ProjectId)));
                break;
            case OperationalMaterialKind.TimedDrawA4Pdf:
                var excel = plan.Materials.Single(m => m.Kind == OperationalMaterialKind.TimedDrawExcel && m.ProjectId == material.ProjectId);
                new DrawResultVisualWriter().Write(path, Path.Combine(stage, excel.FileName), DrawResultVisualFormat.A4Pdf,
                    new(layout.PdfRows, layout.PdfColumns), DrawContext(plan.Projects.Single(p => p.Id == material.ProjectId)));
                break;
            case OperationalMaterialKind.ProjectRecordExcel:
            case OperationalMaterialKind.MergedRecordExcel:
                new WorkspaceMatchRecordWriter().Write(path, plan.Context, material.Rows); break;
            case OperationalMaterialKind.DailyScheduleExcel:
                new ScheduleExcelWriter().WriteDailySchedule(path, plan.Context, material.Rows); break;
            case OperationalMaterialKind.IndividualScorePdf:
                new ScoreSheetExcelWriter().WriteIndividualMatchScorePdf(path, plan.Context, material.Rows); break;
            case OperationalMaterialKind.TeamScoreExcel:
                new ScoreSheetExcelWriter().WriteTeamScoreSheets(path, plan.Context, material.Rows); break;
            case OperationalMaterialKind.QualityExcel:
                new WorkspaceScheduleQualityExcelWriter().Write(path, plan.Context, exportedAt); break;
            default: throw new InvalidOperationException("非可渲染材料类型：" + material.Kind);
        }
    }

    private OperationalPackageOutput Inspect(string stage, MaterialPlan material, string outputDirectory)
    {
        var path = Path.Combine(stage, material.FileName);
        try
        {
            var info = new FileInfo(path);
            Require(info.Exists && info.LinkTarget is null && (info.Attributes & FileAttributes.Directory) == 0 && info.Length > 0,
                "export.artifact-invalid", "必需材料缺失、为空或不是普通文件：" + path);
            using var stream = files.OpenRead(path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920]; long length = 0; int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) { hash.AppendData(buffer, 0, read); length += read; }
            info.Refresh();
            Require(info.Exists && info.LinkTarget is null && length > 0 && info.Length == length,
                "export.artifact-invalid", "必需材料读取不完整或已变化：" + path);
            return new(material.Kind, material.ProjectId, material.RecordDay, Path.Combine(outputDirectory, material.FileName),
                length, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new WorkspaceCommandException(new("export.artifact-invalid", "无法校验必需材料：" + path + "；" + exception.Message), exception); }
    }

    internal sealed class ExportProgress : ExportPublicationProgress
    {
        internal long? SourceRevision { get; set; }
        internal DateTimeOffset? ExportedAt { get; set; }
        internal OperationalPackageScope? Scope { get; set; }
        internal OperationalPackageCounts? Counts { get; set; }
        internal List<OperationalPackageOutput> Outputs { get; } = [];
        internal List<OperationalPackageSkip> Skips { get; } = [];
        internal string? AttemptedOutputPath { get; set; }
    }
}
