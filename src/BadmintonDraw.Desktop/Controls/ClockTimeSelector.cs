using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace BadmintonDraw.Desktop.Controls;

/// <summary>Time selection stays pending until confirmed and is discarded when editing is no longer possible.</summary>
public sealed class ClockTimeSelector : TimePicker
{
    private Popup? popup;
    private Button? button;
    protected override Type StyleKeyOverride => typeof(TimePicker);
    protected override AutomationPeer OnCreateAutomationPeer() => new SelectionOnlyAutomationPeer(this);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (button is not null) button.Click -= Opened;
        if (popup is not null) { popup.Closed -= Closed; popup.Close(); }
        base.OnApplyTemplate(e);
        button = e.NameScope.Find<Button>("PART_FlyoutButton");
        popup = e.NameScope.Find<Popup>("PART_Popup");
        if (button is not null)
        {
            AutomationProperties.SetName(button, AutomationProperties.GetName(this));
            // The base click handler has opened and laid out the presenter before this runs.
            button.Click += Opened;
        }
        if (popup is not null) popup.Closed += Closed;
    }

    private void Opened(object? sender, RoutedEventArgs e)
    {
        if (popup?.IsOpen != true || popup.Child is not TimePickerPresenter presenter) return;
        foreach (var action in presenter.GetVisualDescendants().OfType<Button>())
        {
            if (action.Name is not ("PART_AcceptButton" or "PART_DismissButton")) continue;
            var label = action.Name == "PART_AcceptButton" ? "确认" : "取消";
            action.Content = label;
            AutomationProperties.SetName(action, label);
        }
        // Avalonia retains pending wheel positions if Time is unchanged after cancellation.
        // Reseed the wheels on every opening without touching the bound value or its precision.
        var panels = presenter.GetVisualDescendants().OfType<DateTimePickerPanel>().ToArray();
        var time = presenter.Time;
        foreach (var panel in panels)
        {
            var selected = panel.Name switch
            {
                "PART_HourSelector" => presenter.ClockIdentifier == "12HourClock" ? (time.Hours + 11) % 12 + 1 : time.Hours,
                "PART_MinuteSelector" => time.Minutes,
                "PART_SecondSelector" => time.Seconds,
                "PART_PeriodSelector" => time.Hours >= 12 ? 1 : 0,
                _ => (int?)null
            };
            if (selected.HasValue) panel.SelectedValue = selected.Value;
        }
        panels.FirstOrDefault(panel => panel.Name == "PART_HourSelector")?.Focus();
    }

    private void Closed(object? sender, EventArgs e)
    {
        if (IsEffectivelyEnabled && this.IsAttachedToVisualTree()) button?.Focus();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == IsEffectivelyEnabledProperty && !IsEffectivelyEnabled) ||
            change.Property == DataContextProperty || change.Property == SelectedTimeProperty)
            popup?.Close();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        popup?.Close();
        base.OnDetachedFromVisualTree(e);
    }

    private sealed class SelectionOnlyAutomationPeer(ClockTimeSelector owner) : TimePickerAutomationPeer(owner), IValueProvider
    {
        bool IValueProvider.IsReadOnly => true;
        void IValueProvider.SetValue(string? value) => throw new InvalidOperationException("请通过时间选择器选择时间。");
    }
}
