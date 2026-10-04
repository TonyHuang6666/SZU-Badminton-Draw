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
    public async Task CopyPreviousDayUsesNearestEarlierDateAndCopiesCourtsAndTimesOnlyAfterConfirmation()
    {
        using var fixture = new ScheduleUiFixture();
        var completion = new TaskCompletionSource<bool>();
        var page = Page(fixture, model => completion.Task);
        var path = fixture.Workflow.CurrentSession!.WorkspacePath;
        var bytes = SHA256.HashData(File.ReadAllBytes(path));
        page.Days[0].DateText = "2026-10-05"; page.Days[0].CourtsText = "丽湖至畅 · 2号场";
        page.AddDayCommand.Execute(null); page.Days[0].DateText = "2026-10-03";
        var source = page.Days[0]; source.CourtsText = "粤海东馆 · C2";
        source.StartText = "14:15"; source.EndText = "18:45";
        source.AddUnavailableCommand.Execute(null); source.AddRefereeWindowCommand.Execute(null);
        page.AddDayCommand.Execute(null); var target = page.Days[0]; target.DateText = "2026-10-04";
        target.StartText = "09:00"; target.EndText = "12:00";
        var pending = target.CopyPreviousCourtsCommand.ExecuteAsync();
        Assert.Empty(target.Build().Courts);
        Assert.Equal("09:00", target.StartText); Assert.Equal("12:00", target.EndText);
        completion.SetResult(true); await pending;
        Assert.Equal(["粤海东馆 · C2"], target.Build().Courts);
        Assert.Equal("14:15", target.StartText); Assert.Equal("18:45", target.EndText);
        Assert.Equal("2026-10-04", target.DateText);
        Assert.Empty(target.Unavailable);
        Assert.Empty(target.RefereeWindows);
        Assert.Equal(bytes, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrUnconfirmedCourtAdjustmentCopiesNeitherCourtsNorTimes(bool accept)
    {
        using var fixture = new ScheduleUiFixture();
        var page = Page(fixture, _ => Task.FromResult(accept));
        var source = page.Days[0]; source.DateText = "2026-10-03";
        source.CourtsText = "粤海东馆 · C2"; source.StartText = "14:00"; source.EndText = "18:00";
        page.AddDayCommand.Execute(null); var target = page.Days[0];
        target.CourtsText = "粤海东馆 · A1"; target.StartText = "09:00"; target.EndText = "12:00";
        target.AddUnavailableCommand.Execute(null); target.Unavailable[0].CourtsText = target.CourtsText;
        await target.CopyPreviousCourtsCommand.ExecuteAsync();
        Assert.Equal("粤海东馆 · A1", target.CourtsText);
        Assert.Equal("09:00", target.StartText); Assert.Equal("12:00", target.EndText);
        Assert.Equal("粤海东馆 · A1", Assert.Single(target.Unavailable).CourtsText);
    }

    [Fact]
    public async Task CopyDoesNotReplaceTargetsOwnUnavailableAndRefereeWindows()
    {
        using var fixture = new ScheduleUiFixture();
        var page = Page(fixture, _ => Task.FromResult(true));
        var source = page.Days[0]; source.DateText = "2026-10-03";
        source.CourtsText = "粤海东馆 · C2"; source.StartText = "14:00"; source.EndText = "18:00";
        source.AddUnavailableCommand.Execute(null); source.AddRefereeWindowCommand.Execute(null);
        page.AddDayCommand.Execute(null); var target = page.Days[0];
        target.StartText = "15:00"; target.EndText = "17:00"; target.CourtsText = source.CourtsText;
        target.AddUnavailableCommand.Execute(null); var block = target.Unavailable[0]; block.CourtsText = target.CourtsText;
        target.AddRefereeWindowCommand.Execute(null); var referees = target.RefereeWindows[0]; referees.CountText = "2";
        await target.CopyPreviousCourtsCommand.ExecuteAsync();
        Assert.Equal("14:00", target.StartText); Assert.Equal("18:00", target.EndText);
        Assert.Same(block, Assert.Single(target.Unavailable));
        Assert.Equal("15:00", block.StartText); Assert.Equal("17:00", block.EndText);
        Assert.Same(referees, Assert.Single(target.RefereeWindows));
        Assert.Equal("15:00", referees.StartText); Assert.Equal("17:00", referees.EndText); Assert.Equal("2", referees.CountText);
    }

    [Theory]
    [InlineData("source-edit")]
    [InlineData("target-edit")]
    [InlineData("remove")]
    [InlineData("reset")]
    [InlineData("session")]
    [InlineData("leave")]
    [InlineData("dispose")]
    public async Task PendingCopyCannotOverwriteNewerDraftOrSession(string change)
    {
        using var fixture = new ScheduleUiFixture();
        var completion = new TaskCompletionSource<bool>();
        var page = Page(fixture, _ => completion.Task);
        var source = page.Days[0]; source.DateText = "2026-10-03";
        source.CourtsText = "粤海东馆 · C2"; source.StartText = "14:00"; source.EndText = "18:00";
        page.AddDayCommand.Execute(null); var target = page.Days[0];
        target.CourtsText = "粤海东馆 · A1"; target.StartText = "09:00"; target.EndText = "12:00";
        var pending = target.CopyPreviousCourtsCommand.ExecuteAsync();
        Assert.Equal("粤海东馆 · A1", target.CourtsText);
        Assert.Equal("09:00", target.StartText); Assert.Equal("12:00", target.EndText);
        switch (change)
        {
            case "source-edit": source.StartText = "15:00"; break;
            case "target-edit": target.EndText = "13:00"; break;
            case "remove": source.RemoveCommand.Execute(null); break;
            case "reset": await page.ResetCommand.ExecuteAsync(); break;
            case "session": page.RefreshSession(page.Session with { }); break;
            case "leave": fixture.Shell.Navigate(WorkspaceRoute.Overview); break;
            case "dispose": page.Dispose(); break;
        }
        completion.SetResult(true); await pending;
        Assert.Equal("粤海东馆 · A1", target.CourtsText);
        Assert.Equal("09:00", target.StartText);
        Assert.Equal(change == "target-edit" ? "13:00" : "12:00", target.EndText);
        Assert.Null(fixture.Workflow.CurrentSession!.Workspace.Resources);
    }

    [Theory]
    [InlineData("", "18:00")]
    [InlineData("18:00", "14:00")]
    public async Task InvalidSourceTimesAreReportedWithoutOpeningPickerOrChangingTarget(string start, string end)
    {
        using var fixture = new ScheduleUiFixture();
        var opened = false;
        var page = Page(fixture, _ => { opened = true; return Task.FromResult(true); });
        var source = page.Days[0]; source.DateText = "2026-10-03";
        source.CourtsText = "粤海东馆 · C2"; source.StartText = start; source.EndText = end;
        page.AddDayCommand.Execute(null); var target = page.Days[0]; target.CourtsText = "粤海东馆 · A1";
        await target.CopyPreviousCourtsCommand.ExecuteAsync();
        Assert.False(opened);
        Assert.NotNull(fixture.Shell.LastError);
        Assert.Equal("粤海东馆 · A1", target.CourtsText);
        Assert.Equal("09:00", target.StartText); Assert.Equal("18:00", target.EndText);
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
