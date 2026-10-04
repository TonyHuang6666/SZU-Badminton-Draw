using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed partial class ScheduleSetupPageViewModel
{
    private readonly Func<VenueCourtSelectionViewModel, Task<bool>> chooseCourts;
    private readonly Func<UnavailableCourtSelectionViewModel, Task<bool>> chooseUnavailable;
    private bool selectingCourts, disposed;
    private int selectionGeneration;
    private bool CanSelectCourts => CanEdit && !selectingCourts;

    private sealed record DayTimeCopy(string DateText, TimeOnly Start, TimeOnly End);

    private ScheduleDayEditorViewModel? PreviousDay(ScheduleDayEditorViewModel day)
    {
        if (!DateOnly.TryParse(day.DateText, out var date)) return null;
        return Days.Where(d => d != day && DateOnly.TryParse(d.DateText, out var prior) && prior < date)
            .OrderByDescending(d => DateOnly.Parse(d.DateText)).FirstOrDefault();
    }

    private Task CopyPreviousDayAsync(ScheduleDayEditorViewModel day)
    {
        if (!CanSelectCourts || !Days.Contains(day) || PreviousDay(day) is not { } previous) return Task.CompletedTask;
        var courts = ScheduleEditorInput.Courts(previous.CourtsText);
        if (courts.Length == 0) return Task.CompletedTask;
        var start = ScheduleEditorInput.Time(previous.StartText, "上一个比赛日的开始时间");
        var end = ScheduleEditorInput.Time(previous.EndText, "上一个比赛日的结束时间");
        if (end <= start) throw ScheduleEditorInput.Error("请先调整上一个比赛日的时间：结束时间必须晚于开始时间。");
        return SelectCourtsAsync(day, courts, new(previous.DateText, start, end));
    }

    private bool SelectionIsCurrent(ScheduleDayEditorViewModel day, WorkspaceSession session, int generation) =>
        CanEdit && ReferenceEquals(Session, session) && selectionGeneration == generation && Days.Contains(day);

    private async Task SelectCourtsAsync(ScheduleDayEditorViewModel day, IReadOnlyList<string> initialCourts, DayTimeCopy? timeCopy = null)
    {
        if (!CanSelectCourts || !Days.Contains(day)) return;
        var session = Session; var generation = selectionGeneration;
        var constraints = day.Unavailable.Select(w => new CourtSelectionConstraint($"{w.StartText}–{w.EndText}", ScheduleEditorInput.Courts(w.CourtsText))).ToArray();
        var options = new VenueCourtSelectionViewModel(initialCourts, constraints)
        {
            ReuseSummary = timeCopy is null ? "" : $"沿用 {timeCopy.DateText} 的场地和时间：{ScheduleEditorInput.TimeText(timeCopy.Start)}–{ScheduleEditorInput.TimeText(timeCopy.End)}。当前比赛日期不变。"
        };
        selectingCourts = true; RefreshAvailability();
        try
        {
            if (!await chooseCourts(options) || !SelectionIsCurrent(day, session, generation) || !options.TryCreateSelection(out var courts)) return;
            // Never turn a now-empty explicit subset into an all-courts block. The dialog asks for
            // explicit consent before changing affected windows; unchanged/all-court windows survive.
            foreach (var window in day.Unavailable.ToArray())
            {
                var old = ScheduleEditorInput.Courts(window.CourtsText);
                if (old.Length == 0) continue;
                var remaining = old.Where(c => courts.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (remaining.Length == old.Length) continue;
                if (remaining.Length == 0) day.Unavailable.Remove(window);
                else window.CourtsText = string.Join(", ", remaining);
            }
            day.CourtsText = string.Join(", ", courts);
            if (timeCopy is not null)
            {
                day.StartText = ScheduleEditorInput.TimeText(timeCopy.Start);
                day.EndText = ScheduleEditorInput.TimeText(timeCopy.End);
            }
            Edited();
        }
        catch (Exception) when (!SelectionIsCurrent(day, session, generation))
        {
            // A closed/stale dialog must not replace the current page's feedback.
        }
        finally { selectingCourts = false; RefreshAvailability(); }
    }

    private async Task SelectUnavailableCourtsAsync(ScheduleDayEditorViewModel day, ScheduleUnavailableEditorViewModel window)
    {
        if (!CanSelectCourts || !Days.Contains(day) || !day.Unavailable.Contains(window)) return;
        var session = Session; var generation = selectionGeneration;
        var options = new UnavailableCourtSelectionViewModel(ScheduleEditorInput.Courts(day.CourtsText), ScheduleEditorInput.Courts(window.CourtsText));
        selectingCourts = true; RefreshAvailability();
        try
        {
            if (!await chooseUnavailable(options) || !SelectionIsCurrent(day, session, generation) || !day.Unavailable.Contains(window)
                || !options.TryCreateSelection(out var courts)) return;
            window.CourtsText = string.Join(", ", courts);
        }
        catch (Exception) when (!SelectionIsCurrent(day, session, generation) || !day.Unavailable.Contains(window))
        {
            // The target editor no longer owns this asynchronous result or its error.
        }
        finally { selectingCourts = false; RefreshAvailability(); }
    }

    public void Dispose() { disposed = true; selectionGeneration++; RefreshAvailability(); }
}
