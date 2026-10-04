using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class ScheduleDateValueTests
{
    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Utc)]
    public void CalendarDatesIgnoreTimeOfDayAndTimeZoneAndNotifyOnce(DateTimeKind kind)
    {
        var changes = 0;
        var day = new ScheduleDayEditorViewModel(new(new(2026, 10, 3), new(14, 0), new(18, 0), ["B1"]),
            () => changes++, _ => { });
        var properties = new List<string?>();
        day.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        day.SelectedDate = new DateTime(2028, 2, 29, 23, 59, 0, kind);
        Assert.Equal("2028-02-29", day.DateText);
        Assert.Equal(new DateOnly(2028, 2, 29), day.Build().Date);
        Assert.Equal(DateTimeKind.Unspecified, day.SelectedDate!.Value.Kind);
        Assert.Equal(1, changes);
        Assert.Equal([nameof(day.DateText), nameof(day.SelectedDate)], properties);
        day.SelectedDate = new DateTime(2028, 2, 29);
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2027-02-29")]
    public void MissingOrInvalidDatesAreNotSilentlyChangedToToday(string text)
    {
        var day = new ScheduleDayEditorViewModel(new(new(2026, 10, 3), new(14, 0), new(18, 0), ["B1"]),
            () => { }, _ => { }) { DateText = text };
        Assert.Null(day.SelectedDate);
        Assert.Equal(text, day.DateText);
        Assert.Throws<WorkspaceCommandException>(() => day.Build());
    }
}
