using System.Collections.ObjectModel;
using System.Globalization;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

internal static class ScheduleEditorInput
{
    internal static WorkspaceCommandException Error(string message) => new(new("schedule.editor-input", message));
    internal static int Integer(string text, string label, int minimum = 1) => int.TryParse(text, out var value) && value >= minimum
        ? value : throw Error($"{label}应为不小于 {minimum} 的整数。");
    internal static TimeOnly Time(string text, string label) => TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
        ? value : throw Error($"{label}请输入有效时间，例如 09:00。");
    internal static string TimeText(TimeOnly value) => value.Ticks % TimeSpan.TicksPerMinute == 0
        ? value.ToString("HH:mm", CultureInfo.InvariantCulture)
        : value.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    internal static DateOnly Date(string text) => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
        ? value : throw Error("比赛日请输入有效日期，格式 yyyy-MM-dd。");
    internal static double? Percent(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value is >= 0 and <= 100
            ? value / 100 : throw Error($"{label}应为 0–100 的百分比，留空表示不覆盖策略默认值。");
    }
    internal static string PercentText(double? value) => value?.ToString("G17", CultureInfo.InvariantCulture) is null ? "" : (value.Value * 100).ToString("G17", CultureInfo.InvariantCulture);
    internal static string[] Courts(string text) => text.Split([',', '，', ';', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public abstract class ScheduleEditorViewModel(Action changed) : ViewModelBase
{
    protected void Edit<T>(ref T field, T value, string property) { if (SetProperty(ref field, value, property)) changed(); }
}

public sealed class ScheduleDayEditorViewModel : ScheduleEditorViewModel
{
    private readonly Action changed;
    private readonly Func<ScheduleDayEditorViewModel, ScheduleUnavailableEditorViewModel, Task>? chooseUnavailable;
    private readonly Func<bool> canEdit;
    private readonly Action<Exception>? onError;
    private string dateText, startText, endText, courtsText, targetLoadText, warningLoadText, stageProgressText;
    public string DateText { get => dateText; set => Edit(ref dateText, value, nameof(DateText)); }
    public string StartText { get => startText; set => Edit(ref startText, value, nameof(StartText)); }
    public string EndText { get => endText; set => Edit(ref endText, value, nameof(EndText)); }
    public string CourtsText
    {
        get => courtsText;
        set
        {
            if (!SetProperty(ref courtsText, value)) return;
            OnPropertyChanged(nameof(VenueSummary)); OnPropertyChanged(nameof(CourtsSummary));
            changed(); RefreshCommands();
        }
    }
    public string VenueSummary => ScheduleCourtPresentation.Venue(ScheduleEditorInput.Courts(CourtsText));
    public string CourtsSummary => ScheduleCourtPresentation.Summary(ScheduleEditorInput.Courts(CourtsText));
    public string TargetLoadText { get => targetLoadText; set => Edit(ref targetLoadText, value, nameof(TargetLoadText)); }
    public string WarningLoadText { get => warningLoadText; set => Edit(ref warningLoadText, value, nameof(WarningLoadText)); }
    public string StageProgressText { get => stageProgressText; set => Edit(ref stageProgressText, value, nameof(StageProgressText)); }
    public ObservableCollection<ScheduleUnavailableEditorViewModel> Unavailable { get; } = [];
    public ObservableCollection<ScheduleRefereeEditorViewModel> RefereeWindows { get; } = [];
    public DelegateCommand AddUnavailableCommand { get; }
    public DelegateCommand AddRefereeWindowCommand { get; }
    public DelegateCommand RemoveCommand { get; }
    public AsyncCommand ChooseCourtsCommand { get; }
    public AsyncCommand CopyPreviousCourtsCommand { get; }
    public ScheduleDayEditorViewModel(ScheduleDaySettings day, double? target, double? warning, double? stage, Action changed, Action<ScheduleDayEditorViewModel> remove,
        Func<ScheduleDayEditorViewModel, Task>? chooseCourts = null,
        Func<ScheduleDayEditorViewModel, ScheduleUnavailableEditorViewModel, Task>? chooseUnavailable = null,
        Func<ScheduleDayEditorViewModel, Task>? copyPrevious = null, Func<bool>? canEdit = null,
        Func<ScheduleDayEditorViewModel, bool>? canCopyPrevious = null, Action<Exception>? onError = null) : base(changed)
    {
        this.chooseUnavailable = chooseUnavailable; this.canEdit = canEdit ?? (() => true); this.onError = onError;
        this.changed = changed; dateText = day.DayLabel; startText = ScheduleEditorInput.TimeText(day.DayStart); endText = ScheduleEditorInput.TimeText(day.DayEnd); courtsText = string.Join(", ", day.Courts);
        targetLoadText = ScheduleEditorInput.PercentText(target); warningLoadText = ScheduleEditorInput.PercentText(warning); stageProgressText = ScheduleEditorInput.PercentText(stage);
        foreach (var item in day.UnavailableCourtWindows ?? []) AddUnavailable(item);
        foreach (var item in day.RefereeCapacityWindows ?? []) AddReferees(item);
        TimeOnly WindowStart() => TimeOnly.TryParse(StartText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : day.DayStart;
        TimeOnly WindowEnd() => TimeOnly.TryParse(EndText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : day.DayEnd;
        AddUnavailableCommand = new(() => { AddUnavailable(new(WindowStart(), WindowEnd(), [])); changed(); }, this.canEdit);
        AddRefereeWindowCommand = new(() => { AddReferees(new(WindowStart(), WindowEnd(), 0)); changed(); }, this.canEdit);
        RemoveCommand = new(() => remove(this));
        ChooseCourtsCommand = new(() => chooseCourts?.Invoke(this) ?? Task.CompletedTask, () => this.canEdit() && chooseCourts is not null, onError);
        CopyPreviousCourtsCommand = new(() => copyPrevious?.Invoke(this) ?? Task.CompletedTask,
            () => this.canEdit() && copyPrevious is not null && (canCopyPrevious?.Invoke(this) ?? false), onError);
    }
    private void AddUnavailable(ScheduleCourtAvailabilityBlock value) => Unavailable.Add(new(value, changed, item => { Unavailable.Remove(item); changed(); },
        item => chooseUnavailable?.Invoke(this, item) ?? Task.CompletedTask,
        () => canEdit() && chooseUnavailable is not null && ScheduleEditorInput.Courts(CourtsText).Length > 0, onError));
    private void AddReferees(ScheduleRefereeCapacityWindow value) => RefereeWindows.Add(new(value, changed, item => { RefereeWindows.Remove(item); changed(); }));
    public ScheduleDaySettings Build() => new(ScheduleEditorInput.Date(DateText), ScheduleEditorInput.Time(StartText, "开始时间"), ScheduleEditorInput.Time(EndText, "结束时间"),
        ScheduleEditorInput.Courts(CourtsText), RefereeWindows.Select(w => w.Build()).ToArray(), Unavailable.Select(w => w.Build()).ToArray());
    public void RefreshCommands()
    {
        ChooseCourtsCommand?.NotifyCanExecuteChanged(); CopyPreviousCourtsCommand?.NotifyCanExecuteChanged();
        AddUnavailableCommand?.NotifyCanExecuteChanged(); AddRefereeWindowCommand?.NotifyCanExecuteChanged();
        foreach (var window in Unavailable) window.ChooseCourtsCommand.NotifyCanExecuteChanged();
    }
}

public sealed class ScheduleUnavailableEditorViewModel : ScheduleEditorViewModel
{
    private string startText, endText, courtsText;
    public string StartText { get => startText; set => Edit(ref startText, value, nameof(StartText)); }
    public string EndText { get => endText; set => Edit(ref endText, value, nameof(EndText)); }
    public string CourtsText { get => courtsText; set { Edit(ref courtsText, value, nameof(CourtsText)); OnPropertyChanged(nameof(CourtsSummary)); } }
    public string CourtsSummary => ScheduleEditorInput.Courts(CourtsText) is { Length: > 0 } courts
        ? ScheduleCourtPresentation.Summary(courts, shorten: false) : "当天全部场地";
    public DelegateCommand RemoveCommand { get; }
    public AsyncCommand ChooseCourtsCommand { get; }
    public ScheduleUnavailableEditorViewModel(ScheduleCourtAvailabilityBlock value, Action changed, Action<ScheduleUnavailableEditorViewModel> remove,
        Func<ScheduleUnavailableEditorViewModel, Task>? chooseCourts = null, Func<bool>? canChoose = null, Action<Exception>? onError = null) : base(changed)
    {
        startText = ScheduleEditorInput.TimeText(value.StartTime); endText = ScheduleEditorInput.TimeText(value.EndTime); courtsText = string.Join(", ", value.Courts); RemoveCommand = new(() => remove(this));
        ChooseCourtsCommand = new(() => chooseCourts?.Invoke(this) ?? Task.CompletedTask, () => chooseCourts is not null && (canChoose?.Invoke() ?? true), onError);
    }
    public ScheduleCourtAvailabilityBlock Build() => new(ScheduleEditorInput.Time(StartText, "不可用开始"), ScheduleEditorInput.Time(EndText, "不可用结束"), ScheduleEditorInput.Courts(CourtsText));
}

internal static class ScheduleCourtPresentation
{
    internal static string Venue(IReadOnlyList<string> courts)
    {
        if (courts.Count == 0) return "尚未选择场馆";
        if (BuiltInVenueCatalog.FindForCourts(courts) is { } preset) return preset.DisplayName;
        var prefix = CommonPrefix(courts);
        return prefix.Length > 0 ? prefix : "自定义场地";
    }
    internal static string Summary(IReadOnlyList<string> courts, bool shorten = true)
    {
        if (courts.Count == 0) return "尚未选择场地，请选择已预约或确认可用的场地。";
        var prefix = shorten ? CommonPrefix(courts) : "";
        var labels = courts.Take(8).Select(c => prefix.Length > 0 ? c[(prefix.Length + 3)..] : c);
        return string.Join("、", labels) + (courts.Count > 8 ? " 等" : "") + $" · 共 {courts.Count} 片";
    }
    private static string CommonPrefix(IReadOnlyList<string> courts)
    {
        if (courts.Count == 0) return "";
        var separator = courts[0].IndexOf(" · ", StringComparison.Ordinal);
        if (separator <= 0) return "";
        var prefix = courts[0][..separator];
        return courts.All(c => c.StartsWith(prefix + " · ", StringComparison.Ordinal)) ? prefix : "";
    }
}

public sealed class ScheduleRefereeEditorViewModel : ScheduleEditorViewModel
{
    private string startText, endText, countText;
    public string StartText { get => startText; set => Edit(ref startText, value, nameof(StartText)); }
    public string EndText { get => endText; set => Edit(ref endText, value, nameof(EndText)); }
    public string CountText { get => countText; set => Edit(ref countText, value, nameof(CountText)); }
    public DelegateCommand RemoveCommand { get; }
    public ScheduleRefereeEditorViewModel(ScheduleRefereeCapacityWindow value, Action changed, Action<ScheduleRefereeEditorViewModel> remove) : base(changed)
    { startText = ScheduleEditorInput.TimeText(value.StartTime); endText = ScheduleEditorInput.TimeText(value.EndTime); countText = value.RefereeCount.ToString(); RemoveCommand = new(() => remove(this)); }
    public ScheduleRefereeCapacityWindow Build() => new(ScheduleEditorInput.Time(StartText, "裁判时段开始"), ScheduleEditorInput.Time(EndText, "裁判时段结束"), ScheduleEditorInput.Integer(CountText, "可用裁判", 0));
}
