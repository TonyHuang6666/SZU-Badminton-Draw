using System.ComponentModel;
using BadmintonDraw.Desktop.ViewModels;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class OnsiteRecordViewModelTests
{
    [Fact]
    public async Task CompletedRowSavesOnceAndPreservesOtherDraftsAndSchedule()
    {
        using var f = new Fixture();
        var ready = f.Vm.Rows.Where(r => r.CanEdit).Take(2).ToArray();
        var first = ready[0]; var second = ready[1];
        var times = f.Vm.Rows.Select(r => (r.Key, r.Day, r.Time, r.Court)).ToArray();
        second.Score = "21-9";
        Fill(first);
        Assert.True(await f.Vm.SaveRowAsync(first));
        Assert.Single(f.Data.Shell.CurrentSession!.Workspace.Results);
        Assert.False(first.HasDraft); Assert.True(first.IsSaved);
        Assert.Equal("21-9", second.Score); Assert.True(second.HasDraft);
        Assert.Same(first, f.Vm.Rows.Single(r => r.Key == first.Key));
        Assert.Equal(times, f.Vm.Rows.Select(r => (r.Key, r.Day, r.Time, r.Court)));
        Assert.True(await f.Vm.SaveRowAsync(first));
        Assert.Equal(2, f.Data.Data.Store.Mutations); // Outcome, then optional metadata supplementation.
    }

    [Fact]
    public async Task IncompleteInvalidAndFailedWritesKeepDraftAndBlockFlush()
    {
        using var f = new Fixture(); var row = f.Vm.Rows.First(r => r.CanEdit);
        row.Score = "21-10";
        Assert.False(await f.Vm.FlushAsync());
        Assert.Empty(f.Data.Shell.CurrentSession!.Workspace.Results);
        row.Score = "10-21"; row.Duration = "18"; row.WinnerIndex = 1; row.KindIndex = 0;
        await f.Vm.WaitForAutomaticSavesAsync();
        Assert.False(await f.Vm.SaveRowAsync(row)); Assert.True(row.HasDraft);
        Assert.Single(f.Data.Shell.CurrentSession!.Workspace.Results); // Invalid optional fields cannot prevent outcome save.
        row.Score = "21-10"; f.Data.Files.Armed = true; f.Data.Files.FailPublish = true;
        Assert.False(await f.Vm.SaveRowAsync(row));
        Assert.True(row.HasDraft); Assert.False(row.IsSaved);
        Assert.Equal("21-10", row.Score);
        f.Data.Files.FailPublish = false;
        Assert.True(await f.Vm.FlushAsync()); Assert.False(f.Vm.HasDrafts);
    }

    [Fact]
    public async Task ConcurrentSaveRequestsAreSerializedAndAdvanceUnresolvedMatch()
    {
        using var f = new Fixture(1);
        var ready = f.Vm.Rows.Where(r => r.CanEdit).ToArray();
        var final = f.Vm.Rows.Single(r => !r.CanEdit);
        Assert.Equal(2, ready.Length);
        foreach (var row in ready) Fill(row);
        Assert.All(await Task.WhenAll(ready.Select(f.Vm.SaveRowAsync)), Assert.True);
        Assert.Equal(2, f.Data.Shell.CurrentSession!.Workspace.Results.Count);
        Assert.True(final.CanEdit); Assert.DoesNotContain("胜者", final.SideA);
        Assert.DoesNotContain("胜者", final.SideB);
        Assert.False(f.Vm.ShowProjectFilter);
    }

    [Fact]
    public async Task SavedMetadataNeedsReasonAndForeignEditsCannotBeOverwritten()
    {
        using var f = new Fixture(1); var row = f.Vm.Rows.First(r => r.CanEdit);
        Fill(row); Assert.True(await f.Vm.SaveRowAsync(row));
        row.Duration = "25";
        Assert.DoesNotContain("已保存", f.Vm.StateMessage);
        Assert.False(await f.Vm.SaveRowAsync(row));
        row.AllowCorrections = true; row.CorrectionReason = "核对计分表";
        Assert.True(await f.Vm.SaveRowAsync(row));
        Assert.Equal(25, f.Data.Shell.CurrentSession!.Workspace.Results[row.Key].DurationMinutes);
        row.Duration = "30";
        var path = f.Data.Data.Export("foreign.xlsx", keys: [row.Key]);
        BadmintonDraw.Tests.WorkspaceResultImportFacadeFixture.Edit(path, s => s.Cell(6, 10).Value = 22);
        await f.Data.Preview(path);
        f.Data.ViewModel.AllowCorrections = true; f.Data.ViewModel.CorrectionReason = "另一条录入途径";
        await f.Data.Accept();
        Assert.False(await f.Vm.SaveRowAsync(row)); Assert.True(row.HasDraft);
        Assert.Equal("30", row.Duration);
        f.Vm.DiscardDrafts(); Assert.Equal("22", row.Duration); Assert.False(f.Vm.HasDrafts);
    }

    [Fact]
    public void FiltersPreserveRowsAndDiscardOnlyDrafts()
    {
        using var f = new Fixture(); var row = f.Vm.Rows.First(r => r.CanEdit);
        row.Score = "21-10"; f.Vm.SearchText = "__no_match__";
        Assert.Empty(f.Vm.VisibleRows); Assert.True(f.Vm.HasDrafts);
        f.Vm.SearchText = ""; Assert.Contains(row, f.Vm.VisibleRows);
        f.Vm.DiscardDrafts(); Assert.False(f.Vm.HasDrafts); Assert.Equal("", row.Score);
        Assert.True(f.Vm.ShowProjectFilter);
    }

    [Fact]
    public void RemovingSchedulePreservesDraftUntilExplicitlyDiscarded()
    {
        using var f = new Fixture(1); var row = f.Vm.Rows.First(r => r.CanEdit);
        row.Score = "21-10";
        var session = f.Data.Shell.CurrentSession!;
        f.Vm.RefreshSession(session with { Workspace = session.Workspace with { Schedule = null } });
        Assert.Contains(row, f.Vm.Rows); Assert.Equal("21-10", row.Score);
        Assert.True(f.Vm.HasDrafts); Assert.False(row.CanEdit);
        f.Vm.DiscardRow(row); Assert.Empty(f.Vm.Rows); Assert.False(f.Vm.HasDrafts);
    }

    [Fact]
    public void SimultaneousRowsUseConfiguredCourtOrderLikeExportedRecordTable()
    {
        using var f = new Fixture(1);
        var session = f.Data.Shell.CurrentSession!; var schedule = session.Workspace.Schedule!;
        f.Vm.RefreshSession(session with { Workspace = session.Workspace with
        {
            Schedule = schedule with { Resources = schedule.Resources with
            { Days = schedule.Resources.Days.Select(day => day with { Courts = new[] { "B2", "B1" } }).ToArray() } }
        } });
        Assert.Equal(new[] { "B2", "B1" }, f.Vm.Rows.Take(2).Select(row => row.Court));
    }

    [Fact]
    public async Task RepeatedSimultaneousSaveDoesNotDuplicatePublication()
    {
        using var f = new Fixture(1); var row = f.Vm.Rows.First(r => r.CanEdit);
        row.WinnerIndex = 1; row.KindIndex = 0;
        var results = await Task.WhenAll(f.Vm.SaveRowAsync(row), f.Vm.SaveRowAsync(row), f.Vm.SaveRowAsync(row));
        Assert.All(results, Assert.True); Assert.Equal(1, f.Data.Data.Store.Mutations);
        Assert.Single(f.Data.Shell.CurrentSession!.Workspace.ImportLogs);
    }

    [Fact]
    public async Task UnreadableCommittedSaveBlocksFurtherEditingAndPreservesEvidence()
    {
        using var f = new Fixture(1); var row = f.Vm.Rows.First(r => r.CanEdit);
        f.Data.Files.Armed = true; f.Data.Store.FailCommittedRead = true;
        Fill(row); await f.Vm.WaitForAutomaticSavesAsync();
        Assert.True(f.Data.Files.Published); Assert.False(f.Vm.CanEdit); Assert.False(row.CanEdit);
        Assert.True(row.HasDraft); Assert.Contains("保存已写入", row.Status);
        Assert.Equal("21-10", row.Score);
    }

    private static void Fill(OnsiteRecordRowViewModel row)
    { row.Score = "21-10"; row.Duration = "18"; row.WinnerIndex = 1; row.KindIndex = 0; }

    private sealed class Fixture : IDisposable
    {
        internal ResultImportUiFixture Data { get; }
        internal OnsiteRecordViewModel Vm { get; }
        internal Fixture(int projects = 2)
        {
            Data = new(projects); Vm = new(Data.Shell, Data.Shell.CurrentSession!);
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
