using System.ComponentModel;
using BadmintonDraw.Desktop.ViewModels;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class OnsiteImmediateResultTests
{
    [Fact]
    public async Task SelectingBothRequiredChoicesSavesWithoutScoreDurationOrFocusChange()
    {
        using var f = new Fixture();
        var row = f.Vm.Rows.First(r => r.CanEdit);
        Assert.Equal(-1, row.KindIndex);
        row.WinnerIndex = 1;
        Assert.Empty(f.Data.Shell.CurrentSession!.Workspace.Results);
        row.KindIndex = 0;
        await f.Vm.WaitForAutomaticSavesAsync();
        var result = Assert.Single(f.Data.Shell.CurrentSession!.Workspace.Results).Value;
        Assert.Equal("", result.Score);
        Assert.Null((object?)result.DurationMinutes);
        Assert.True(row.IsSaved);
        Assert.Equal("", row.Duration);
    }

    [Fact]
    public async Task InvalidOptionalDraftDoesNotPreventSavingWinnerAndKind()
    {
        using var f = new Fixture();
        var row = f.Vm.Rows.First(r => r.CanEdit);
        row.Score = "unfinished";
        row.Duration = "?";
        row.KindIndex = 0;
        row.WinnerIndex = 1;
        await f.Vm.WaitForAutomaticSavesAsync();
        Assert.Single(f.Data.Shell.CurrentSession!.Workspace.Results);
        Assert.True(row.HasSavedResult);
        Assert.True(row.HasDraft);
        Assert.Equal("unfinished", row.Score);
        Assert.Equal("?", row.Duration);
        Assert.False(await f.Vm.FlushAsync());
    }

    [Fact]
    public async Task BlankMetadataCanBeSupplementedWithoutCorrectionConsent()
    {
        using var f = new Fixture();
        var row = f.Vm.Rows.First(r => r.CanEdit);
        row.KindIndex = 0; row.WinnerIndex = 1;
        await f.Vm.WaitForAutomaticSavesAsync();
        row.Score = "21-10"; row.Duration = "18";
        Assert.False(row.NeedsCorrectionConsent);
        Assert.True(await f.Vm.SaveRowAsync(row));
        Assert.Equal(18, f.Data.Shell.CurrentSession!.Workspace.Results[row.Key].DurationMinutes);
        row.Duration = "20";
        Assert.True(row.NeedsCorrectionConsent);
        Assert.False(await f.Vm.SaveRowAsync(row));
    }

    [Fact]
    public void NativeDateChoicesDoNotExposeFutureDaysOrAllDates()
    {
        using var f = new Fixture();
        Assert.Equal(new[] { "2026-09-13" }, f.Vm.DayOptions);
        Assert.Equal("2026-09-13", f.Vm.SelectedDay);
        f.Vm.SelectedDay = "全部日期";
        Assert.Equal("2026-09-13", f.Vm.SelectedDay);
        f.Vm.SelectedDay = "2026-09-14";
        Assert.Equal("2026-09-13", f.Vm.SelectedDay);
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
