using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

/// <summary>Shell lifetime, independent of route and of whether a workspace can be read.</summary>
public sealed class WorkspaceRecoveryViewModel : ViewModelBase, IDisposable
{
    private readonly AppShellViewModel shell;
    private readonly Func<Task<string?>> targetPicker, backupPicker;
    private WorkspaceSession? context, previewSession;
    private WorkspaceRestorePreview? restorePreview;
    private WorkspaceRecoveryPreview? recoveryPreview;
    private int generation;
    private bool isOpen, disposed, picking, recoverCorruptTarget, confirmed;
    private string targetPath = "", backupPath = "", reason = "", previewDetails = "尚未预览；选择来源不会自动恢复。", outputDetails = "";
    public bool IsOpen { get => isOpen; private set => SetProperty(ref isOpen, value); }
    public bool RecoverCorruptTarget { get => recoverCorruptTarget; set { if (SetProperty(ref recoverCorruptTarget, value)) InvalidatePreview(); } }
    public string TargetPath { get => targetPath; set { if (SetProperty(ref targetPath, value)) InvalidatePreview(); } }
    public string BackupPath { get => backupPath; set { if (SetProperty(ref backupPath, value)) InvalidatePreview(); } }
    public string Reason { get => reason; set { if (SetProperty(ref reason, value)) { Confirmed = false; RefreshAvailability(); } } }
    public bool Confirmed { get => confirmed; set { if (SetProperty(ref confirmed, value)) RefreshAvailability(); } }
    public string CurrentWorkspaceDetails => context is null ? "尚未打开工作区；可选择损坏目标进行恢复。" :
        $"当前工作区：{context.Workspace.Name}\n{context.WorkspacePath}\n修订 {context.Workspace.Revision} · {AppShellViewModel.StageName(context.Workspace.Stage)}" +
        (context.RequiresReload ? "\n已保存但无法重读；普通备份/恢复须先重新载入。仍可检查明确选定的损坏目标。" : "");
    public bool HasPreview => restorePreview is not null || recoveryPreview is not null;
    public string PreviewDetails { get => previewDetails; private set => SetProperty(ref previewDetails, value); }
    public string OutputDetails { get => outputDetails; private set => SetProperty(ref outputDetails, value); }
    public string? LastManualBackupPath { get; private set; }
    public WorkspaceBackupInfo? LastValidatedBackup { get; private set; }
    public AsyncCommand PickTargetCommand { get; }
    public AsyncCommand PickBackupCommand { get; }
    public AsyncCommand PreviewCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public AsyncCommand CreateBackupCommand { get; }
    public DelegateCommand CloseCommand { get; }

    public WorkspaceRecoveryViewModel(AppShellViewModel shell, Func<Task<string?>> targetPicker, Func<Task<string?>> backupPicker)
    {
        this.shell = shell; this.targetPicker = targetPicker; this.backupPicker = backupPicker;
        PickTargetCommand = new(() => PickAsync(true), Available, shell.ReportError);
        PickBackupCommand = new(() => PickAsync(false), Available, shell.ReportError);
        PreviewCommand = new(PreviewAsync, () => Available() && !string.IsNullOrWhiteSpace(BackupPath) &&
            (RecoverCorruptTarget ? !string.IsNullOrWhiteSpace(TargetPath) : shell.CanMutate), shell.ReportError);
        RestoreCommand = new(RestoreAsync, () => Available() && HasPreview && Confirmed && !string.IsNullOrWhiteSpace(Reason) &&
            (RecoverCorruptTarget || shell.CanMutate), shell.ReportError);
        CreateBackupCommand = new(CreateBackupAsync, () => Available() && shell.CanMutate, shell.ReportError);
        CloseCommand = new(Close, () => !disposed);
    }
    private bool Available() => !disposed && IsOpen && !picking && !shell.IsBusy;
    public void Open() { if (disposed) return; RefreshContext(); IsOpen = true; RefreshAvailability(); }
    public void Close() { IsOpen = false; InvalidatePreview(); }
    public void RefreshContext()
    {
        if (ReferenceEquals(context, shell.CurrentSession)) return;
        context = shell.CurrentSession; InvalidatePreview(); OnPropertyChanged(nameof(CurrentWorkspaceDetails));
    }
    private void InvalidatePreview()
    {
        generation++; restorePreview = null; recoveryPreview = null; previewSession = null; Confirmed = false;
        PreviewDetails = "尚无有效恢复预览；请重新检查来源并明确确认。";
        OnPropertyChanged(nameof(HasPreview)); RefreshAvailability();
    }
    private bool IsCurrent(int captured, WorkspaceSession? session) => !disposed && IsOpen && generation == captured && ReferenceEquals(shell.CurrentSession, session);
    private async Task PickAsync(bool target)
    {
        var captured = generation; var session = shell.CurrentSession; picking = true; RefreshAvailability();
        try
        {
            var selected = await (target ? targetPicker : backupPicker)();
            if (selected is not null && IsCurrent(captured, session))
            { if (target) TargetPath = selected; else BackupPath = selected; }
        }
        catch (Exception exception)
        {
            // Picker failures belong to the same captured context as successful selections.
            if (IsCurrent(captured, session)) shell.ReportError(exception);
        }
        finally { picking = false; if (!disposed) RefreshAvailability(); }
    }
    private async Task PreviewAsync()
    {
        InvalidatePreview(); var captured = generation; var session = shell.CurrentSession;
        if (RecoverCorruptTarget)
        {
            var result = await shell.PreviewRecoveryAsync(session, TargetPath, BackupPath);
            if (!IsCurrent(captured, session) || !result.Succeeded || result.Value is not { } preview) return;
            recoveryPreview = preview;
            PreviewDetails = $"损坏目标（无法验证原赛事身份）：\n{preview.WorkspacePath}\n原文件 SHA-256：{preview.CorruptContentHash}\n" + DescribeBackup(preview.Backup);
        }
        else if (session is not null)
        {
            var path = BackupPath;
            var result = await shell.RunWorkspaceQueryAsync(session, (workflow, _) => workflow.PreviewRestoreBackup(path));
            if (!IsCurrent(captured, session) || !result.Succeeded || result.Value is not { } preview) return;
            restorePreview = preview;
            PreviewDetails = $"恢复当前工作区：\n{preview.WorkspacePath}\n当前修订：{preview.SourceRevision}\n" + DescribeBackup(preview.Backup);
        }
        previewSession = session; OnPropertyChanged(nameof(HasPreview)); RefreshAvailability();
    }
    private async Task RestoreAsync()
    {
        var healthy = restorePreview; var corrupt = recoveryPreview; var session = previewSession; var acceptedReason = Reason.Trim();
        var target = healthy?.WorkspacePath ?? corrupt?.WorkspacePath;
        InvalidatePreview();
        var success = healthy is not null && session is not null
            ? await shell.RunWorkspaceCommandAsync(session, (workflow, revision) => workflow.RestoreBackup(healthy, acceptedReason, revision), "工作区已从备份恢复并保存。")
            : corrupt is not null && await shell.RecoverFromBackupAsync(session, corrupt, acceptedReason);
        if (!disposed) OutputDetails = $"恢复目标：{target}\n" + (success ? shell.Status : DescribeError(shell.LastError));
    }
    private async Task CreateBackupAsync()
    {
        var session = shell.CurrentSession; if (session is null) return;
        WorkspaceBackupOutcome? outcome = null; WorkspaceBackupException? failure = null;
        var success = await shell.RunWorkspaceCommandAsync(session, (workflow, revision) =>
        {
            try { outcome = workflow.CreateBackup(revision); return outcome.Command; }
            catch (WorkspaceBackupException exception) { failure = exception; throw new WorkspaceCommandException(exception.Error, exception); }
        }, "手动备份已创建，审计记录已保存。");
        if (disposed) return;
        LastManualBackupPath = outcome?.Backup.FullPath ?? failure?.ManualBackupPath;
        LastValidatedBackup = outcome?.Backup ?? failure?.Backup;
        OnPropertyChanged(nameof(LastManualBackupPath)); OnPropertyChanged(nameof(LastValidatedBackup));
        OutputDetails = (LastManualBackupPath is null ? "没有已报告的手动副本。" : $"手动副本：{LastManualBackupPath}\n" +
            (LastValidatedBackup is null ? "完整性未验证；不能视为可用备份，必须重新检查。" : DescribeBackup(LastValidatedBackup))) + "\n" +
            (success && outcome is not null ? $"审计已保存。\n自动审计备份：{outcome.Command.BackupPath}" : DescribeError(failure?.Error ?? shell.LastError));
    }
    private static string DescribeBackup(WorkspaceBackupInfo backup) =>
        $"已验证备份：{backup.FullPath}\n赛事：{backup.Workspace.Name}\n赛事 ID：{backup.Workspace.Id}\n修订：{backup.Workspace.Revision} · {AppShellViewModel.StageName(backup.Workspace.Stage)}\n更新时间：{backup.Workspace.UpdatedAt:O}\nSHA-256：{backup.ContentHash}\n项目 {backup.Workspace.Projects.Count} · 赛果 {backup.Workspace.Results.Count}";
    private static string DescribeError(WorkspaceError? error) => error is null ? "操作未完成，请检查当前状态。" :
        $"{error.Message} [{error.Code}]\n" + (error.Committed ? "目标文件已保存，但重新读取未完成；请检查当前快照并重新载入。" : "本次操作未提交。") +
        (error.CandidatePath is { } candidate ? $"\n保留候选文件：{candidate}" : "") +
        (error.BackupPath is { } backup ? $"\n自动备份 / 保留副本（失败时不保证完整性）：{backup}" : "");
    public void RefreshAvailability()
    {
        PickTargetCommand?.NotifyCanExecuteChanged(); PickBackupCommand?.NotifyCanExecuteChanged(); PreviewCommand?.NotifyCanExecuteChanged();
        RestoreCommand?.NotifyCanExecuteChanged(); CreateBackupCommand?.NotifyCanExecuteChanged();
    }
    public void Dispose() { if (disposed) return; disposed = true; Close(); CloseCommand.NotifyCanExecuteChanged(); }
}
