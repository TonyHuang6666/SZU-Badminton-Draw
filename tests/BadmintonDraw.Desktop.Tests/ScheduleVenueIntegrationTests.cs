using BadmintonDraw.Desktop.Navigation;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Persistence;
using System.Security.Cryptography;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class ScheduleVenueIntegrationTests
{
    [Fact]
    public void NewDaysDoNotAssumeAnyCourtsAreBooked()
    {
        using var fixture = new ScheduleUiFixture();
        var page = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!);
        Assert.Empty(page.Days[0].Build().Courts);
        Assert.Null(page.CapacityEstimate);
        page.Days[0].CourtsText = "市民体育中心 · 1号场";
        page.AddDayCommand.Execute(null);
        Assert.Empty(page.Days[0].Build().Courts);
        Assert.Null(fixture.Workflow.CurrentSession!.Workspace.Resources);
    }

    [Fact]
    public void ExistingFreeTextNamesAreNotSilentlyAssignedToASzuVenue()
    {
        var day = new ScheduleDayEditorViewModel(new(new(2026, 10, 3), new(14, 0), new(18, 0), ["B1", "1号场", "中央场"]),
            () => { }, _ => { });
        Assert.Equal(["B1", "1号场", "中央场"], day.Build().Courts);
    }

    [Fact]
    public async Task AcceptingVenueOnlyEditsDraftUntilScheduleGenerationAndNamesSurviveReopen()
    {
        using var fixture = new ScheduleUiFixture();
        var path = fixture.Workflow.CurrentSession!.WorkspacePath;
        var bytes = SHA256.HashData(File.ReadAllBytes(path));
        var page = Page(fixture, model => { Select(model, 1, "1号场", "10号场"); return Task.FromResult(true); });
        await page.Days[0].ChooseCourtsCommand.ExecuteAsync();
        Assert.Equal(["丽湖至快 · 1号场", "丽湖至快 · 10号场"], page.Days[0].Build().Courts);
        Assert.Contains("至快体育馆", page.Days[0].VenueSummary);
        Assert.Equal(bytes, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Null(fixture.Workflow.CurrentSession.Workspace.Schedule);
        await page.GenerateCommand.ExecuteAsync();
        Assert.Null(fixture.Shell.LastError);
        var saved = new TournamentWorkspaceStore().Read(path);
        Assert.Equal(["丽湖至快 · 1号场", "丽湖至快 · 10号场"], saved.Schedule!.Resources.Days[0].Courts);
        Assert.All(saved.Schedule.Placements.Values, match => Assert.Contains(match.Court, saved.Schedule.Resources.Days[0].Courts));
        page.Days[0].CourtsText = "其他场地";
        await page.ResetCommand.ExecuteAsync();
        Assert.Contains("至快体育馆", page.Days[0].VenueSummary);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task CancelOrUnconfirmedWindowChangesPreserveAllInput(bool accept, bool confirm)
    {
        using var fixture = new ScheduleUiFixture();
        var page = Page(fixture, model =>
        {
            Select(model, 2, "1号场"); model.ConfirmAdjustUnavailable = confirm;
            return Task.FromResult(accept);
        });
        var day = page.Days[0]; day.CourtsText = "B1, B2";
        day.AddUnavailableCommand.Execute(null); day.Unavailable[0].CourtsText = "B1";
        await day.ChooseCourtsCommand.ExecuteAsync();
        Assert.Equal(["B1", "B2"], day.Build().Courts);
        Assert.Equal("B1", Assert.Single(day.Unavailable).CourtsText);
    }

    [Fact]
    public async Task ConfirmedCourtRemovalRetainsRemainingBlocksAndNeverTurnsEmptySelectionIntoAllCourts()
    {
        using var fixture = new ScheduleUiFixture();
        var page = Page(fixture, model =>
        {
            Select(model, 0, "B2"); Assert.True(model.HasAffectedWindows);
            model.ConfirmAdjustUnavailable = true; return Task.FromResult(true);
        });
        var day = page.Days[0]; day.CourtsText = "粤海东馆 · B1, 粤海东馆 · B2";
        for (var i = 0; i < 3; i++) day.AddUnavailableCommand.Execute(null);
        day.Unavailable[0].CourtsText = "粤海东馆 · B1, 粤海东馆 · B2";
        day.Unavailable[1].CourtsText = "粤海东馆 · B1";
        day.Unavailable[2].CourtsText = "";
        await day.ChooseCourtsCommand.ExecuteAsync();
        Assert.Equal(["粤海东馆 · B2"], day.Build().Courts);
        Assert.Equal(2, day.Unavailable.Count);
        Assert.Equal("粤海东馆 · B2", day.Unavailable[0].CourtsText);
        Assert.Equal("", day.Unavailable[1].CourtsText);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("edit")]
    [InlineData("remove")]
    [InlineData("session")]
    [InlineData("leave")]
    public async Task LatePickerResultCannotOverwriteNewerDraftOrSession(string change)
    {
        using var fixture = new ScheduleUiFixture();
        var completion = new TaskCompletionSource<bool>();
        var page = Page(fixture, model => { Select(model, 0, "A1"); return completion.Task; });
        var day = page.Days[0]; day.CourtsText = "原有场地";
        var pending = day.ChooseCourtsCommand.ExecuteAsync();
        Assert.False(page.GenerateCommand.CanExecute(null));
        switch (change)
        {
            case "reset": await page.ResetCommand.ExecuteAsync(); break;
            case "edit": day.CourtsText = "新输入的场地"; break;
            case "remove": day.RemoveCommand.Execute(null); break;
            case "session": page.RefreshSession(page.Session with { }); break;
            case "leave": fixture.Shell.Navigate(WorkspaceRoute.Overview); break;
        }
        completion.SetResult(true); await pending;
        Assert.NotEqual("粤海东馆 · A1", day.CourtsText);
        Assert.Null(fixture.Workflow.CurrentSession!.Workspace.Resources);
    }

    [Fact]
    public async Task CopyPreviousDayUsesNearestEarlierDateAndCopiesOnlyCourtsAfterConfirmation()
    {
        using var fixture = new ScheduleUiFixture();
        var page = Page(fixture, model => Task.FromResult(true));
        page.Days[0].DateText = "2026-10-05"; page.Days[0].CourtsText = "丽湖至畅 · 2号场";
        page.AddDayCommand.Execute(null); page.Days[0].DateText = "2026-10-03";
        page.Days[0].CourtsText = "粤海东馆 · C2";
        page.AddDayCommand.Execute(null); var target = page.Days[0]; target.DateText = "2026-10-04";
        target.StartText = "14:00"; target.EndText = "18:00";
        await target.CopyPreviousCourtsCommand.ExecuteAsync();
        Assert.Equal(["粤海东馆 · C2"], target.Build().Courts);
        Assert.Equal("14:00", target.StartText); Assert.Equal("18:00", target.EndText);
        Assert.Empty(target.Unavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateDialogFailureDoesNotReplaceFeedbackAfterLeavingThePage(bool unavailable)
    {
        using var fixture = new ScheduleUiFixture();
        var completion = new TaskCompletionSource<bool>();
        fixture.Shell.RegisterPageFactory(WorkspaceRoute.ScheduleSetup, session => new ScheduleSetupPageViewModel(
            fixture.Shell, session, _ => completion.Task, _ => completion.Task));
        fixture.Shell.Navigate(WorkspaceRoute.ScheduleSetup);
        var page = Assert.IsType<ScheduleSetupPageViewModel>(fixture.Shell.CurrentPage);
        var day = page.Days[0]; day.CourtsText = "1号场"; day.AddUnavailableCommand.Execute(null);
        var pending = unavailable ? day.Unavailable[0].ChooseCourtsCommand.ExecuteAsync() : day.ChooseCourtsCommand.ExecuteAsync();
        fixture.Shell.Navigate(WorkspaceRoute.Overview);
        var status = fixture.Shell.Status;
        completion.SetException(new IOException("late dialog failure")); await pending;
        Assert.Null(fixture.Shell.LastError);
        Assert.Equal(status, fixture.Shell.Status);
    }

    [Fact]
    public async Task UnavailablePickerIsScopedToDayAndExplicitAllIsDifferentFromEmptySubset()
    {
        using var fixture = new ScheduleUiFixture();
        var count = 0;
        var page = new ScheduleSetupPageViewModel(fixture.Shell, fixture.Workflow.CurrentSession!, chooseUnavailable: model =>
        {
            Assert.Equal(["丽湖至畅 · 1号场", "丽湖至畅 · 2号场"], model.Courts.Select(c => c.Name));
            model.AllCourts = false;
            foreach (var court in model.Courts) court.IsSelected = false;
            if (count++ == 0) model.Courts[1].IsSelected = true;
            return Task.FromResult(true);
        });
        var day = page.Days[0]; day.CourtsText = "丽湖至畅 · 1号场, 丽湖至畅 · 2号场";
        day.AddUnavailableCommand.Execute(null); var window = day.Unavailable[0];
        await window.ChooseCourtsCommand.ExecuteAsync();
        Assert.Equal("丽湖至畅 · 2号场", window.CourtsText);
        await window.ChooseCourtsCommand.ExecuteAsync();
        Assert.Equal("丽湖至畅 · 2号场", window.CourtsText);
    }

    private static ScheduleSetupPageViewModel Page(ScheduleUiFixture fixture, Func<VenueCourtSelectionViewModel, Task<bool>> choose)
    {
        fixture.Shell.RegisterPageFactory(WorkspaceRoute.ScheduleSetup, session => new ScheduleSetupPageViewModel(fixture.Shell, session, choose, confirmDiscard: () => Task.FromResult(true)));
        fixture.Shell.Navigate(WorkspaceRoute.ScheduleSetup);
        return Assert.IsType<ScheduleSetupPageViewModel>(fixture.Shell.CurrentPage);
    }

    private static void Select(VenueCourtSelectionViewModel model, int venue, params string[] labels)
    {
        model.VenueIndex = venue; model.ClearCommand.Execute(null);
        foreach (var court in model.Groups.SelectMany(group => group.Courts)) court.IsSelected = labels.Contains(court.Label);
    }
}
