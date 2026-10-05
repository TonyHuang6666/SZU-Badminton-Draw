using BadmintonDraw.Workflows.Templates;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class TemplateExportViewModel : ViewModelBase, IDisposable
{
    private readonly Func<Task<string?>> outputPicker;
    private readonly Func<IReadOnlyList<string>, Task<bool>> overwriteConfirmation;
    private readonly ITemplateExportWriter writer;
    private bool roster = true, individual = true, team = true, excel = true, pdf = true;
    private bool working, hasError, disposed;
    private string stateMessage = "选择需要的模板，点击导出后选择保存文件夹。";
    private IReadOnlyList<string> outputPaths = Array.Empty<string>();
    private CancellationTokenSource? cancellation;

    public TemplateExportViewModel(Func<Task<string?>> outputPicker,
        Func<IReadOnlyList<string>, Task<bool>> overwriteConfirmation, ITemplateExportWriter? writer = null)
    {
        this.outputPicker = outputPicker;
        this.overwriteConfirmation = overwriteConfirmation;
        this.writer = writer ?? new BlankTemplateExportWriter();
        ExportCommand = new(ExportAsync, CanExport);
    }

    public bool IncludeRoster { get => roster; set { if (SetProperty(ref roster, value)) SelectionChanged(); } }
    public bool IncludeIndividualScoreSheet { get => individual; set { if (SetProperty(ref individual, value)) SelectionChanged(); } }
    public bool IncludeTeamScoreSheet { get => team; set { if (SetProperty(ref team, value)) SelectionChanged(); } }
    public bool IncludeExcel { get => excel; set { if (SetProperty(ref excel, value)) SelectionChanged(); } }
    public bool IncludePdf { get => pdf; set { if (SetProperty(ref pdf, value)) SelectionChanged(); } }
    public bool IsWorking => working;
    public bool IsIdle => !working;
    public bool HasScoreSheets => individual || team;
    public bool HasError { get => hasError; private set => SetProperty(ref hasError, value); }
    public string StateMessage { get => stateMessage; private set => SetProperty(ref stateMessage, value); }
    public IReadOnlyList<string> OutputPaths => outputPaths;
    public bool HasOutputs => outputPaths.Count > 0;
    public string OutputSummary => string.Join(Environment.NewLine, outputPaths);
    public string SelectionSummary => !roster && !HasScoreSheets ? "请至少选择一个模板。"
        : HasScoreSheets && !excel && !pdf ? "请为计分表选择至少一种格式。"
        : $"将导出 {SelectedFiles().Count} 个文件 · 参赛名单固定为 Excel";
    public AsyncCommand ExportCommand { get; }

    private bool CanExport() => !disposed && !working && (roster || HasScoreSheets) && (!HasScoreSheets || excel || pdf);
    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(HasScoreSheets)); OnPropertyChanged(nameof(SelectionSummary));
        ExportCommand.NotifyCanExecuteChanged();
    }

    private List<TemplateFile> SelectedFiles()
    {
        var files = new List<TemplateFile>();
        if (roster) files.Add(new(TemplateExportKind.RosterExcel, "参赛名单.xlsx"));
        if (individual && excel) files.Add(new(TemplateExportKind.IndividualExcel, "空白单场比赛计分表.xlsx"));
        if (individual && pdf) files.Add(new(TemplateExportKind.IndividualPdf, "空白单场比赛计分表.pdf"));
        if (team && excel) files.Add(new(TemplateExportKind.TeamExcel, "空白团体比赛计分表.xlsx"));
        if (team && pdf) files.Add(new(TemplateExportKind.TeamPdf, "空白团体比赛计分表.pdf"));
        return files;
    }

    private void SetOutputs(IReadOnlyList<string> paths)
    {
        outputPaths = paths;
        OnPropertyChanged(nameof(OutputPaths)); OnPropertyChanged(nameof(HasOutputs)); OnPropertyChanged(nameof(OutputSummary));
    }

    private async Task ExportAsync()
    {
        if (!CanExport()) return;
        var files = SelectedFiles();
        using var operation = new CancellationTokenSource();
        cancellation = operation;
        working = true; HasError = false; SetOutputs(Array.Empty<string>());
        OnPropertyChanged(nameof(IsWorking)); OnPropertyChanged(nameof(IsIdle)); ExportCommand.NotifyCanExecuteChanged();
        try
        {
            StateMessage = "请选择保存文件夹。";
            var folder = await outputPicker();
            operation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(folder)) { StateMessage = "已取消导出，模板选择已保留。"; return; }
            folder = Path.GetFullPath(folder);
            var paths = files.Select(file => Path.Combine(folder, file.FileName)).ToArray();
            var conflicts = paths.Where(File.Exists).ToArray();
            if (conflicts.Length > 0)
            {
                StateMessage = "保存文件夹中已有同名文件，请确认是否覆盖。";
                var approved = await overwriteConfirmation(Array.AsReadOnly(conflicts));
                operation.Token.ThrowIfCancellationRequested();
                if (!approved) { StateMessage = "已取消覆盖，未导出文件；模板选择已保留。"; return; }
            }
            StateMessage = "正在生成模板…";
            await Task.Run(() => GenerateAndPublish(folder, files, conflicts, operation.Token), operation.Token);
            SetOutputs(Array.AsReadOnly(paths));
            StateMessage = $"已导出 {paths.Length} 个模板文件。";
        }
        catch (OperationCanceledException) { StateMessage = "已取消导出，模板选择已保留。"; }
        catch (Exception exception) { HasError = true; StateMessage = $"导出失败：{exception.Message}\n模板选择已保留，可修正后重试。"; }
        finally
        {
            cancellation = null; working = false;
            OnPropertyChanged(nameof(IsWorking)); OnPropertyChanged(nameof(IsIdle)); ExportCommand.NotifyCanExecuteChanged();
        }
    }

    private void GenerateAndPublish(string folder, IReadOnlyList<TemplateFile> files, IReadOnlyList<string> approvedPaths, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var approved = new HashSet<string>(approvedPaths, StringComparer.Ordinal);
        CheckDestination(folder, files, approved);
        Directory.CreateDirectory(folder);
        var staging = Path.Combine(folder, $".template-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var retainBackups = false;
        try
        {
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var staged = Path.Combine(staging, file.FileName);
                writer.Write(file.Kind, staged);
                if (!File.Exists(staged) || new FileInfo(staged).Length == 0)
                    throw new IOException($"未能生成有效文件：{file.FileName}");
            }
            token.ThrowIfCancellationRequested();
            CheckDestination(folder, files, approved);
            var published = new List<(string Target, string? Backup)>();
            try
            {
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    var target = Path.Combine(folder, file.FileName);
                    string? backup = null;
                    if (File.Exists(target))
                    {
                        if (!approved.Contains(target)) throw new IOException($"出现未经确认的同名文件，请重新导出：{target}");
                        backup = Path.Combine(staging, $"backup-{file.FileName}");
                        File.Copy(target, backup);
                    }
                    File.Move(Path.Combine(staging, file.FileName), target, overwrite: backup is not null);
                    published.Add((target, backup));
                }
                token.ThrowIfCancellationRequested();
            }
            catch
            {
                foreach (var item in published.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (item.Backup is not null) File.Move(item.Backup, item.Target, overwrite: true);
                        else File.Delete(item.Target);
                    }
                    catch { retainBackups = true; }
                }
                if (retainBackups) throw new IOException($"保存失败，部分文件未能恢复。原文件备份保留在：{staging}");
                throw;
            }
        }
        finally
        {
            if (!retainBackups)
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void CheckDestination(string folder, IReadOnlyList<TemplateFile> files, HashSet<string> approved)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(folder, file.FileName);
            if (Directory.Exists(path)) throw new IOException($"同名文件夹占用了输出路径：{path}");
            if (File.Exists(path) && !approved.Contains(path))
                throw new IOException($"出现未经确认的同名文件，请重新导出：{path}");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; cancellation?.Cancel(); ExportCommand.NotifyCanExecuteChanged();
    }

    private sealed record TemplateFile(TemplateExportKind Kind, string FileName);
}
