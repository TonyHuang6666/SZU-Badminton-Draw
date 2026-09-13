using BadmintonDraw.Core.Tournaments;

namespace BadmintonDraw.Workflows.Tournaments;

internal class ExportPublicationProgress
{
    internal string? StagingDirectory { get; set; }
}

internal static class WorkspaceExportPublication
{
    internal static void ValidateDestination(string path, TournamentWorkspace workspace, string workspacePath, bool overwrite)
    {
        var name = Path.GetFileName(path);
        // Roster persistence retains filenames, not original absolute paths. Protect matching basenames conservatively.
        if (path.Equals(workspacePath, StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".szbd", StringComparison.OrdinalIgnoreCase) ||
            workspace.Projects.Any(p => p.Roster?.SourceFileName.Equals(name, StringComparison.OrdinalIgnoreCase) == true) ||
            new FileInfo(path).LinkTarget is not null || Directory.Exists(path))
            throw new WorkspaceCommandException(new("export.protected-path", "导出不能覆盖赛事工作区、备份、原始名单、符号链接或文件夹。"));
        if (!overwrite && File.Exists(path))
            throw new WorkspaceCommandException(new("export.exists", "导出文件已存在，请明确确认覆盖或选择其他文件夹：" + path));
    }

    internal static void WithStaging(string outputDirectory, ExportPublicationProgress progress,
        Action<string> writeAndPublish, Action<string>? deleteStagingDirectory = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var sibling = Path.Combine(outputDirectory, ".draw-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sibling);
        progress.StagingDirectory = sibling;
        try { writeAndPublish(sibling); }
        finally
        {
            try
            {
                if (deleteStagingDirectory is null) Directory.Delete(sibling, true);
                else deleteStagingDirectory(sibling);
                progress.StagingDirectory = null;
            }
            catch (IOException) { /* Keep the owned path, without hiding an original failure. */ }
            catch (UnauthorizedAccessException) { /* Caller can recover the reported owned directory. */ }
        }
        if (progress.StagingDirectory is not null)
            throw new WorkspaceCommandException(new("export.cleanup", "导出文件已生成，但无法清理暂存目录：" + sibling));
    }

    internal static void Publish(string stagedPath, string destination, TournamentWorkspace source,
        string workspacePath, bool overwrite, Action<string, string, bool> publish)
    {
        ValidateDestination(destination, source, workspacePath, overwrite);
        publish(stagedPath, destination, overwrite);
    }
}
