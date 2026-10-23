using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Persistence;
using BadmintonDraw.Tests;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class OnsiteRecordWindowTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Fact]
    public Task OperationsOpensOneNonmodalRecordWindowAndReopeningKeepsFilters() => ui.Dispatch(() =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1);
        var owner = CreateWindow(data);
        try
        {
            owner.Show(); var shell = (AppShellViewModel)owner.DataContext!;
            var sheet = Open(shell, owner); var model = (OnsiteRecordViewModel)sheet.DataContext!;
            Assert.True(owner.IsEnabled);
            Assert.False(sheet.FindControl<ComboBox>("OnsiteProject")!.IsVisible);
            Assert.Same(sheet, Open(shell, owner));
            model.SearchText = "选手";
            sheet.Close(); Assert.True(owner.IsVisible); Assert.Empty(owner.OwnedWindows);
            var reopened = Open(shell, owner);
            Assert.Same(model, reopened.DataContext); Assert.Equal("选手", model.SearchText);
        }
        finally { owner.Close(); }
        return Task.FromResult(0);
    }, CancellationToken.None);

    [Fact]
    public Task EnterSupplementsSavedResultOnceAndKeepsMainPageOpen() => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1);
        var owner = CreateWindow(data);
        try
        {
            owner.Show(); var shell = (AppShellViewModel)owner.DataContext!;
            var sheet = Open(shell, owner); var model = (OnsiteRecordViewModel)sheet.DataContext!;
            var row = model.VisibleRows.First(r => r.CanEdit);
            await SaveRequiredAsync(model, row);
            var score = Cell<TextBox>(sheet, row, "OnsiteScore"); score.Focus();
            FillMetadata(row); var revision = data.Revision;
            sheet.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UntilAsync(() => row.IsSaved && !model.IsWorking);
            Assert.Equal(revision + 1, data.Revision);
            Assert.False(row.HasDraft); Assert.True(sheet.IsVisible); Assert.True(owner.IsVisible);
            Assert.IsType<OperationsPageViewModel>(shell.CurrentPage);
            sheet.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await model.FlushAsync(); Assert.Equal(revision + 1, data.Revision);
        }
        finally { DiscardAndClose(owner); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task MovingBetweenCellsDoesNotCommitButLeavingTheRowDoes() => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1);
        var owner = CreateWindow(data);
        try
        {
            owner.Show(); var shell = (AppShellViewModel)owner.DataContext!;
            var sheet = Open(shell, owner); var model = (OnsiteRecordViewModel)sheet.DataContext!;
            var row = model.VisibleRows.First(r => r.CanEdit);
            await SaveRequiredAsync(model, row);
            var score = Cell<TextBox>(sheet, row, "OnsiteScore"); score.Focus(); FillMetadata(row);
            var revision = data.Revision;
            Cell<TextBox>(sheet, row, "OnsiteDuration").Focus(); Layout(sheet);
            Assert.True(row.HasDraft); Assert.Equal(revision, data.Revision);
            sheet.FindControl<TextBox>("OnsiteSearch")!.Focus();
            await UntilAsync(() => row.IsSaved && !model.IsWorking);
            Assert.Equal(revision + 1, data.Revision);
        }
        finally { DiscardAndClose(owner); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task OpeningAnotherRowsDropdownDoesNotSuppressTheCompletedRowsAutoSave() => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1);
        var owner = CreateWindow(data);
        try
        {
            owner.Show(); var shell = (AppShellViewModel)owner.DataContext!;
            var sheet = Open(shell, owner); var model = (OnsiteRecordViewModel)sheet.DataContext!;
            var ready = model.VisibleRows.Where(r => r.CanEdit).Take(2).ToArray();
            await SaveRequiredAsync(model, ready[0]);
            Cell<TextBox>(sheet, ready[0], "OnsiteDuration").Focus(); FillMetadata(ready[0]);
            var revision = data.Revision;
            var nextWinner = Cell<ComboBox>(sheet, ready[1], "OnsiteWinner");
            nextWinner.Focus(); nextWinner.IsDropDownOpen = true; Layout(sheet);
            nextWinner.IsDropDownOpen = false;
            await UntilAsync(() => ready[0].IsSaved && !model.IsWorking);
            Assert.Equal(revision + 1, data.Revision);
            Assert.False(ready[1].HasDraft);
        }
        finally { DiscardAndClose(owner); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task IncompleteDraftPreventsWindowAndTournamentClosingUntilExplicitlyDiscarded() => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1);
        using var second = new WorkspaceResultImportFacadeFixture(1);
        var owner = CreateWindow(data);
        try
        {
            owner.Show(); var shell = (AppShellViewModel)owner.DataContext!;
            var sheet = Open(shell, owner); var model = (OnsiteRecordViewModel)sheet.DataContext!;
            var row = model.VisibleRows.First(r => r.CanEdit); row.Score = "21-10";
            var revision = data.Revision;
            Assert.False(await sheet.FlushForCloseAsync());
            sheet.Close(); Layout(sheet);
            Assert.True(sheet.IsVisible); Assert.True(row.HasDraft); Assert.Equal(revision, data.Revision);
            Assert.True(sheet.FindControl<TextBlock>("OnsiteCloseWarning")!.IsVisible);
            Assert.False(await shell.OpenWorkspaceAsync(second.Session.WorkspacePath));
            Assert.Equal(data.Session.WorkspacePath, shell.CurrentSession!.WorkspacePath);
            owner.Close(); Layout(owner); Assert.True(owner.IsVisible);
            sheet.FindControl<Button>("OnsiteDiscardDrafts")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(model.HasDrafts);
            Assert.True(await shell.OpenWorkspaceAsync(second.Session.WorkspacePath));
            Layout(owner); Assert.False(sheet.IsVisible);
        }
        finally { DiscardAndClose(owner); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task ClosingAnOptionalMetadataDraftWaitsForItsSaveBeforeClosing() => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1);
        var owner = CreateWindow(data);
        try
        {
            owner.Show(); var shell = (AppShellViewModel)owner.DataContext!;
            var sheet = Open(shell, owner); var model = (OnsiteRecordViewModel)sheet.DataContext!;
            var row = model.VisibleRows.First(r => r.CanEdit);
            await SaveRequiredAsync(model, row);
            FillMetadata(row);
            var revision = data.Revision; sheet.Close();
            await UntilAsync(() => !sheet.IsVisible);
            Assert.Equal(revision + 1, data.Revision); Assert.False(model.HasDrafts);
            Assert.True(owner.IsVisible);
        }
        finally { DiscardAndClose(owner); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task SelectingWinnerAndKindControlsSavesWithoutEnterOrFocusChange() => ui.Dispatch(async () =>
    {
        using var data = new WorkspaceResultImportFacadeFixture(1);
        var owner = CreateWindow(data);
        try
        {
            owner.Show(); var shell = (AppShellViewModel)owner.DataContext!;
            var sheet = Open(shell, owner); var model = (OnsiteRecordViewModel)sheet.DataContext!;
            var row = model.VisibleRows.First(r => r.CanEdit);
            var winner = Cell<ComboBox>(sheet, row, "OnsiteWinner");
            var kind = Cell<ComboBox>(sheet, row, "OnsiteKind");
            Assert.Equal(-1, kind.SelectedIndex);
            var revision = data.Revision;
            winner.SelectedIndex = 1;
            Assert.Equal(revision, data.Revision);
            kind.Focus(); kind.SelectedIndex = 0;
            await model.WaitForAutomaticSavesAsync();
            Assert.Same(kind, sheet.FocusManager!.GetFocusedElement());
            Assert.Equal(revision + 1, data.Revision);
            Assert.True(row.IsSaved); Assert.False(row.HasDraft);
            var result = shell.CurrentSession!.Workspace.Results[row.Key];
            Assert.Equal("", result.Score); Assert.Null((object?)result.DurationMinutes);
        }
        finally { DiscardAndClose(owner); }
        return 0;
    }, CancellationToken.None);

    private static void FillMetadata(OnsiteRecordRowViewModel row)
    { row.Score = "21-10"; row.Duration = "20"; }

    private static async Task SaveRequiredAsync(OnsiteRecordViewModel model, OnsiteRecordRowViewModel row)
    {
        row.WinnerIndex = 1; row.KindIndex = 0;
        await model.WaitForAutomaticSavesAsync();
        Assert.True(row.IsSaved); Assert.False(row.HasDraft);
    }

    private static AppShellWindow CreateWindow(WorkspaceResultImportFacadeFixture data) =>
        new(data.Workflow, new RecentWorkspaceStore(data.PathFor("onsite-window-recent.json")));

    private static OnsiteRecordWindow Open(AppShellViewModel shell, AppShellWindow owner)
    {
        Assert.True(shell.Navigate(WorkspaceRoute.Operations));
        Assert.IsType<OperationsPageViewModel>(shell.CurrentPage).OpenOnsiteRecordsCommand.Execute(null);
        var sheet = Assert.Single(owner.OwnedWindows.OfType<OnsiteRecordWindow>()); Layout(sheet);
        return sheet;
    }

    private static T Cell<T>(Window window, OnsiteRecordRowViewModel row, string name) where T : Control =>
        Assert.Single(window.GetVisualDescendants().OfType<T>(), c => c.Name == name && ReferenceEquals(c.DataContext, row));

    private static void Layout(Window window)
    { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }

    private static async Task UntilAsync(Func<bool> complete)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!complete() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs(); Assert.True(complete());
    }

    private static void DiscardAndClose(AppShellWindow owner)
    {
        foreach (var child in owner.OwnedWindows.OfType<OnsiteRecordWindow>())
            (child.DataContext as OnsiteRecordViewModel)?.DiscardDrafts();
        owner.Close();
    }

    public void Dispose() => ui.Dispose();
}
