using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class OperationalMaterialNamingTests
{
    [Fact]
    public void Package_collects_daily_materials_under_date_named_folder_and_lists_actual_filenames()
    {
        using var f = new WorkspaceOperationalPackageFixture();
        var result = f.Workflow.ExportOperationalPackage(new(f.Output), f.Workspace.Revision);
        var folder = Path.Combine(f.Output, "9月20日-9月22日多项目合并材料包");
        Assert.All(result.Outputs, o => Assert.Equal(folder, Path.GetDirectoryName(o.Path)));
        Assert.Empty(Directory.GetFiles(f.Output));
        var expected = new[]
        {
            "同名项目带时间对阵图.xlsx", "同名项目带时间对阵图.pdf",
            "9月20日合并赛程安排表.xlsx", "9月20日合并赛程安排表.pdf", "9月20日合并赛程记录表.xlsx",
            "9月20日合并单场比赛计分表.pdf", "多项目排程检查报告.xlsx", "合并材料包说明.txt", "材料包校验清单.json"
        };
        Assert.Equal(expected.Order(), result.Outputs.Select(o => Path.GetFileName(o.Path)).Order());
        var description = File.ReadAllText(Path.Combine(folder, "合并材料包说明.txt"));
        Assert.Contains("同名项目", description);
        Assert.Contains("2026-09-20", description);
        Assert.All(expected, name => Assert.Contains(name, description));
        Assert.DoesNotContain("TimedDrawA4Pdf", description);
    }

    [Fact]
    public void Sanitized_project_collisions_remain_readable_distinct_and_stable_for_selected_project()
    {
        using var f = new WorkspaceOperationalPackageFixture(projects: 3, transform: source => source with
        {
            Projects = source.Projects.Select((p, i) => p with { DisplayName = new[] { "男/双", "男\\双", "男_双（2）" }[i] }).ToArray()
        });
        var all = f.Workflow.ExportOperationalPackage(new(f.Output), f.Workspace.Revision);
        var expected = new[] { "男_双带时间对阵图.xlsx", "男_双（2）带时间对阵图.xlsx", "男_双（2）（2）带时间对阵图.xlsx" };
        Assert.Equal(expected.Order(), all.Outputs.Where(o => o.Kind == OperationalMaterialKind.TimedDrawExcel)
            .Select(o => Path.GetFileName(o.Path)).Order());
        var selected = f.Workflow.ExportOperationalPackage(new(f.Output, f.Workspace.Projects[1].Id,
            [WorkspaceOperationalPackageFixture.FirstDay]), f.Workspace.Revision);
        var draw = Assert.Single(selected.Outputs, o => o.Kind == OperationalMaterialKind.TimedDrawExcel);
        Assert.Equal("男_双（2）带时间对阵图.xlsx", Path.GetFileName(draw.Path));
        Assert.Equal("9月20日男_双（2）比赛材料包", Path.GetFileName(Path.GetDirectoryName(draw.Path)));
    }

    [Fact]
    public void A_project_named_combined_does_not_collide_with_the_combined_record()
    {
        using var f = new WorkspaceOperationalPackageFixture(transform: source => source with
        { Projects = source.Projects.Select(p => p with { DisplayName = "合并" }).ToArray() });
        var result = f.Workflow.ExportOperationalPackage(new(f.Output), f.Workspace.Revision);
        Assert.Contains(result.Outputs, o => Path.GetFileName(o.Path) == "合并带时间对阵图.xlsx");
        var record = Assert.Single(result.Outputs, o => Path.GetFileName(o.Path).EndsWith("赛程记录表.xlsx", StringComparison.Ordinal));
        Assert.Equal("9月20日合并赛程记录表.xlsx", Path.GetFileName(record.Path));
        Assert.Equal(result.Outputs.Count, result.Outputs.Select(o => o.Path).Distinct().Count());
    }

    [Fact]
    public void Package_folder_cannot_redirect_publication_through_a_symlink()
    {
        using var f = new WorkspaceOperationalPackageFixture();
        Directory.CreateDirectory(f.Output);
        var outside = Directory.CreateDirectory(Path.Combine(f.DirectoryPath, "keep")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(f.Output, "9月20日-9月22日多项目合并材料包"), outside);
        var before = WorkspaceOperationalPackageFixture.Hash(f.Archive);
        var error = Assert.Throws<OperationalPackageExportException>(() =>
            f.Workflow.ExportOperationalPackage(new(f.Output, OverwriteExisting: true), f.Workspace.Revision));
        Assert.Equal("export.protected-path", error.Error.Code);
        Assert.Empty(Directory.GetFiles(outside));
        Assert.Equal(before, WorkspaceOperationalPackageFixture.Hash(f.Archive));
    }
}
