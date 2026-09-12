using System.Globalization;
using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceResultImportTests
{
    [Fact]
    public void SingleMatchImportBuildsOneCompleteCandidateWithoutChangingSourceOrSchedule()
    {
        var source = Fixture(); var before = Snapshot(source); var options = Options();
        var evaluation = Evaluate(source, [File(Row(source))], options);
        var candidate = Ready(evaluation);
        Assert.Equal(TournamentStage.Completed, candidate.Stage);
        Assert.Equal(source.Revision, candidate.Revision); Assert.Equal(source.UpdatedAt, candidate.UpdatedAt);
        Assert.Same(source.Schedule, candidate.Schedule); Assert.Equal(before, Snapshot(source));
        var result = Assert.Single(candidate.Results).Value;
        Assert.Equal(options.ImportedAt, result.RecordedAt); Assert.Equal("21-10", result.Score);
        Assert.Equal(new DateOnly(2026, 9, 13), result.ActualPlayedDay);
        Assert.Equal(1, Assert.Single(candidate.ImportLogs).AddedResultCount);
        Assert.Single(candidate.ProcessedDays); Assert.Empty(candidate.ResultHistory);
        Assert.Equal("ResultsImported", Assert.Single(candidate.AuditEvents).Action);
    }

    [Theory]
    [InlineData("21－19", "18m", "21-19", 18)]
    [InlineData("021 – 019；19-21、21—18", "20 分钟", "21-19, 19-21, 21-18", 20)]
    [InlineData("30-29, 30-29", "20M", "30-29, 30-29", 20)]
    [InlineData("2-0", "1", "2-0", 1)]
    public void SupportedIndividualScoresAndDurationsHaveCanonicalValues(string score, string duration, string canonical, int minutes)
    {
        var source = Fixture(); var row = Row(source) with { Score = Cell(score), Duration = Cell(duration) };
        var result = Ready(Evaluate(source, [File(row)])).Results.Single().Value;
        Assert.Equal(canonical, result.Score); Assert.Equal(minutes, result.DurationMinutes);
    }

    [Theory]
    [InlineData("", "20")]
    [InlineData("21-21", "20")]
    [InlineData("19-21", "20")]
    [InlineData("21-19,19-21", "20")]
    [InlineData("21-19,21-19,19-21", "20")]
    [InlineData("21-19,21-19,21-19,21-19", "20")]
    [InlineData("21-19,", "20")]
    [InlineData("999999999999999-0", "20")]
    [InlineData("退赛", "20")]
    [InlineData("21:19", "20")]
    [InlineData("21-19", "0")]
    [InlineData("21-19", "1.5")]
    [InlineData("21-19", "-2")]
    [InlineData("21-19", "2hours")]
    [InlineData("21-19", "999999999999")]
    public void InvalidOrContradictoryPlayedValuesBlockWholeBatch(string score, string duration)
    {
        var source = Fixture(); var before = Snapshot(source);
        Rejected(Evaluate(source, [File(Row(source) with { Score = Cell(score), Duration = Cell(duration) })]));
        Assert.Equal(before, Snapshot(source));
    }

    [Fact]
    public void ExplicitWalkoverKeepsAnnotationAndZeroDurationWithoutInventingData()
    {
        var source = Fixture(); var row = Row(source) with { ResultKind = Cell("弃权"), Score = Cell("  因伤未开赛  "), Duration = Cell("0") };
        var result = Ready(Evaluate(source, [File(row)])).Results.Single().Value;
        Assert.Equal(TournamentResultKind.Walkover, result.Kind); Assert.Equal(0, result.DurationMinutes); Assert.Equal("因伤未开赛", result.Score);
        Assert.Equal("", Ready(Evaluate(source, [File(row with { Score = Empty })])).Results.Single().Value.Score);
        Rejected(Evaluate(source, [File(row with { Duration = Cell("1") })]));
        Rejected(Evaluate(source, [File(row with { ResultKind = Empty, Score = Empty })]));
    }

    [Fact]
    public void PendingRowsKeepCoverageAndWarningsWithoutResolvingHelperCachesOrCompleting()
    {
        var source = Fixture(); var row = Row(source) with { Winner = Empty, ActualPlayedDay = Empty,
            OptionA = new("", HasFormula: true, ReadError: "no cache"), SideB = new("#REF!", WorkspaceRecordCellKind.Error) };
        var result = Evaluate(source, [File(row)]); var candidate = Ready(result);
        Assert.Equal(TournamentStage.ScheduleReady, candidate.Stage); Assert.Empty(candidate.Results);
        Assert.False(Assert.Single(Assert.Single(candidate.ImportLogs).Rows).HadResult);
        Assert.Single(candidate.ProcessedDays); Assert.Contains(result.Diagnostics, d => d.Code == "result.pending-details");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ResultImportDiagnosticSeverity.Blocking);
        Assert.Single(Ready(Evaluate(source, [File(row with { Winner = Cell("a"), ActualPlayedDay = Cell("2026-09-13") })])).Results);
        Rejected(Evaluate(source, [File(row with { Winner = new("", ReadError: "no winner cache") })]));
    }

    [Fact]
    public void OneProjectSheetDoesNotCompleteOtherProjectsAndActualDateDoesNotMovePlacement()
    {
        var source = Fixture(projectCount: 2); var row = Row(source) with { ActualPlayedDay = Cell("2026-09-14") };
        var result = Ready(Evaluate(source, [File(row)]));
        Assert.Equal(TournamentStage.InProgress, result.Stage); Assert.Single(result.Results);
        Assert.Equal(new DateOnly(2026, 9, 14), result.Results.Single().Value.ActualPlayedDay);
        Assert.Equal("2026-09-13", result.Schedule!.Placements[Guid.Parse(row.MatchId.Text)].DayLabel);
    }

    internal static readonly WorkspaceRecordCell Empty = new("", WorkspaceRecordCellKind.Empty);
    internal static WorkspaceRecordCell Cell(string value) => new(value);
    internal static ResultImportEvaluationOptions Options(bool allow = false, string? reason = null) =>
        new(allow, reason, DateTimeOffset.UtcNow.AddDays(1), Guid.NewGuid());
    internal static ResultImportEvaluation Evaluate(TournamentWorkspace source, IReadOnlyList<WorkspaceRecordImportFile> files,
        ResultImportEvaluationOptions? options = null) => new ResultImportWorkflow().Evaluate(source, files, options ?? Options());
    internal static TournamentWorkspace Ready(ResultImportEvaluation evaluation)
    {
        Assert.True(evaluation.Status == ResultImportEvaluationStatus.Ready, string.Join("; ", evaluation.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.NotNull(evaluation.Candidate); TournamentWorkspaceRules.Validate(evaluation.Candidate); return evaluation.Candidate;
    }
    internal static void Rejected(ResultImportEvaluation evaluation)
    { Assert.Equal(ResultImportEvaluationStatus.Rejected, evaluation.Status); Assert.Null(evaluation.Candidate); Assert.Contains(evaluation.Diagnostics, d => d.Severity == ResultImportDiagnosticSeverity.Blocking); }
    internal static WorkspaceRecordImportFile File(WorkspaceRecordRawRow row, char hash = 'a', string path = "/virtual/记录.xlsx") =>
        new("记录.xlsx", path, new(new string(hash, 64), [row]));
    internal static WorkspaceRecordRawRow Row(TournamentWorkspace workspace, int project = 0, int match = 0, int row = 6)
    {
        var p = workspace.Projects[project]; var n = p.MatchGraph!.Matches[match];
        return new(new("对阵记录表", row), Cell(workspace.Id.ToString()), Cell(p.Id.ToString()), Cell(n.Id.ToString()),
            Cell(p.MatchGraph.Revision), Cell(p.Draw!.ConfirmedAt!.Value.ToString("O", CultureInfo.InvariantCulture)),
            Cell("2026-09-13"), Cell("2026-09-13"), Empty, Cell("A"), Cell("21-10"), Cell("20"), Empty, Empty, Empty, Empty);
    }
    internal static TournamentWorkspace Fixture(int projectCount = 1, bool bracket = false, bool team = false)
    {
        var workspace = TournamentWorkspaceRulesTests.Fixture(TournamentStage.ScheduleReady);
        var projects = new List<TournamentProject>(); var placements = new Dictionary<Guid, MatchPlacement>();
        for (var i = 0; i < projectCount; i++)
        {
            var template = workspace.Projects[0]; var id = Guid.NewGuid();
            var roster = Enumerable.Range(1, bracket ? 4 : 2).Select(j => new DrawParticipant($"选手{i}-{j}", PrimaryStudentId: $"{i}-{j}")).ToArray();
            var discipline = team ? EventDiscipline.Team : i == 0 ? EventDiscipline.MenSingles : EventDiscipline.WomenSingles;
            var participants = roster.Select(p => ProjectEntrantIdentity.Create(discipline, p)).ToArray();
            MatchNode Node(int order, EntrantSource a, EntrantSource b) => new(Guid.NewGuid(), id, $"match-{order}", order, 1, "阶段", "同名场次", a, b, 30,
                new[] { a, b }.Select(s => s switch { EntrantSource.WinnerOf w => w.MatchId, EntrantSource.LoserOf l => l.MatchId, _ => Guid.Empty }).Where(g => g != Guid.Empty).Distinct().ToArray());
            var nodes = new List<MatchNode> { Node(1, participants[0], participants[1]) };
            if (bracket)
            {
                nodes.Add(Node(2, participants[2], participants[3]));
                nodes.Add(Node(3, new EntrantSource.WinnerOf(nodes[0].Id), new EntrantSource.WinnerOf(nodes[1].Id)));
                nodes.Add(Node(4, new EntrantSource.LoserOf(nodes[0].Id), new EntrantSource.LoserOf(nodes[1].Id)));
            }
            var mode = team ? CompetitionMode.TeamKnockout : CompetitionMode.SinglesKnockout;
            var draw = template.Draw!.Result with { Settings = template.Draw.Result.Settings with { CompetitionMode = mode, EventKind = team ? EventKind.Team : EventKind.Singles } };
            projects.Add(template with { Id = id, Discipline = discipline, CompetitionMode = mode, SortOrder = i,
                Roster = new(roster, "名单.xlsx", "roster-hash", []), Draw = new(draw, template.Draw.ConfirmedAt), MatchGraph = new(id, "graph-v1", nodes) });
            foreach (var node in nodes) placements.Add(node.Id, new(node.Id, "2026-09-13", new TimeOnly(9, 0).AddMinutes((node.Order - 1) * 45), new TimeOnly(9, 30).AddMinutes((node.Order - 1) * 45), (i + 1).ToString()));
        }
        var resources = workspace.Resources! with { Days = [new(new(2026, 9, 13), new(9, 0), new(18, 0), ["1", "2"]), new(new(2026, 9, 14), new(9, 0), new(18, 0), ["1", "2"])] };
        workspace = workspace with { Kind = team ? TournamentKind.Team : TournamentKind.Individual, Projects = projects, Results = new Dictionary<WorkspaceMatchKey, TournamentMatchResult>(), Resources = resources,
            Schedule = workspace.Schedule! with { Resources = resources, Placements = placements, GraphRevisions = projects.ToDictionary(p => p.Id, p => p.MatchGraph!.Revision) } };
        TournamentWorkspaceRules.Validate(workspace); return workspace;
    }
    internal static string Snapshot(TournamentWorkspace workspace) => JsonSerializer.Serialize(new { workspace.Id, workspace.Stage, workspace.Revision,
        workspace.Projects, workspace.Schedule, workspace.Resources, Results = workspace.Results.OrderBy(p => p.Key.ProjectId).ThenBy(p => p.Key.MatchId).Select(p => p.Value),
        workspace.ImportLogs, workspace.ProcessedDays, workspace.ResultHistory, workspace.AuditEvents });
}
