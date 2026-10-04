using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class OperationalMaterialNamingTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void Exactly_one_selected_project_uses_project_names_for_all_daily_materials(int projectCount, bool explicitSelection)
    {
        using var f = new WorkspaceOperationalPackageFixture(projects: projectCount, transform: source => source with
        { Projects = source.Projects.Select((p, i) => p with { DisplayName = i == 0 ? "男子单打" : $"其他项目{i}" }).ToArray() });
        var result = f.Workflow.ExportOperationalPackage(new(f.Output,
            explicitSelection ? f.Workspace.Projects[0].Id : null, [WorkspaceOperationalPackageFixture.FirstDay]), f.Workspace.Revision);

        var folder = Path.Combine(f.Output, "9月20日男子单打比赛材料包");
        var expected = new[]
        {
            "男子单打带时间对阵图.xlsx", "男子单打带时间对阵图.pdf",
            "9月20日男子单打赛程安排表.xlsx", "9月20日男子单打赛程安排表.pdf", "9月20日男子单打赛程记录表.xlsx",
            "9月20日男子单打单场比赛计分表.pdf", "材料包说明.txt"
        };
        Assert.Equal(expected.Order(), result.Outputs.Select(o => Path.GetFileName(o.Path)).Order());
        Assert.All(result.Outputs, o => Assert.Equal(folder, Path.GetDirectoryName(o.Path)));
        var description = File.ReadAllText(Path.Combine(folder, "材料包说明.txt"));
        Assert.StartsWith("男子单打比赛材料包", description);
        Assert.All(expected, name => Assert.Contains(name, description));
        Assert.DoesNotContain("合并材料包", description);
        Assert.DoesNotContain("合并赛程", description);
        Assert.DoesNotContain("合并单场比赛", description);
        Assert.DoesNotContain("多项目", description);
        Assert.DoesNotContain("排程检查报告", description);
        Assert.DoesNotContain("材料包校验清单", description);
    }

    [Fact]
    public void Multiple_selected_projects_keep_combined_materials_and_quality_report_without_json_manifest()
    {
        using var f = new WorkspaceOperationalPackageFixture(projects: 2);
        var result = f.Workflow.ExportOperationalPackage(new(f.Output,
            Days: [WorkspaceOperationalPackageFixture.FirstDay]), f.Workspace.Revision);

        Assert.All(result.Outputs, o => Assert.Equal("9月20日多项目合并材料包", Path.GetFileName(Path.GetDirectoryName(o.Path))));
        Assert.Contains(result.Outputs, o => Path.GetFileName(o.Path) == "9月20日合并赛程记录表.xlsx");
        Assert.Contains(result.Outputs, o => Path.GetFileName(o.Path) == "多项目排程检查报告.xlsx");
        var descriptionOutput = Assert.Single(result.Outputs, o => o.Kind == OperationalMaterialKind.Description);
        Assert.Equal("合并材料包说明.txt", Path.GetFileName(descriptionOutput.Path));
        Assert.StartsWith("多项目合并材料包", File.ReadAllText(descriptionOutput.Path));
        Assert.DoesNotContain(result.Outputs, o => o.Kind == OperationalMaterialKind.Manifest || o.Path.EndsWith(".json"));
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(descriptionOutput.Path)!), path => path.EndsWith(".json"));
        Assert.DoesNotContain("材料包校验清单", File.ReadAllText(descriptionOutput.Path));
    }

    [Fact]
    public void Package_collects_daily_materials_under_date_named_folder_and_lists_actual_filenames()
    {
        using var f = new WorkspaceOperationalPackageFixture(projects: 2);
        var result = f.Workflow.ExportOperationalPackage(new(f.Output), f.Workspace.Revision);
        var folder = Path.Combine(f.Output, "9月20日-9月22日多项目合并材料包");
        Assert.All(result.Outputs, o => Assert.Equal(folder, Path.GetDirectoryName(o.Path)));
        Assert.Empty(Directory.GetFiles(f.Output));
        var expected = new[]
        {
            "同名项目带时间对阵图.xlsx", "同名项目带时间对阵图.pdf",
            "同名项目（2）带时间对阵图.xlsx", "同名项目（2）带时间对阵图.pdf",
            "9月20日合并赛程安排表.xlsx", "9月20日合并赛程安排表.pdf", "9月20日合并赛程记录表.xlsx",
            "9月20日合并单场比赛计分表.pdf", "多项目排程检查报告.xlsx", "合并材料包说明.txt"
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
        using var f = new WorkspaceOperationalPackageFixture(projects: 2, transform: source => source with
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
        using var f = new WorkspaceOperationalPackageFixture(projects: 2);
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
