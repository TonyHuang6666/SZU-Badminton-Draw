using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class ScheduleDiscardChangesTests
{
    [Theory]
    [InlineData("edit")]
    [InlineData("session")]
    [InlineData("dispose")]
    public async Task LateConfirmationCannotDiscardNewerInputOrAStaleEditor(string change)
    {
        using var fixture = new ScheduleUiFixture();
        var answer = new TaskCompletionSource<bool>();
        using var page = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!, confirmDiscard: () => answer.Task);
        page.MinimumRestText = "45";
        var pending = page.ResetCommand.ExecuteAsync();
        if (change == "edit") page.MinimumRestText = "50";
        else if (change == "session") page.RefreshSession(page.Session with { });
        else page.Dispose();
        answer.SetResult(true);
        await pending;
        Assert.Equal(change == "edit" ? "50" : "45", page.MinimumRestText);
    }

    [Fact]
    public async Task FailedConfirmationAfterDisposalDoesNotReplaceCurrentFeedback()
    {
        using var fixture = new ScheduleUiFixture();
        var answer = new TaskCompletionSource<bool>();
        using var page = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!, confirmDiscard: () => answer.Task);
        page.MinimumRestText = "45";
        var pending = page.ResetCommand.ExecuteAsync();
        page.Dispose();
        var status = fixture.Shell.Status;
        answer.SetException(new IOException("The old dialog has closed."));
        await pending;
        Assert.Equal(status, fixture.Shell.Status);
        Assert.Null(fixture.Shell.LastError);
    }

    [Fact]
    public async Task SuccessfulGenerationMarksSavedSettingsCleanAndCanceledDiscardKeepsFailureDetails()
    {
        using var fixture = new ScheduleUiFixture();
        fixture.Shell.RegisterPageFactory(WorkspaceRoute.ScheduleSetup, s => new ScheduleSetupPageViewModel(fixture.Shell, s, confirmDiscard: () => Task.FromResult(false)));
        fixture.Shell.Navigate(WorkspaceRoute.ScheduleSetup);
        var page = Assert.IsType<ScheduleSetupPageViewModel>(fixture.Shell.CurrentPage);
        page.Days[0].CourtsText = "B1";
        Assert.True(page.ResetCommand.CanExecute(null));
        await page.GenerateCommand.ExecuteAsync();
        Assert.NotNull(fixture.Workflow.CurrentSession!.Workspace.Schedule);
        Assert.False(page.ResetCommand.CanExecute(null));
        page.Days[0].EndText = "09:05";
        await page.GenerateCommand.ExecuteAsync();
        var failure = page.Failure;
        Assert.NotNull(failure);
        await page.ResetCommand.ExecuteAsync();
        Assert.Same(failure, page.Failure);
        Assert.Equal("09:05", page.Days[0].EndText);
    }

    [Fact]
    public void DiscardIsDisabledUntilTheDraftDiffersAndDisablesAgainWhenReverted()
    {
        using var fixture = new ScheduleUiFixture();
        using var page = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        Assert.False(page.ResetCommand.CanExecute(null));
        var original = page.MinimumRestText;
        page.MinimumRestText = "invalid";
        Assert.True(page.ResetCommand.CanExecute(null));
        page.MinimumRestText = original;
        Assert.False(page.ResetCommand.CanExecute(null));
        page.AddDayCommand.Execute(null);
        Assert.True(page.ResetCommand.CanExecute(null));
        page.Days[0].RemoveCommand.Execute(null);
        Assert.False(page.ResetCommand.CanExecute(null));
        var day = page.Days[0];
        day.AddUnavailableCommand.Execute(null);
        Assert.True(page.ResetCommand.CanExecute(null));
        day.Unavailable[0].RemoveCommand.Execute(null);
        Assert.False(page.ResetCommand.CanExecute(null));
        day.AddRefereeWindowCommand.Execute(null);
        Assert.True(page.ResetCommand.CanExecute(null));
        day.RefereeWindows[0].RemoveCommand.Execute(null);
        Assert.False(page.ResetCommand.CanExecute(null));
        page.RequireChampionshipFinalsOnLastDay = true;
        Assert.True(page.ResetCommand.CanExecute(null));
        page.RequireChampionshipFinalsOnLastDay = false;
        Assert.False(page.ResetCommand.CanExecute(null));
    }
}

[Collection("Avalonia UI dispatcher")]
public sealed class ScheduleDiscardChangesViewTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData("discard", true)]
    [InlineData("continue", false)]
    [InlineData("escape", false)]
    [InlineData("enter", false)]
    [InlineData("close", false)]
    public Task OnlyExplicitConfirmationRestoresSettingsWithoutChangingTheArchive(string action, bool discard) => ui.Dispatch(() =>
    {
        using var fixture = new ScheduleUiFixture();
        var workflow = fixture.Workflow;
        workflow.GenerateSchedule(new([new(new(2026, 10, 3), new(14, 0), new(18, 0), ["B1", "B2"],
            [new(new(16, 0), new(17, 0), 1)], [new(new(15, 0), new(16, 0), ["B1"])])], 2, 20, 4),
            new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []), workflow.CurrentSession!.Workspace.Revision);
        var session = workflow.CurrentSession!;
        var bytes = File.ReadAllBytes(session.WorkspacePath);
        var window = new AppShellWindow(workflow, new RecentWorkspaceStore(Path.Combine(fixture.DirectoryPath, "window-recent.json")));
        try
        {
            window.Show();
            var shell = Assert.IsType<AppShellViewModel>(window.DataContext);
            Assert.True(shell.Navigate(WorkspaceRoute.ScheduleSetup));
            var page = Assert.IsType<ScheduleSetupPageViewModel>(shell.CurrentPage);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, page.ResetCommand));
            Assert.False(button.IsEffectivelyEnabled);
            page.Days[0].DateText = "2026-10-06";
            page.Days[0].StartText = "09:00";
            page.Days[0].CourtsText = "C1";
            page.Days[0].Unavailable[0].StartText = "14:30";
            page.Days[0].RefereeWindows[0].CountText = "0";
            page.MinimumRestText = "45";
            page.ProjectTimings[0].MinutesText = "20";
            page.RequireChampionshipFinalsOnLastDay = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsEffectivelyEnabled);
            button.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.Equal("20", page.ProjectTimings[0].MinutesText);
            Assert.Same(session, workflow.CurrentSession);
            Assert.Equal(bytes, File.ReadAllBytes(session.WorkspacePath));
            var keep = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "继续编辑"));
            Assert.True(keep.IsFocused);
            if (action is "discard" or "continue")
                dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, action == "discard" ? "放弃修改" : "继续编辑"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if (action == "close") dialog.Close();
            else dialog.KeyPress(action == "escape" ? Key.Escape : Key.Enter, RawInputModifiers.None,
                action == "escape" ? PhysicalKey.Escape : PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.OwnedWindows);
            Assert.Equal(discard ? "2026-10-03" : "2026-10-06", page.Days[0].DateText);
            Assert.Equal(discard ? "14:00" : "09:00", page.Days[0].StartText);
            Assert.Equal(discard ? "B1, B2" : "C1", page.Days[0].CourtsText);
            Assert.Equal(discard ? "15:00" : "14:30", page.Days[0].Unavailable[0].StartText);
            Assert.Equal(discard ? "1" : "0", page.Days[0].RefereeWindows[0].CountText);
            Assert.Equal(discard ? "20" : "45", page.MinimumRestText);
            Assert.Equal(discard ? "30" : "20", page.ProjectTimings[0].MinutesText);
            Assert.Equal(!discard, page.RequireChampionshipFinalsOnLastDay);
            Assert.Equal(!discard, button.IsEffectivelyEnabled);
            Assert.Same(session, workflow.CurrentSession);
            Assert.Equal(bytes, File.ReadAllBytes(session.WorkspacePath));
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
