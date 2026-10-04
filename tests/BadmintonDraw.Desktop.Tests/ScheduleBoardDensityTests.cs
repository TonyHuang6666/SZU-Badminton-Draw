using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.Controls;
using BadmintonDraw.Desktop.Scheduling;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class ScheduleBoardDensityTests : IDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));

    // A compact renderer that drops identity/players or keeps minute-only seconds breaks this contract.
    [Fact]
    public Task CompactCardsKeepIdentityPlayersAndLockWhileDetailToggleRestoresPhase() => session.Dispatch(() =>
    {
        var item = Card(new(9, 0), new(9, 30)) with { IsLocked = true };
        WithBoard(Board(item), (control, window) =>
        {
            var card = FindCard(control, item.Key);
            var compactHeight = card.Bounds.Height;
            var compact = CardText(card);
            Assert.Contains("男子单打", compact);
            Assert.Contains("第 1 场", compact);
            Assert.Contains("张三", compact);
            Assert.Contains("李四", compact);
            Assert.Contains("锁定", compact);
            Assert.Contains("09:00–09:30", compact);
            Assert.DoesNotContain("09:00:00", compact);
            Assert.DoesNotContain("淘汰赛", compact);

            var toggle = control.FindControl<ToggleButton>("CompactToggle");
            Assert.NotNull(toggle);
            Assert.True(toggle.IsChecked);
            toggle.IsChecked = false;
            window.UpdateLayout();
            card = FindCard(control, item.Key);
            Assert.Contains("淘汰赛", CardText(card));
            Assert.Contains("张三", CardText(card));
            Assert.True(card.Bounds.Height > compactHeight);
            Assert.False(card.ContextMenu!.Items.OfType<MenuItem>().Single().IsEnabled);
        });
    }, CancellationToken.None);

    // Sub-minute starts are legal placements, so presentation must not silently round them away.
    [Theory]
    [InlineData(30, 0, "09:00:30–09:30:30")]
    [InlineData(30, 125, "09:00:30.125–09:30:30.125")]
    public Task CardsKeepSubMinutePrecision(int seconds, int milliseconds, string expected) => session.Dispatch(() =>
    {
        var item = Card(new(9, 0, seconds, milliseconds), new(9, 30, seconds, milliseconds));
        WithBoard(Board(item), (control, _) => Assert.Contains(expected, CardText(FindCard(control, item.Key))));
    }, CancellationToken.None);

    // Losing either offset synchronization makes headings describe the wrong cell after a scroll.
    [Fact]
    public Task ScrollingKeepsCourtHeadersAndTimeAxisFixedAndAlignedWithCells() => session.Dispatch(() =>
    {
        var item = Card(new(9, 0), new(9, 30));
        WithBoard(Board(item), (control, window) =>
        {
            var body = control.FindControl<ScrollViewer>("BoardScroll")!;
            var headers = control.FindControl<ScrollViewer>("CourtHeaderScroll");
            var times = control.FindControl<ScrollViewer>("TimeAxisScroll");
            Assert.NotNull(headers);
            Assert.NotNull(times);
            var court = headers.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "B2");
            var time = times.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "09:15");
            var initialCourt = court.TranslatePoint(default, control)!.Value;
            var initialTime = time.TranslatePoint(default, control)!.Value;

            body.Offset = new(100, 100);
            window.UpdateLayout();

            Assert.Equal(100, body.Offset.X);
            Assert.Equal(100, body.Offset.Y);
            Assert.Equal(body.Offset.X, headers.Offset.X);
            Assert.Equal(body.Offset.Y, times.Offset.Y);
            var movedCourt = court.TranslatePoint(default, control)!.Value;
            var movedTime = time.TranslatePoint(default, control)!.Value;
            Assert.Equal(initialCourt.Y, movedCourt.Y);
            Assert.Equal(initialCourt.X - 100, movedCourt.X);
            Assert.Equal(initialTime.X, movedTime.X);
            Assert.Equal(initialTime.Y - 100, movedTime.Y);

            var bodyRows = control.FindControl<Grid>("BoardGrid")!.RowDefinitions;
            var axisRows = control.FindControl<Grid>("TimeAxisGrid")!.RowDefinitions;
            Assert.Equal(bodyRows.Count, axisRows.Count);
            for (var i = 0; i < bodyRows.Count; i++) Assert.Equal(bodyRows[i].ActualHeight, axisRows[i].ActualHeight);

            body.Offset = new(body.Extent.Width, body.Extent.Height);
            window.UpdateLayout();
            Assert.Equal(body.Offset.X, headers.Offset.X);
            Assert.Equal(body.Offset.Y, times.Offset.Y);
        });
    }, CancellationToken.None);

    // Hover validation text must never move the drop target while a drag is active.
    [Fact]
    public Task ShortAndLongHoverFeedbackKeepDropTargetGeometryStable() => session.Dispatch(() =>
    {
        WithBoard(Board(Card(new(9, 0), new(9, 30))), (control, window) =>
        {
            var feedback = control.FindControl<TextBlock>("Feedback")!;
            feedback.Text = "可以移动";
            window.UpdateLayout();
            var scroll = control.FindControl<ScrollViewer>("BoardScroll")!;
            var position = scroll.TranslatePoint(default, control);
            var viewport = scroll.Viewport;
            feedback.Text = string.Join("；", Enumerable.Repeat("目标位置与选手休息时间冲突，请选择其他场地或时间", 8));
            window.UpdateLayout();
            Assert.Equal(position, scroll.TranslatePoint(default, control));
            Assert.Equal(viewport, scroll.Viewport);
        });
    }, CancellationToken.None);

    // Coloring every match independently would prevent recognizing one project across dates or densities.
    [Fact]
    public Task ProjectColorsRemainConsistentAcrossCardsAndDensityChangesWithTextLabels() => session.Dispatch(() =>
    {
        var first = Card(new(9, 0), new(9, 30));
        var second = Card(new(9, 30), new(10, 0), first.Key.ProjectId);
        var other = Card(new(10, 0), new(10, 30)) with { ProjectName = "女子单打" };
        WithBoard(Board(first, second, other), (control, window) =>
        {
            var firstColor = ((ISolidColorBrush)FindCard(control, first.Key).Background!).Color;
            Assert.Equal(firstColor, ((ISolidColorBrush)FindCard(control, second.Key).Background!).Color);
            Assert.NotEqual(firstColor, ((ISolidColorBrush)FindCard(control, other.Key).Background!).Color);
            Assert.Contains("女子单打", CardText(FindCard(control, other.Key)));
            control.FindControl<ToggleButton>("CompactToggle")!.IsChecked = false;
            window.UpdateLayout();
            Assert.Equal(firstColor, ((ISolidColorBrush)FindCard(control, first.Key).Background!).Color);
        });
    }, CancellationToken.None);

    // A minimum height inherited from the detailed view wastes vertical space on empty slots.
    [Fact]
    public Task CompactEmptySlotsUseLessHeightThanDetailedSlots() => session.Dispatch(() =>
    {
        WithBoard(Board(Card(new(9, 0), new(9, 30))), (control, window) =>
        {
            var compact = control.FindControl<Grid>("BoardGrid")!.RowDefinitions[2].ActualHeight;
            Assert.InRange(compact, 24, 48);
            control.FindControl<ToggleButton>("CompactToggle")!.IsChecked = false;
            window.UpdateLayout();
            Assert.True(control.FindControl<Grid>("BoardGrid")!.RowDefinitions[2].ActualHeight > compact);
        });
    }, CancellationToken.None);

    // A timeout, border-only highlight, or measuring the badge only after location breaks this contract.
    [Fact]
    public Task LocatedCardKeepsNoticeableHighlightUntilExplicitlyClearedWithoutChangingGeometry() => session.Dispatch(async () =>
    {
        var item = Card(new(9, 0), new(9, 30));
        var sibling = Card(new(9, 30), new(10, 0), item.Key.ProjectId);
        var control = new ScheduleBoardControl { Board = Board(item, sibling), SelectedDay = "2026-10-04" };
        var window = new Window { Width = 600, Height = 450, Content = control };
        try
        {
            window.Show(); window.UpdateLayout();
            var card = FindCard(control, item.Key);
            var originalBorder = ((ISolidColorBrush)card.BorderBrush!).Color;
            var originalBackground = ((ISolidColorBrush)card.Background!).Color;
            var originalSize = card.Bounds.Size;
            var originalViewport = control.FindControl<ScrollViewer>("BoardScroll")!.Viewport;
            control.FocusMatch(item.Key); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.NotEqual(originalBorder, ((ISolidColorBrush)card.BorderBrush!).Color);
            Assert.NotEqual(originalBackground, ((ISolidColorBrush)card.Background!).Color);
            AssertLocated(control, item.Key);
            AssertNotLocated(control, sibling.Key);
            Assert.Equal(originalSize, card.Bounds.Size);
            Assert.Equal(originalViewport, control.FindControl<ScrollViewer>("BoardScroll")!.Viewport);
            Assert.True(card.BoxShadow.Count > 0);
            AssertReadable(card);
            var info = control.FindControl<TextBlock>("LocationInfo")!;
            Assert.Contains("男子单打", info.Text);
            Assert.Contains("2026-10-04", info.Text);
            Assert.Contains("09:00–09:30", info.Text);
            Assert.Contains("B1", info.Text);

            await Task.Delay(1700); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            AssertLocated(control, item.Key);
            Assert.NotEqual(originalBorder, ((ISolidColorBrush)card.BorderBrush!).Color);
            control.FindControl<Button>("CancelLocation")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            AssertNotLocated(control, item.Key);
            Assert.Equal(originalBorder, ((ISolidColorBrush)card.BorderBrush!).Color);
            Assert.Equal(originalBackground, ((ISolidColorBrush)card.Background!).Color);
            Assert.Equal(originalSize, card.Bounds.Size);
            Assert.Equal(originalViewport, control.FindControl<ScrollViewer>("BoardScroll")!.Viewport);
            Assert.False(control.FindControl<Border>("LocationSummary")!.IsVisible);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    // Selecting a different card must transfer the location, including within the same project.
    [Fact]
    public Task LocatingOrSelectingAnotherMatchTransfersTheOnlyLocationBadge() => session.Dispatch(() =>
    {
        var first = Card(new(9, 0), new(9, 30));
        var second = Card(new(9, 30), new(10, 0), first.Key.ProjectId);
        WithBoard(Board(first, second), (control, window) =>
        {
            control.FocusMatch(first.Key); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            AssertLocated(control, first.Key);
            control.FocusMatch(second.Key); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            AssertNotLocated(control, first.Key);
            AssertLocated(control, second.Key);
            control.CanEdit = false;
            FindCard(control, first.Key).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            window.UpdateLayout();
            AssertLocated(control, first.Key);
            AssertNotLocated(control, second.Key);
        });
    }, CancellationToken.None);

    // The minimum font size must not be clipped by scaled badge space; comma decimals must not create extra columns.
    [Theory]
    [InlineData(.65)]
    [InlineData(1.13)]
    public Task LocationBadgeAndLockedSubMinuteTimeRemainReadableAtZoomInCommaDecimalCulture(double zoom) => session.Dispatch(() =>
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            var item = Card(new(9, 0, 30, 125), new(9, 30, 30, 125)) with { IsLocked = true };
            WithBoard(Board(item), (control, window) =>
            {
                control.Zoom = zoom;
                control.FocusMatch(item.Key); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var card = FindCard(control, item.Key);
                var badge = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "当前定位");
                Assert.True(badge.Bounds.Width >= badge.TextLayout.WidthIncludingTrailingWhitespace,
                    $"Badge width {badge.Bounds.Width} must fit rendered text width {badge.TextLayout.WidthIncludingTrailingWhitespace} at {zoom} zoom.");
                var badgeBorder = (Border)badge.Parent!;
                var position = badgeBorder.TranslatePoint(default, card)!.Value;
                Assert.InRange(card.Bounds.Width - position.X - badgeBorder.Bounds.Width,
                    card.Padding.Right + card.BorderThickness.Right - .51, card.Padding.Right + card.BorderThickness.Right + .51);
                Assert.Contains("09:00:30.125–09:30:30.125 · 已锁定", CardText(card));
                var time = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text!.Contains("已锁定", StringComparison.Ordinal));
                Assert.True(time.Bounds.Width > 0);
                Assert.True(time.Bounds.Height >= time.TextLayout.Height);
                AssertReadable(card);
            });
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }, CancellationToken.None);

    // Rebuilding render elements or refreshing the same workspace must retain the logical match key.
    [Fact]
    public Task LocationSurvivesDayZoomDensityThemeAndBoardRefreshWithUpdatedPosition() => session.Dispatch(() =>
    {
        var item = Card(new(9, 0), new(9, 30));
        var original = Board(item);
        var otherResources = new ScheduleDaySettings(new(2026, 10, 5), new(9, 0), new(18, 0), ["B1"]);
        original = original with { Days = [.. original.Days, new(otherResources, [new(9, 0)])] };
        WithBoard(original, (control, window) =>
        {
            control.FocusMatch(item.Key); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            control.SelectedDay = "2026-10-05"; window.UpdateLayout();
            var info = control.FindControl<TextBlock>("LocationInfo");
            Assert.NotNull(info);
            Assert.Contains("2026-10-04", info.Text);
            control.SelectedDay = "2026-10-04";
            control.Zoom = .7; control.IsCompact = false; window.UpdateLayout();
            AssertLocated(control, item.Key);
            var lightBackground = ((ISolidColorBrush)FindCard(control, item.Key).Background!).Color;
            window.RequestedThemeVariant = ThemeVariant.Dark; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var card = FindCard(control, item.Key);
            AssertLocated(control, item.Key);
            Assert.NotEqual(lightBackground, ((ISolidColorBrush)card.Background!).Color);
            AssertReadable(card);

            var moved = item with { Placement = item.Placement with { StartTime = new(10, 0), EndTime = new(10, 30), Court = "B2" } };
            control.Board = original with { WorkspaceRevision = 2, ScheduleRevision = 2, Cards = [moved] }; window.UpdateLayout();
            AssertLocated(control, item.Key);
            Assert.Contains("10:00–10:30", control.FindControl<TextBlock>("LocationInfo")!.Text);
            Assert.Contains("B2", control.FindControl<TextBlock>("LocationInfo")!.Text);
        });
    }, CancellationToken.None);

    // Removed targets and new workspaces must not resurrect stale match location state.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RemovingLocatedMatchOrChangingWorkspaceClearsTheLocation(bool newWorkspace) => session.Dispatch(() =>
    {
        var item = Card(new(9, 0), new(9, 30));
        var original = Board(item);
        WithBoard(original, (control, window) =>
        {
            control.FocusMatch(item.Key); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            AssertLocated(control, item.Key);
            control.Board = newWorkspace ? original with { WorkspaceId = Guid.NewGuid() } : original with { Cards = [] };
            window.UpdateLayout();
            Assert.False(control.FindControl<Border>("LocationSummary")!.IsVisible);
            control.Board = original; window.UpdateLayout();
            AssertNotLocated(control, item.Key);
        });
    }, CancellationToken.None);

    private static void AssertLocated(ScheduleBoardControl control, WorkspaceMatchKey key) =>
        Assert.Contains(FindCard(control, key).GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "当前定位" && t.IsVisible && t.Opacity > 0);
    private static void AssertNotLocated(ScheduleBoardControl control, WorkspaceMatchKey key) =>
        Assert.DoesNotContain(FindCard(control, key).GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "当前定位" && t.IsVisible && t.Opacity > 0);
    private static void AssertReadable(Border card)
    {
        foreach (var text in card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible))
        {
            var background = text.Text == "当前定位" ? ((ISolidColorBrush)((Border)text.Parent!).Background!).Color : ((ISolidColorBrush)card.Background!).Color;
            Assert.True(Contrast(((ISolidColorBrush)text.Foreground!).Color, background) >= 4.5, $"Located text '{text.Text}' must remain readable on its background.");
        }
    }
    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value) { var channel = value / 255d; return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4); }
            return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static WorkspaceBoardCard Card(TimeOnly start, TimeOnly end, Guid? projectId = null)
    {
        var key = new WorkspaceMatchKey(projectId ?? Guid.NewGuid(), Guid.NewGuid());
        return new(key, "男子单打", "第 1 场", "淘汰赛", "张三\nVS\n李四", new(key.MatchId, "2026-10-04", start, end, "B1"), false);
    }

    private static WorkspaceScheduleBoard Board(params WorkspaceBoardCard[] cards)
    {
        var resources = new ScheduleDaySettings(new(2026, 10, 4), new(9, 0), new(18, 0), ["B1", "B2", "B3", "B4"]);
        var times = Enumerable.Range(0, 36).Select(i => new TimeOnly(9, 0).AddMinutes(i * 15)).Concat(cards.Select(c => c.Placement.StartTime)).Distinct().Order().ToArray();
        return new(Guid.NewGuid(), 1, 1, [new(resources, times)], cards);
    }

    private static Border FindCard(ScheduleBoardControl control, WorkspaceMatchKey key) => control.GetVisualDescendants().OfType<Border>().Single(b => b.Tag is WorkspaceBoardCard item && item.Key == key);
    private static string CardText(Border card) => string.Join("\n", card.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
    private static void WithBoard(WorkspaceScheduleBoard board, Action<ScheduleBoardControl, Window> action)
    {
        var control = new ScheduleBoardControl { Board = board, CanEdit = true };
        var window = new Window { Width = 600, Height = 450, Content = control };
        try { window.Show(); window.UpdateLayout(); action(control, window); }
        finally { window.Close(); }
    }

    public void Dispose() => session.Dispose();
}
