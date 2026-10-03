using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed record DrawExportScope(Guid? ProjectId, string Label, DrawExportState? State)
{
    public bool CanExport => State.HasValue;
}

public sealed record DrawExportSelection(Guid? ProjectId, DrawExportState State, int FormatIndex, int PdfRows, int PdfColumns);

public sealed class DrawExportOptionsViewModel : ViewModelBase
{
    private const string UnavailableAllMessage = "全部导出需要所有项目都已抽签，且确认状态一致。请先导出当前项目。";
    private object? selectedScope;
    private int exportFormatIndex;
    private string pdfRowsText, pdfColumnsText;

    public DrawExportOptionsViewModel(IReadOnlyList<DrawExportScope> scopes, int selectedScopeIndex = 0,
        int exportFormatIndex = 0, string pdfRowsText = "1", string pdfColumnsText = "1")
    {
        ArgumentNullException.ThrowIfNull(scopes);
        Scopes = Array.AsReadOnly(scopes.ToArray());
        selectedScope = selectedScopeIndex >= 0 && selectedScopeIndex < Scopes.Count ? Scopes[selectedScopeIndex] : null;
        this.exportFormatIndex = exportFormatIndex;
        this.pdfRowsText = pdfRowsText;
        this.pdfColumnsText = pdfColumnsText;
    }

    public IReadOnlyList<DrawExportScope> Scopes { get; }
    public object? SelectedScope
    {
        get => selectedScope;
        set { if (SetProperty(ref selectedScope, value)) RefreshValidation(); }
    }
    public IReadOnlyList<string> ExportFormats { get; } = ["Excel 工作簿", "A4 PDF", "PNG 图片", "JPEG 图片", "全部格式"];
    public int ExportFormatIndex
    {
        get => exportFormatIndex;
        set
        {
            if (!SetProperty(ref exportFormatIndex, value)) return;
            OnPropertyChanged(nameof(UsesPdf));
            RefreshValidation();
        }
    }
    public string PdfRowsText
    {
        get => pdfRowsText;
        set { if (SetProperty(ref pdfRowsText, value)) RefreshValidation(); }
    }
    public string PdfColumnsText
    {
        get => pdfColumnsText;
        set { if (SetProperty(ref pdfColumnsText, value)) RefreshValidation(); }
    }
    public bool UsesPdf => ExportFormatIndex is 1 or 4;
    public bool CanExport => ValidationMessage.Length == 0;
    public string ScopeHint => Scopes.Any(scope => scope.ProjectId is null && !scope.CanExport) ? UnavailableAllMessage : "";
    public string StateHint => SnapshotScope?.State switch
    {
        DrawExportState.Preview => "抽签结果（待确认）",
        DrawExportState.Confirmed => "抽签结果（已确认）",
        _ => ""
    };
    public string ValidationMessage
    {
        get
        {
            if (SnapshotScope is not { } scope) return "请选择导出范围。";
            if (!scope.CanExport) return scope.ProjectId is null ? UnavailableAllMessage : "请先完成本项目抽签，再导出结果。";
            if (ExportFormatIndex < 0 || ExportFormatIndex >= ExportFormats.Count) return "请选择导出格式。";
            if (UsesPdf && (!TryPositiveInteger(PdfRowsText, out _) || !TryPositiveInteger(PdfColumnsText, out _)))
                return "PDF 拼页行数和列数应为正整数。";
            return "";
        }
    }

    public bool TryCreateSelection(out DrawExportSelection? selection)
    {
        selection = null;
        if (!CanExport || SnapshotScope is not { State: { } state } scope) return false;
        var rows = 1; var columns = 1;
        if (UsesPdf)
        {
            if (!TryPositiveInteger(PdfRowsText, out rows) || !TryPositiveInteger(PdfColumnsText, out columns)) return false;
        }
        selection = new(scope.ProjectId, state, ExportFormatIndex, rows, columns);
        return true;
    }

    private DrawExportScope? SnapshotScope => SelectedScope is DrawExportScope candidate
        ? Scopes.FirstOrDefault(scope => scope == candidate) : null;

    private static bool TryPositiveInteger(string text, out int value) => int.TryParse(text, out value) && value > 0;

    private void RefreshValidation()
    {
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(StateHint));
    }
}
