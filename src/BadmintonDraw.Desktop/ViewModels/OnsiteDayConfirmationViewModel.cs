using System.ComponentModel;
using System.Globalization;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class OnsitePostponementRowViewModel(WorkspaceMatchKey key, string title) : ViewModelBase
{
    private bool postponed;
    private string reason = "";
    public WorkspaceMatchKey Key { get; } = key;
    public string Title { get; } = title;
    public bool IsPostponed { get => postponed; set => SetProperty(ref postponed, value); }
    public string Reason { get => reason; set => SetProperty(ref reason, value ?? ""); }
}

public sealed record OnsiteDayChangeViewModel(string Title, string Before, string After);

public sealed class OnsiteDayConfirmationViewModel : ViewModelBase
{
    private readonly AppShellViewModel shell;
    private readonly WorkspaceSession source;
    private readonly DateOnly day;
    private readonly Action<WorkspaceSession> completed;
    private WorkspaceDayConfirmationPreview? preview;
    private long draftGeneration;
    private bool working, done;
    private string message = "请核对当天结果。尚未完成的比赛须逐场填写延期原因，并确认已经征得裁判长同意。";

    internal OnsiteDayConfirmationViewModel(AppShellViewModel shell, WorkspaceSession source, DateOnly day,
        IReadOnlyList<OnsitePostponementRowViewModel> pending, Action<WorkspaceSession> completed)
    {
        this.shell = shell; this.source = source; this.day = day; this.completed = completed;
        PendingMatches = pending;
        foreach (var row in pending) row.PropertyChanged += InvalidatePreview;
    }

    public string Day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public IReadOnlyList<OnsitePostponementRowViewModel> PendingMatches { get; }
    public string Summary => $"{Day} · 已记录 {source.Workspace.Schedule!.Placements.Values.Count(p => p.DayLabel == Day) - PendingMatches.Count} 场" +
        $" · 拟延期 {PendingMatches.Count(p => p.IsPostponed)} 场 · 未处理 {PendingMatches.Count(p => !p.IsPostponed || string.IsNullOrWhiteSpace(p.Reason))} 场";
    public bool IsWorking => working;
    public bool IsCompleted => done;
    public bool CanPreview => !working && !done && PendingMatches.All(p => p.IsPostponed && !string.IsNullOrWhiteSpace(p.Reason));
    public bool HasPreview => preview is not null;
    public bool CanConfirm => !working && !done && preview is { CanConfirm: true };
    public string StateMessage => message;
    public string PreviewSummary => preview is null ? "" : $"已记录 {preview.CompletedCount} 场 · 经批准延期 {preview.PostponedCount} 场 · 调整 {preview.Changes.Count} 场";
    public IReadOnlyList<OnsiteDayChangeViewModel> Changes { get; private set; } = [];

    private void InvalidatePreview(object? sender, PropertyChangedEventArgs e)
    { draftGeneration++; preview = null; Changes = []; message = "延期选择或原因已变更，请重新预览。"; OnPropertyChanged(null); }

    public async Task PreviewAsync()
    {
        if (!CanPreview) return;
        working = true; preview = null; Changes = []; message = "正在校验当天结果及延期编排…"; OnPropertyChanged(null);
        var capturedGeneration = draftGeneration;
        try
        {
            var result = await shell.PreviewOnsiteDayAsync(source, new(day,
                PendingMatches.Select(p => new WorkspaceMatchPostponement(p.Key, p.Reason.Trim(), p.IsPostponed)).ToArray()));
            if (draftGeneration != capturedGeneration)
            { message = "延期选择或原因已变更，请重新预览。"; return; }
            if (result.Error is { } error) { message = error.Message; return; }
            preview = result.Value;
            Changes = preview?.Changes.Select(c => new OnsiteDayChangeViewModel($"{c.ProjectName} · {c.MatchName}",
                $"{c.Before.DayLabel} {c.Before.StartTime:HH:mm}–{c.Before.EndTime:HH:mm} · {c.Before.Court}",
                $"{c.After.DayLabel} {c.After.StartTime:HH:mm}–{c.After.EndTime:HH:mm} · {c.After.Court}")).ToArray() ?? [];
            message = preview is { CanConfirm: true } ? "校验通过。确认后将保存当天确认记录及以上赛程调整，并开放下一个比赛日。" :
                string.Join("\n", preview?.Errors ?? ["未生成有效确认方案。"]);
        }
        catch (Exception error) { message = "预览失败：" + error.Message; }
        finally { working = false; OnPropertyChanged(null); }
    }

    public async Task ConfirmAsync()
    {
        if (!CanConfirm) return;
        working = true; message = "正在保存当天确认和赛程调整…"; OnPropertyChanged(null);
        try
        {
            var result = await shell.ConfirmOnsiteDayAsync(source, preview!);
            if (result.Error is { } error)
            { message = error.Committed ? "确认已经写入，但读取失败。请关闭窗口并重新打开赛事核对。" : error.Message; preview = null; return; }
            if (result.Value is null || result.CompletionSession is null) { message = "未取得保存结果，请重新打开赛事核对。"; preview = null; return; }
            done = true; message = "当天结果已确认。"; completed(result.CompletionSession);
        }
        catch (Exception error) { message = "确认失败：" + error.Message; preview = null; }
        finally { working = false; OnPropertyChanged(null); }
    }
}
