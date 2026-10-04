using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace BadmintonDraw.Desktop.Controls;

/// <summary>A calendar-only date field: neither typing nor scrolling changes the date.</summary>
public sealed class CalendarDateSelector : CalendarDatePicker
{
    private Calendar? calendar;
    private DateTime? dateWhenOpened;
    protected override Type StyleKeyOverride => typeof(CalendarDatePicker);
    protected override AutomationPeer OnCreateAutomationPeer() => new CalendarOnlyAutomationPeer(this);

    public CalendarDateSelector() => CalendarOpened += (_, _) =>
    {
        dateWhenOpened = SelectedDate;
        calendar?.Focus();
    };

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (calendar is not null) calendar.KeyDown -= CalendarKeyDown;
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<TextBox>("PART_TextBox") is { } text)
        {
            text.IsReadOnly = true;
            text.IsHitTestVisible = false;
            text.Focusable = false;
        }
        calendar = e.NameScope.Find<Calendar>("PART_Calendar");
        if (calendar is not null)
        {
            calendar.Focusable = true;
            calendar.KeyDown += CalendarKeyDown;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && ReferenceEquals(e.Source, this) && IsEffectivelyEnabled &&
            !IsDropDownOpen && e.Key is Key.Enter or Key.Space)
        {
            SetCurrentValue(IsDropDownOpenProperty, true);
            e.Handled = true;
        }
    }

    private void CalendarKeyDown(object? sender, KeyEventArgs e)
    {
        // The built-in picker handles Escape only in day view, not month/year navigation.
        if (!e.Handled && e.Key == Key.Escape)
        {
            SetCurrentValue(SelectedDateProperty, dateWhenOpened);
            SetCurrentValue(IsDropDownOpenProperty, false);
            Focus();
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // Let the surrounding page scroll instead of silently moving the competition day.
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsEffectivelyEnabledProperty && !IsEffectivelyEnabled)
            SetCurrentValue(IsDropDownOpenProperty, false);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SetCurrentValue(IsDropDownOpenProperty, false);
        base.OnDetachedFromVisualTree(e);
    }

    private sealed class CalendarOnlyAutomationPeer(CalendarDateSelector owner)
        : CalendarDatePickerAutomationPeer(owner), IValueProvider, IExpandCollapseProvider
    {
        bool IValueProvider.IsReadOnly => true;
        void IValueProvider.SetValue(string? value) => throw new InvalidOperationException("请通过日历选择日期。");
        void IExpandCollapseProvider.Expand()
        {
            EnsureEnabled();
            Owner.SetCurrentValue(IsDropDownOpenProperty, true);
        }
    }
}
