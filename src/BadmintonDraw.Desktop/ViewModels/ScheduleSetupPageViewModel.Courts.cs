using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed partial class ScheduleSetupPageViewModel
{
    private readonly Func<VenueCourtSelectionViewModel, Task<bool>> chooseCourts;
    private readonly Func<UnavailableCourtSelectionViewModel, Task<bool>> chooseUnavailable;
    private bool selectingCourts, disposed;
    private int selectionGeneration;
    private bool CanSelectCourts => CanEdit && !selectingCourts;

    private IReadOnlyList<string> PreviousCourts(ScheduleDayEditorViewModel day)
    {
        if (!DateOnly.TryParse(day.DateText, out var date)) return [];
        var previous = Days.Where(d => d != day && DateOnly.TryParse(d.DateText, out var prior) && prior < date)
            .OrderByDescending(d => DateOnly.Parse(d.DateText)).FirstOrDefault();
        return previous is null ? [] : ScheduleEditorInput.Courts(previous.CourtsText);
    }

    private bool SelectionIsCurrent(ScheduleDayEditorViewModel day, WorkspaceSession session, int generation) =>
        CanEdit && ReferenceEquals(Session, session) && selectionGeneration == generation && Days.Contains(day);

    private async Task SelectCourtsAsync(ScheduleDayEditorViewModel day, IReadOnlyList<string> initialCourts)
    {
        if (!CanSelectCourts || !Days.Contains(day)) return;
        var session = Session; var generation = selectionGeneration;
        var constraints = day.Unavailable.Select(w => new CourtSelectionConstraint($"{w.StartText}–{w.EndText}", ScheduleEditorInput.Courts(w.CourtsText))).ToArray();
        var options = new VenueCourtSelectionViewModel(initialCourts, constraints);
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
