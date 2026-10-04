using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Desktop.ViewModels;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class NewWorkspaceWizardViewModelTests
{
    [Fact]
    public void ChoosingTeamClearsIndividualSelectionsAndCreatesExactlyOneTeamProject()
    {
        var wizard = Ready();
        wizard.SetDisciplines([EventDiscipline.MenSingles, EventDiscipline.MixedDoubles]);
        wizard.SelectKind(TournamentKind.Team);
        var request = wizard.BuildRequest("校长杯团体赛", Path.GetFullPath("team.szbd"));
        Assert.Equal(EventDiscipline.Team, Assert.Single(request.Projects).Discipline);
        Assert.Equal(CompetitionMode.TeamKnockout, request.Projects[0].CompetitionMode);
        wizard.SelectKind(TournamentKind.Individual);
        Assert.DoesNotContain(wizard.Projects, p => p.IsSelected);
    }

    [Fact]
    public void ChoosingMultipleIndividualEventsCarriesModesAndNoScheduleConfiguration()
    {
        var wizard = Ready();
        wizard.SetDisciplines([EventDiscipline.MenSingles, EventDiscipline.MenDoubles, EventDiscipline.MixedDoubles]);
        wizard.Projects.Single(p => p.Discipline == EventDiscipline.MixedDoubles).CompetitionModeIndex = 1;
        var request = wizard.BuildRequest("校长杯单项赛", Path.GetFullPath("individual.szbd"));
        Assert.Equal(3, request.Projects.Count);
        Assert.Equal(CompetitionMode.SinglesRoundRobin, request.Projects[2].CompetitionMode);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request));
        Assert.False(json.RootElement.TryGetProperty("ResourcePlan", out _));
        Assert.False(json.RootElement.TryGetProperty("SchedulingPolicy", out _));
    }

    [Fact]
    public void RejectsDuplicateAndIllegalDisciplinesAndEmptySelection()
    {
        var wizard = Ready();
        Assert.Throws<ArgumentException>(() => wizard.SetDisciplines([EventDiscipline.MenSingles, EventDiscipline.MenSingles]));
        Assert.Throws<ArgumentException>(() => wizard.SetDisciplines([EventDiscipline.Team]));
        wizard.SetDisciplines([]);
        Assert.Throws<ArgumentException>(() => wizard.BuildRequest("赛事", Path.GetFullPath("a.szbd")));
    }

    [Theory]
    [InlineData("", "/tmp/a.szbd")]
    [InlineData("赛事", "")]
    [InlineData("赛事", "/tmp/a.xlsx")]
    public void RequiresNameAndWorkspacePath(string name, string path)
    {
        var wizard = Ready();
        wizard.SetDisciplines([EventDiscipline.MenSingles]);
        Assert.Throws<ArgumentException>(() => wizard.BuildRequest(name, path));
    }

    [Fact]
    public void RequiresExplicitKindAndPurposeAndValidatesEachWizardStep()
    {
        var wizard = new NewWorkspaceWizardViewModel();
        wizard.Name = "赛事";
        Assert.False(wizard.NextCommand.CanExecute(null));
        wizard.SelectKind(TournamentKind.Team);
        Assert.Throws<ArgumentException>(() => wizard.BuildRequest("赛事", Path.GetFullPath("a.szbd")));
        wizard.SelectPurpose(TournamentPurpose.PublicDrawOnly);
        wizard.NextCommand.Execute(null);
        Assert.Equal(2, wizard.Step);
        wizard.NextCommand.Execute(null);
        Assert.Equal(3, wizard.Step);
        Assert.Equal(TournamentPurpose.PublicDrawOnly, wizard.BuildRequest("赛事", Path.GetFullPath("a.szbd")).Purpose);
    }

    private static NewWorkspaceWizardViewModel Ready()
    {
        var wizard = new NewWorkspaceWizardViewModel();
        wizard.SelectKind(TournamentKind.Individual);
        wizard.SelectPurpose(TournamentPurpose.FullTournament);
        return wizard;
    }
}
