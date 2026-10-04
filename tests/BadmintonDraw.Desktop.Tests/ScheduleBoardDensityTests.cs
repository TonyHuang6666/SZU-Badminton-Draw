using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
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

    // Repeated locate actions must not permanently replace the project's border with the focus accent.
    [Fact]
    public Task RepeatedLocateRestoresProjectBorderAfterHighlight() => session.Dispatch(async () =>
    {
        var item = Card(new(9, 0), new(9, 30));
        var control = new ScheduleBoardControl { Board = Board(item) };
        var window = new Window { Width = 600, Height = 450, Content = control };
        try
        {
            window.Show(); window.UpdateLayout();
            // Select the current day before keeping a reference, because locate also selects its date.
            control.SelectedDay = "2026-10-04"; window.UpdateLayout();
            var card = FindCard(control, item.Key);
            var original = ((ISolidColorBrush)card.BorderBrush!).Color;
            var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void BorderChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
            {
                if (change.Property == Border.BorderBrushProperty && card.BorderBrush is ISolidColorBrush brush && brush.Color == original)
                    restored.TrySetResult();
            }
            card.PropertyChanged += BorderChanged;
            try
            {
                control.FocusMatch(item.Key); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.NotEqual(original, ((ISolidColorBrush)card.BorderBrush!).Color);
                control.FocusMatch(item.Key); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.NotEqual(original, ((ISolidColorBrush)card.BorderBrush!).Color);
                // UI timer callbacks can be delayed by other work. Observe restoration, not elapsed wall time.
                await restored.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Equal(original, ((ISolidColorBrush)card.BorderBrush!).Color);
            }
            finally { card.PropertyChanged -= BorderChanged; }
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

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
