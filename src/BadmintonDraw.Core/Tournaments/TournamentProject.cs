using BadmintonDraw.Core.Matches;
namespace BadmintonDraw.Core.Tournaments;
public sealed record TournamentProject(Guid Id, EventDiscipline Discipline, string DisplayName,
    CompetitionMode CompetitionMode, ProjectRoster? Roster, ProjectDraw? Draw, MatchGraph? MatchGraph, int SortOrder)
{
    public static TournamentProject Create(EventDiscipline discipline, CompetitionMode competitionMode, int sortOrder,
        string? displayName = null)
    {
        var project = new TournamentProject(Guid.NewGuid(), discipline, displayName ?? discipline switch
        {
            EventDiscipline.MenSingles => "男子单打", EventDiscipline.WomenSingles => "女子单打",
            EventDiscipline.MenDoubles => "男子双打", EventDiscipline.WomenDoubles => "女子双打",
            EventDiscipline.MixedDoubles => "混合双打", EventDiscipline.Team => "团体", _ => ""
        }, competitionMode, null, null, null, sortOrder);
        TournamentWorkspaceRules.ValidateProject(project);
        return project;
    }
}
