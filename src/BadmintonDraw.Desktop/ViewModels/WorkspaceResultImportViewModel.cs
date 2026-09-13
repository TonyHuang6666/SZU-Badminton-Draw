using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed partial class WorkspaceResultImportViewModel : ViewModelBase, IDisposable
{
    private readonly AppShellViewModel shell;
    private readonly Func<Task<IReadOnlyList<string>?>> pickFiles;
    private WorkspaceSession session;
    private WorkspaceResultImportPreview? preview;
    private long generation, sessionGeneration;
    private bool disposed, working, applying, allowCorrections, confirmed;
    private string reason = "", state = "请选择记录表，再预览导入；选择文件不会保存赛果。", source = "", outcome = "";
    public WorkspaceResultImportViewModel(AppShellViewModel shell, WorkspaceSession session,
        Func<Task<IReadOnlyList<string>?>> pickFiles)
    {
        this.shell = shell ?? throw new ArgumentNullException(nameof(shell));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.pickFiles = pickFiles ?? throw new ArgumentNullException(nameof(pickFiles));
        PickFilesCommand = new(PickAsync, () => Usable && !IsWorking && !shell.IsBusy);
        ClearFilesCommand = new(ClearFiles, () => !disposed && SelectedPaths.Count > 0);
        PreviewCommand = new(PreviewAsync, () => Usable && !IsWorking && !shell.IsBusy && SelectedPaths.Count > 0);
        ConfirmImportCommand = new(ImportAsync, CanConfirm);
    }
    public IReadOnlyList<string> SelectedPaths { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<ResultImportFileEvaluation> Files { get; private set; } = Array.Empty<ResultImportFileEvaluation>();
    public IReadOnlyList<ResultImportDiagnostic> Diagnostics { get; private set; } = Array.Empty<ResultImportDiagnostic>();
    public IReadOnlyList<ResultImportCorrection> Corrections { get; private set; } = Array.Empty<ResultImportCorrection>();
    public ResultImportCounts? Counts { get; private set; }
    public ResultImportEvaluationStatus? PreviewStatus { get; private set; }
    public bool HasCurrentPreview => preview is not null;
    public bool AllowCorrections { get => allowCorrections; set { if (SetProperty(ref allowCorrections, value)) ConsentEdited(); } }
    public string CorrectionReason { get => reason; set { if (SetProperty(ref reason, value ?? "")) ConsentEdited(); } }
    public bool Confirmed { get => confirmed; set { if (SetProperty(ref confirmed, value)) { generation++; InputsChangedDuringWork(); RefreshAvailability(); } } }
    public bool IsWorking => working;
    public string SourceDetails { get => source; private set => SetProperty(ref source, value); }
    public string StateMessage { get => state; private set => SetProperty(ref state, value); }
    public string OutcomeDetails { get => outcome; private set => SetProperty(ref outcome, value); }
    public string ConfirmButtonText => PreviewStatus == ResultImportEvaluationStatus.NoChanges ? "确认检查（无需保存）" : "确认导入并保存";
    public AsyncCommand PickFilesCommand { get; }
    public DelegateCommand ClearFilesCommand { get; }
    public AsyncCommand PreviewCommand { get; }
    public AsyncCommand ConfirmImportCommand { get; }
    private bool Usable => !disposed && ReferenceEquals(shell.CurrentSession, session) && !session.RequiresReload &&
        session.Workspace.Purpose == TournamentPurpose.FullTournament && session.Workspace.Stage >= TournamentStage.ScheduleReady;
    private bool CanConfirm() => Usable && !IsWorking && !shell.IsBusy && Confirmed && preview is not null &&
        (PreviewStatus is ResultImportEvaluationStatus.Ready or ResultImportEvaluationStatus.NoChanges ||
         PreviewStatus == ResultImportEvaluationStatus.RequiresConfirmation && AllowCorrections && !string.IsNullOrWhiteSpace(CorrectionReason));
    public void RefreshSession(WorkspaceSession next)
    {
        if (disposed || ReferenceEquals(session, next)) return;
        session = next; sessionGeneration++;
        // Publication revokes preview authorization, but only the actual returned command can identify an own-save refresh.
        if (!applying) generation++;
        ClearEvidence(); StateMessage = "工作区已更新或重新打开；保留文件与原因，请重新预览。";
    }
    private bool Current(long token, WorkspaceSession captured) => !disposed && token == generation && ReferenceEquals(session, captured) && ReferenceEquals(shell.CurrentSession, captured);
    private void ConsentEdited() { generation++; ResetConfirmation(); InputsChangedDuringWork(); RefreshAvailability(); }
    private void InputsChangedDuringWork()
    {
        if (applying) StateMessage = "输入已改变；已发起的导入不会因此回滚，请以当前工作区为准并重新预览。";
        else if (IsWorking) StateMessage = "输入已改变；保留原有内容，请重新选择或预览。文件选择和预览不会提交修改。";
    }
    private void ResetConfirmation() { confirmed = false; OnPropertyChanged(nameof(Confirmed)); }
    private void ClearEvidence()
    {
        preview = null; ResetConfirmation(); Files = Array.Empty<ResultImportFileEvaluation>(); Diagnostics = Array.Empty<ResultImportDiagnostic>();
        Corrections = Array.Empty<ResultImportCorrection>(); Counts = null; PreviewStatus = null; acceptedEvaluation = false; SourceDetails = ""; EvidenceChanged();
    }
    private void Install(ResultImportEvaluation evaluation, bool accepted = false)
    {
        acceptedEvaluation = accepted;
        Files = Array.AsReadOnly(evaluation.Files.ToArray()); Diagnostics = Array.AsReadOnly(evaluation.Diagnostics.ToArray());
        Corrections = Array.AsReadOnly(evaluation.Corrections.ToArray()); Counts = evaluation.ProposedCounts; PreviewStatus = evaluation.Status; EvidenceChanged();
    }
    private void EvidenceChanged()
    {
        foreach (var name in new[] { nameof(Files), nameof(Diagnostics), nameof(Corrections), nameof(Counts), nameof(PreviewStatus), nameof(HasCurrentPreview), nameof(ConfirmButtonText) }) OnPropertyChanged(name);
        PresentationChanged(); RefreshAvailability();
    }
    private static string DescribeSource(WorkspaceResultImportPreview value) => $"工作区：{value.WorkspacePath}\n预览修订：{value.SourceRevision}\n" +
        string.Join("\n\n", value.Files.Select(f => $"{f.SourceFileName}\n{f.FullPath}\nSHA-256：{f.ContentHash}"));
    private static WorkspaceError ToError(Exception error) => error is WorkspaceCommandException command ? command.Error : new("desktop.operation-failed", "操作失败：" + error.Message);
    private void ShowError(WorkspaceError error)
    {
        StateMessage = error.Message + " [" + error.Code + "]" + (error.Committed ? " 文件已保存；重读失败不代表回滚，请检查当前快照或重新载入。" : " 本次没有提交修改。");
        OutcomeDetails = StateMessage + (error.CandidatePath is { } candidate ? "\n候选文件：" + candidate : "") +
            (error.BackupPath is { } backup ? "\n保留的备份路径（不单凭路径证明完整性）：" + backup : "");
    }
    private void SetWorking(bool value) { working = value; if (!disposed) { OnPropertyChanged(nameof(IsWorking)); RefreshAvailability(); } }
    public void RefreshAvailability()
    {
        PickFilesCommand.NotifyCanExecuteChanged(); ClearFilesCommand.NotifyCanExecuteChanged(); PreviewCommand.NotifyCanExecuteChanged(); ConfirmImportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasCurrentPreview));
    }
    public void Dispose() { disposed = true; generation++; preview = null; ResetConfirmation(); RefreshAvailability(); }
}
