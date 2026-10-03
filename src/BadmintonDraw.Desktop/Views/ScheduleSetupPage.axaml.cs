using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.ViewModels;
namespace BadmintonDraw.Desktop.Views;
public partial class ScheduleSetupPage : UserControl
{
    private ScheduleSetupPageViewModel? observedModel;
    private int scrollRequest;

    public ScheduleSetupPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ObserveModel();
        AttachedToVisualTree += (_, _) => ObserveModel();
        DetachedFromVisualTree += (_, _) => ObserveModel(detached: true);
    }

    private void ObserveModel(bool detached = false)
    {
        ++scrollRequest;
        if (observedModel is not null) observedModel.DayAdded -= RevealNewDay;
        observedModel = !detached && this.IsAttachedToVisualTree() ? DataContext as ScheduleSetupPageViewModel : null;
        if (observedModel is not null) observedModel.DayAdded += RevealNewDay;
    }

    private void RevealNewDay(ScheduleDayEditorViewModel day)
    {
        var model = observedModel;
        var request = ++scrollRequest;
        Dispatcher.UIThread.Post(() =>
        {
            if (request != scrollRequest || model is null || !ReferenceEquals(observedModel, model) ||
                !ReferenceEquals(model.Days.FirstOrDefault(), day) || ScheduleContent.Content is not Visual content) return;
            // Keep both the add button and the new card visible, without focusing an input or
            // scrolling in response to ordinary edits, reloads, or a page that has been replaced.
            if (ScheduleDaysHeader.TranslatePoint(default, content) is { } point)
                ScheduleContent.Offset = new Vector(0, point.Y);
        }, DispatcherPriority.Loaded);
    }
}
