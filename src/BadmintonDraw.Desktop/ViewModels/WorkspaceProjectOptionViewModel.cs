using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;

namespace BadmintonDraw.Desktop.ViewModels;

public sealed class WorkspaceProjectOptionViewModel(EventDiscipline discipline, Guid? projectId = null) : ViewModelBase
{
    private bool isSelected;
    private int competitionModeIndex;
    private string displayName = DisciplineName(discipline);
    public EventDiscipline Discipline { get; } = discipline;
    public Guid? ProjectId { get; } = projectId;
    public string Label => DisciplineName(Discipline);
    public bool IsOptional => Discipline != EventDiscipline.Team;
    public bool IsSelected { get => isSelected; set => SetProperty(ref isSelected, value); }
    public string DisplayName { get => displayName; set => SetProperty(ref displayName, value); }
    public IReadOnlyList<string> CompetitionModes { get; } = ["淘汰赛", "循环赛"];
    public int CompetitionModeIndex
    {
        get => competitionModeIndex;
        set
        {
            if (value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
            SetProperty(ref competitionModeIndex, value);
        }
    }
    public WorkspaceProjectRequest BuildRequest() => new(Discipline, Discipline == EventDiscipline.Team
        ? (CompetitionModeIndex == 0 ? CompetitionMode.TeamKnockout : CompetitionMode.TeamRoundRobin)
        : (CompetitionModeIndex == 0 ? CompetitionMode.SinglesKnockout : CompetitionMode.SinglesRoundRobin), DisplayName.Trim(), ProjectId);
    public static string DisciplineName(EventDiscipline discipline) => discipline switch
    {
        EventDiscipline.MenSingles => "男子单打", EventDiscipline.WomenSingles => "女子单打",
        EventDiscipline.MenDoubles => "男子双打", EventDiscipline.WomenDoubles => "女子双打",
        EventDiscipline.MixedDoubles => "混合双打", EventDiscipline.Team => "团体项目", _ => "未知项目"
    };
}
