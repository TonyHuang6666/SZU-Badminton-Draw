using System.ComponentModel;
using BadmintonDraw.Desktop.ViewModels;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class OnsiteDayConfirmationViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditsDuringPreviewRequireANewPreview(bool revokeApproval)
    {
        using var f = new Fixture();
        var model = Assert.IsType<OnsiteDayConfirmationViewModel>(await f.Vm.PrepareDayConfirmationAsync());
        foreach (var row in model.PendingMatches) { row.IsPostponed = true; row.Reason = "天气"; }
        using var releaseRead = new ManualResetEventSlim();
        var readStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Data.Store.BeforeRead = _ =>
        {
            readStarted.TrySetResult(true);
            if (!releaseRead.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Preview read was not released.");
        };
        var pendingPreview = model.PreviewAsync();
        try
        {
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (revokeApproval) model.PendingMatches[0].IsPostponed = false;
            else model.PendingMatches[0].Reason = "场馆关闭";
        }
        finally
        {
            releaseRead.Set();
            await pendingPreview;
            f.Data.Store.BeforeRead = null;
        }

        Assert.False(model.CanConfirm);
        Assert.False(model.HasPreview);
        Assert.Empty(model.Changes);
        await model.ConfirmAsync();
        Assert.Empty(f.Data.Shell.CurrentSession!.Workspace.DayConfirmations);
        if (revokeApproval) model.PendingMatches[0].IsPostponed = true;
        await model.PreviewAsync();
        Assert.True(model.CanConfirm, model.StateMessage);
    }

    [Fact]
    public async Task ExplicitPostponementPreviewAndConfirmUnlockNextDayAndPersist()
    {
        using var f = new Fixture();
        var model = Assert.IsType<OnsiteDayConfirmationViewModel>(await f.Vm.PrepareDayConfirmationAsync());
        Assert.Equal(3, model.PendingMatches.Count);
        Assert.False(model.CanPreview); Assert.False(model.CanConfirm);
        foreach (var row in model.PendingMatches) { row.IsPostponed = true; row.Reason = "场馆临时关闭"; }
        Assert.True(model.CanPreview);
        await model.PreviewAsync();
        Assert.True(model.CanConfirm, model.StateMessage);
        Assert.Equal(3, model.Changes.Count);
        Assert.Empty(f.Data.Shell.CurrentSession!.Workspace.DayConfirmations);
        model.PendingMatches[0].Reason = "暴雨";
        Assert.False(model.CanConfirm); Assert.False(model.HasPreview);
        await model.PreviewAsync(); await model.ConfirmAsync();
        Assert.True(model.IsCompleted, model.StateMessage);
        Assert.Equal(new[] { "2026-09-13", "2026-09-14" }, f.Vm.DayOptions);
        Assert.Equal("2026-09-14", f.Vm.SelectedDay);
        Assert.Single(f.Data.Shell.CurrentSession!.Workspace.DayConfirmations);
        Assert.All(f.Vm.VisibleRows, row => Assert.Equal("2026-09-14", row.ActualDay));
        f.Vm.SelectedDay = "2026-09-13";
        Assert.False(f.Vm.CanConfirmDay); Assert.Empty(f.Vm.VisibleRows);
        Assert.Contains("经批准延期 3 场", f.Vm.DaySummary);
    }

    [Fact]
    public async Task AllCompletedStillRequiresExplicitConfirmationThenMetadataCanBeSupplemented()
    {
        using var f = new Fixture();
        for (var i = 0; i < 3; i++)
        {
            var row = f.Vm.Rows.First(r => r.CanEdit && !r.HasSavedResult);
            row.WinnerIndex = 1; row.KindIndex = 0;
            await f.Vm.WaitForAutomaticSavesAsync();
        }
        Assert.Single(f.Vm.DayOptions);
        var confirmation = Assert.IsType<OnsiteDayConfirmationViewModel>(await f.Vm.PrepareDayConfirmationAsync());
        Assert.Empty(confirmation.PendingMatches); Assert.True(confirmation.CanPreview);
        await confirmation.PreviewAsync(); await confirmation.ConfirmAsync();
        Assert.True(confirmation.IsCompleted, confirmation.StateMessage);
        f.Vm.SelectedDay = "2026-09-13";
        var saved = f.Vm.VisibleRows.First(); saved.Score = "21-10"; saved.Duration = "18";
        Assert.True(await f.Vm.SaveRowAsync(saved), saved.Status);
        Assert.Single(f.Data.Shell.CurrentSession!.Workspace.DayConfirmations);
    }

    [Fact]
    public async Task InvalidDraftAndFailedCommitDoNotAdvanceDay()
    {
        using var f = new Fixture();
        var first = f.Vm.Rows.First(r => r.CanEdit); first.Score = "draft";
        Assert.Null(await f.Vm.PrepareDayConfirmationAsync());
        f.Vm.DiscardRow(first);
        var confirmation = Assert.IsType<OnsiteDayConfirmationViewModel>(await f.Vm.PrepareDayConfirmationAsync());
        foreach (var row in confirmation.PendingMatches) { row.IsPostponed = true; row.Reason = "天气"; }
        await confirmation.PreviewAsync(); Assert.True(confirmation.CanConfirm, confirmation.StateMessage);
        f.Data.Files.Armed = true; f.Data.Files.FailPublish = true;
        await confirmation.ConfirmAsync();
        Assert.False(confirmation.IsCompleted);
        Assert.Single(f.Vm.DayOptions); Assert.Equal("2026-09-13", f.Vm.SelectedDay);
        Assert.Empty(f.Data.Shell.CurrentSession!.Workspace.DayConfirmations);
    }

    private sealed class Fixture : IDisposable
    {
        internal ResultImportUiFixture Data { get; } = new(1);
        internal OnsiteRecordViewModel Vm { get; }
        internal Fixture()
        {
            Vm = new(Data.Shell, Data.Shell.CurrentSession!);
            Data.Shell.PropertyChanged += Changed;
        }
        private void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Data.Shell.CurrentSession)) Vm.RefreshSession(Data.Shell.CurrentSession!);
            if (e.PropertyName == nameof(Data.Shell.IsBusy)) Vm.RefreshAvailability();
        }
        public void Dispose() { Data.Shell.PropertyChanged -= Changed; Vm.Dispose(); Data.Dispose(); }
    }
}
