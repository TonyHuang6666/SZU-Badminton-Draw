using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Desktop.Controls;
using BadmintonDraw.Desktop.Scheduling;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class ScheduleBoardControlTests : IDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(BoardTestApplication));
    [Theory]
    [InlineData("ClearHover")]
    [InlineData("ClearDragState")]
    public Task EndingHoverOrDragClearsPendingFeedbackAndExplainsActualSaveBehavior(string cleanup) => session.Dispatch(() =>
    {
        var control = new ScheduleBoardControl();
        var feedback = control.FindControl<TextBlock>("Feedback")!;
        feedback.Text = "正在检查目标位置…";

        // Exercise the same cleanup paths used by cell drop/leave and native drag completion.
        typeof(ScheduleBoardControl).GetMethod(cleanup, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, null);

        Assert.DoesNotContain("正在检查", feedback.Text);
        Assert.Contains("自动保存", feedback.Text);
        Assert.Contains("手动和连锁", feedback.Text);
    }, CancellationToken.None);

    [Theory]
    [InlineData(.7)]
    [InlineData(1)]
    public Task CrossDayFocusAndRepeatedLocateKeepTheCardInsideTheActualViewport(double zoom) => session.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(3);
        fixture.Workflow.GenerateSchedule(new([new(new(2026, 9, 19), new(9, 0), new(18, 0), ["B1", "B2", "C1"]),
            new(new(2026, 9, 23), new(9, 0), new(18, 0), ["B1", "B2", "C1"])], 2, 30, 4),
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), fixture.Workflow.CurrentSession!.Workspace.Revision);
        var key = WorkspaceScheduleBoard.Build(fixture.Workflow.CurrentSession!).Cards.Last().Key;
        fixture.Workflow.MoveMatch(new(key, "2026-09-23", new(15, 0), "B2", fixture.Workflow.CaptureScheduleEditBaseline()), fixture.Workflow.CurrentSession!.Workspace.Revision);
        var board = new ScheduleBoardControl { Board = WorkspaceScheduleBoard.Build(fixture.Workflow.CurrentSession!), SelectedDay = "2026-09-19", Zoom = zoom, CanEdit = true };
        var window = new Window { Width = 600, Height = 450, Content = board };
        try
        {
            window.Show(); window.UpdateLayout();
            var scroll = board.FindControl<ScrollViewer>("BoardScroll")!;
            scroll.Offset = new(0, scroll.Viewport.Height / 2); window.UpdateLayout();
            Assert.True(scroll.Offset.Y > 0);

            board.FocusMatch(key); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal("2026-09-23", board.SelectedDay);
            var card = board.GetVisualDescendants().OfType<Border>().Single(c => c.Tag is WorkspaceBoardCard item && item.Key == key);
            Assert.True(card.IsFocused);
            AssertCardVisible(card, scroll);

            scroll.ScrollToHome(); window.UpdateLayout();
            Assert.True(card.IsFocused); // Repeated location must work without a new GotFocus event.
            board.FocusMatch(key); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            AssertCardVisible(card, scroll);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    private static void AssertCardVisible(Border card, ScrollViewer scroll)
    {
        var presenter = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        var transform = card.TransformToVisual(presenter);
        Assert.NotNull(transform);
        var bounds = new Rect(card.Bounds.Size).TransformToAABB(transform.Value);
        var viewport = new Rect(presenter.Bounds.Size);
        Assert.True(viewport.Contains(bounds), $"Target card {bounds} must be inside actual viewport {viewport}; offset={scroll.Offset}.");
    }

    public void Dispose() => session.Dispose();

    public sealed class BoardTestApplication : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }
}
