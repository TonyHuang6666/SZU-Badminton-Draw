using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Desktop.Controls;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class ScheduleCompactLayoutTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public Task FinalDayChoiceAndExplanationAreVisibleOnlyForMultipleProjects(int count, bool visible) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture(count);
        using var model = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        var page = new ScheduleSetupPage { DataContext = model };
        var window = new Window { Content = page };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.Equal(visible, page.FindControl<CheckBox>("FinalsOnLastDay")!.IsVisible);
            var hint = Assert.Single(page.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == model.FinalDayHint);
            Assert.Equal(visible, hint.IsVisible);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task IndependentBoardKeepsGeometryStableWhenSelectionRevealsDetails(bool dark) => ui.Dispatch(async () =>
    {
        using var fixture = new ScheduleUiFixture();
        fixture.Workflow.GenerateSchedule(new([new(new(2026, 9, 19), new(9, 0), new(18, 0), ["B1", "B2"])], 2, 30, 4),
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), fixture.Workflow.CurrentSession!.Workspace.Revision);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "compact-recent.json")))
            { Width = 960, Height = 680, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleBoard);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var model = Assert.IsType<ScheduleBoardPageViewModel>(shell.CurrentPage);
            await model.InitializeAsync(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var boardWindow = Assert.Single(window.OwnedWindows);
            boardWindow.WindowState = WindowState.Normal; boardWindow.Width = 960; boardWindow.Height = 680;
            boardWindow.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs(); boardWindow.UpdateLayout();
            var page = Assert.Single(boardWindow.GetVisualDescendants().OfType<ScheduleBoardPage>());
            var board = page.FindControl<ScheduleBoardControl>("ScheduleBoard")!;
            var scroll = board.FindControl<ScrollViewer>("BoardScroll")!;
            var before = scroll.Bounds;
            var viewport = scroll.Viewport;
            model.SelectMatch(model.Matches[0].Key);
            Dispatcher.UIThread.RunJobs(); boardWindow.UpdateLayout();
            Assert.True(page.FindControl<Border>("MoveEditorPanel")!.IsVisible);
            Assert.False(page.FindControl<StackPanel>("AdjustmentFields")!.IsVisible);
            Assert.True(scroll.Viewport.Height >= 180, $"Board viewport must remain usable at 960x680, actual {scroll.Viewport}; page {page.Bounds}.");
            Assert.Equal(before, scroll.Bounds);
            Assert.Equal(viewport, scroll.Viewport);
            // Hover diagnostics must not change drag target coordinates either.
            board.FindControl<TextBlock>("Feedback")!.Text = string.Join("；", Enumerable.Repeat("目标位置与选手休息时间冲突，请选择其他场地或时间", 8));
            boardWindow.UpdateLayout();
            Assert.Equal(before, scroll.Bounds);
            Assert.Equal(viewport, scroll.Viewport);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LocatingSelectedOrRelatedMatchClosesOverlayAndPreservesPendingAdjustment(bool related) => ui.Dispatch(async () =>
    {
        using var fixture = new ScheduleUiFixture(3);
        fixture.Workflow.GenerateSchedule(new([new(new(2026, 9, 19), new(9, 0), new(18, 0), ["B1", "B2", "C1"])], 3, 30, 4),
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), fixture.Workflow.CurrentSession!.Workspace.Revision);
        var window = new AppShellWindow(fixture.Workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "locate-recent.json")))
            { Width = 960, Height = 680 };
        try
        {
            window.Show(); var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            shell.Navigate(WorkspaceRoute.ScheduleBoard); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var model = Assert.IsType<ScheduleBoardPageViewModel>(shell.CurrentPage);
            await model.InitializeAsync(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var boardWindow = Assert.Single(window.OwnedWindows);
            var view = Assert.Single(boardWindow.GetVisualDescendants().OfType<ScheduleBoardPage>());
            var other = model.Matches.First(match => match.Key != model.SelectedMatch!.Key);
            model.TargetTimeText = related ? other.Placement.StartTime.ToString("HH:mm") : "12:00";
            model.TargetCourt = related ? other.Placement.Court : "C1";
            await model.PreviewMoveCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs(); boardWindow.UpdateLayout();
            var target = (model.TargetDay, model.TargetTimeText, model.TargetCourt);
            var changes = model.PreviewChanges.ToArray(); var violations = model.PreviewViolations.ToArray();
            var canConfirm = model.ConfirmMoveCommand.CanExecute(null);
            Assert.True(model.IsMoveEditorExpanded);
            if (related) model.Issues.First(issue => issue.FocusRelatedCommand.CanExecute(null)).FocusRelatedCommand.Execute(null);
            else model.FocusSelectedCommand.Execute(null);
            Dispatcher.UIThread.RunJobs(); boardWindow.UpdateLayout();
            Assert.False(view.FindControl<Border>("MoveEditorPanel")!.IsVisible);
            Assert.False(model.IsMoveEditorExpanded);
            Assert.Equal(target, (model.TargetDay, model.TargetTimeText, model.TargetCourt));
            Assert.Equal(changes, model.PreviewChanges); Assert.Equal(violations, model.PreviewViolations);
            Assert.Equal(canConfirm, model.ConfirmMoveCommand.CanExecute(null));

            view.FindControl<Button>("ShowMatchDetails")!.Command!.Execute(null);
            Assert.True(model.IsMoveEditorExpanded);
            view.FindControl<Button>("CloseMoveEditorButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(model.IsMoveEditorExpanded);
            Assert.Equal(target, (model.TargetDay, model.TargetTimeText, model.TargetCourt));
            Assert.Equal(changes, model.PreviewChanges); Assert.Equal(violations, model.PreviewViolations);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task AdvancedSettingsRemainExpandableWhenAnEditorConflictDisablesWrites() => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture();
        var model = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        model.RefereeCountText = "2";
        var next = fixture.Workflow.CurrentSession!;
        model.RefreshSession(next with { Workspace = next.Workspace with { Resources = new([new(new(2026, 9, 19), new(9, 0), new(18, 0), ["B1"])], 1, 30, 4) } });
        Assert.False(model.CanEdit);
        var page = new ScheduleSetupPage { DataContext = model };
        var window = new Window { Width = 738, Height = 500, Content = page };
        try
        {
            window.Show(); window.UpdateLayout();
            var advanced = page.FindControl<Expander>("AdvancedScheduleSettings")!;
            Assert.True(advanced.IsEffectivelyEnabled);
            advanced.IsExpanded = true; window.UpdateLayout();
            Assert.True(advanced.IsExpanded);
            var inputs = page.GetVisualDescendants().OfType<TextBox>().ToArray();
            Assert.NotEmpty(inputs);
            Assert.All(inputs, input => Assert.False(input.IsEffectivelyEnabled));
            Assert.All(page.GetVisualDescendants().OfType<ComboBox>(), input => Assert.False(input.IsEffectivelyEnabled));
            var settings = Assert.Single(page.GetVisualDescendants().OfType<StackPanel>(), panel => panel.Classes.Contains("editable-settings"));
            Assert.All(settings.GetVisualDescendants().OfType<Button>().Where(button => button.Command is not null), button => Assert.False(button.IsEffectivelyEnabled));
            Assert.All(settings.GetVisualDescendants().OfType<Expander>(), section => Assert.True(section.IsEffectivelyEnabled));
            Assert.Equal("2", model.RefereeCountText);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
