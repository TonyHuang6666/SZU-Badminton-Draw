using System.Collections.ObjectModel;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class PublicDrawPageViewModel : WorkspacePageViewModel
{
    internal AppShellViewModel Shell { get; }
    private readonly Func<Task<string?>> outputPicker;
    private ProjectDrawViewModel? selectedProject;
    private int exportFormatIndex;
    private string pdfRowsText = "1", pdfColumnsText = "1", exportDetails = "";
    private bool overwriteExisting;
    public ObservableCollection<ProjectDrawViewModel> Projects { get; } = [];
    public ProjectDrawViewModel? SelectedProject
    {
        get => selectedProject;
        set { if (SetProperty(ref selectedProject, value)) OverwriteExisting = false; }
    }
    public IReadOnlyList<string> ExportFormats { get; } = ["Excel 工作簿", "A4 PDF", "PNG 图片", "JPEG 图片", "全部格式"];
    public int ExportFormatIndex { get => exportFormatIndex; set { if (SetProperty(ref exportFormatIndex, value)) { OverwriteExisting = false; OnPropertyChanged(nameof(UsesPdf)); } } }
    public bool UsesPdf => ExportFormatIndex is 1 or 4;
    public string PdfRowsText { get => pdfRowsText; set { if (SetProperty(ref pdfRowsText, value)) OverwriteExisting = false; } }
    public string PdfColumnsText { get => pdfColumnsText; set { if (SetProperty(ref pdfColumnsText, value)) OverwriteExisting = false; } }
    public bool OverwriteExisting { get => overwriteExisting; set => SetProperty(ref overwriteExisting, value); }
    public string ExportDetails { get => exportDetails; private set => SetProperty(ref exportDetails, value); }
    public string Readiness => $"{Session.Workspace.Projects.Count(p => p.Draw?.ConfirmedAt is not null)} / {Session.Workspace.Projects.Count} 个项目抽签已确认。每次预览、确认和导出均须主动操作。";
    public bool CanUpgrade => Session.Workspace.Purpose == TournamentPurpose.PublicDrawOnly;
    public AsyncCommand ExportAllPreviewCommand { get; }
    public AsyncCommand ExportAllConfirmedCommand { get; }
    public AsyncCommand UpgradeCommand { get; }

    public PublicDrawPageViewModel(AppShellViewModel shell, WorkspaceSession session, Func<Task<string?>> outputPicker) : base(session)
    {
        Shell = shell; this.outputPicker = outputPicker;
        ExportAllPreviewCommand = new(() => ExportAsync(null, DrawExportState.Preview), () => CanExportAll(false), shell.ReportError);
        ExportAllConfirmedCommand = new(() => ExportAsync(null, DrawExportState.Confirmed), () => CanExportAll(true), shell.ReportError);
        UpgradeCommand = new(async () => await shell.RunWorkspaceCommandAsync(Session, (workflow, revision) => workflow.UpgradeToFullTournament(revision),
            "已升级为完整赛事筹备；已确认抽签保持不变，尚未生成赛程。"), () => shell.CanMutate && CanUpgrade, shell.ReportError);
        RefreshProjects();
    }
    private bool CanExportAll(bool confirmed) => Shell.CanMutate && Projects.Count > 0 &&
        Session.Workspace.Projects.All(p => p.Draw is not null && (p.Draw.ConfirmedAt is not null) == confirmed);
    internal async Task ExportAsync(Guid? projectId, DrawExportState state)
    {
        var expected = Session;
        var format = ExportFormatIndex switch { 0 => WorkflowExportFormat.Excel, 1 => WorkflowExportFormat.A4Pdf, 2 => WorkflowExportFormat.Png,
            3 => WorkflowExportFormat.Jpeg, 4 => WorkflowExportFormat.All, _ => throw new WorkspaceCommandException(new("draw.export-format", "请选择导出格式。")) };
        var rows = 1; var columns = 1;
        if (UsesPdf && (!int.TryParse(PdfRowsText, out rows) || rows < 1 || !int.TryParse(PdfColumnsText, out columns) || columns < 1))
            throw new WorkspaceCommandException(new("draw.export-layout", "PDF 拼页行数和列数应为正整数。"));
        var overwrite = OverwriteExisting; OverwriteExisting = false;
        var output = await outputPicker();
        if (output is null) return;
        var request = new DrawExportRequest(output, format, state, new(rows, columns), overwrite);
        DrawPackageExportResult? result = null; DrawPackageExportException? failure = null;
        var success = await Shell.RunWorkspaceCommandAsync(expected, (workflow, revision) =>
        {
            try { result = workflow.ExportDrawPackage(projectId, request, revision); return result.Command; }
            catch (DrawPackageExportException exception) { failure = exception; throw new WorkspaceCommandException(exception.Error, exception); }
        }, state == DrawExportState.Preview ? "未确认抽签预览已导出，未自动确认抽签。" : "正式抽签材料已导出，导出记录已保存。");
        ExportDetails = success && result is not null ? "已导出，审计记录已保存：" + Environment.NewLine +
            string.Join(Environment.NewLine, result.Outputs.Select(item => item.Path)) : failure is not null
            ? DrawExportFeedback.Describe(failure) : "导出未完成，请查看下方错误详情。";
    }
    public override void RefreshSession(WorkspaceSession next)
    {
        // Destructive consent applies to the snapshot the user reviewed, not a subsequently loaded schedule/draw.
        if (Session.Workspace.Revision != next.Workspace.Revision)
        {
            OverwriteExisting = false;
            foreach (var project in Projects) project.AcknowledgeInvalidation = false;
        }
        base.RefreshSession(next); RefreshProjects();
        OnPropertyChanged(nameof(Readiness)); OnPropertyChanged(nameof(CanUpgrade));
    }
    private void RefreshProjects()
    {
        var selectedId = SelectedProject?.ProjectId;
        var retained = Projects.ToDictionary(p => p.ProjectId);
        var next = Session.Workspace.Projects.OrderBy(p => p.SortOrder).Select(project =>
        {
            if (retained.TryGetValue(project.Id, out var existing)) { existing.Refresh(project); return existing; }
            return new ProjectDrawViewModel(this, project);
        }).ToArray();
        Projects.Clear(); foreach (var item in next) Projects.Add(item);
        SelectedProject = Projects.FirstOrDefault(p => p.ProjectId == selectedId) ?? Projects.FirstOrDefault();
        RefreshAvailability();
    }
    public override void RefreshAvailability()
    {
        foreach (var project in Projects) project.RefreshAvailability();
        ExportAllPreviewCommand?.NotifyCanExecuteChanged(); ExportAllConfirmedCommand?.NotifyCanExecuteChanged(); UpgradeCommand?.NotifyCanExecuteChanged();
    }
}

internal static class DrawExportFeedback
{
    internal static string Describe(DrawPackageExportException failure) => "导出未完整完成。" + Environment.NewLine +
        (failure.Outputs.Count == 0 ? "没有文件成功发布。" : "以下文件已发布，可使用（不代表整个材料包成功）：" + Environment.NewLine +
            string.Join(Environment.NewLine, failure.Outputs.Select(item => item.Path))) + Environment.NewLine +
        (failure.AuditRecorded ? "导出审计已写入，但重新读取失败；请重新载入工作区。" : "本次完整导出审计未写入工作区。") +
        (failure.RetainedStagingDirectory is { } staging ? Environment.NewLine + "保留的临时目录：" + staging : "");
}
