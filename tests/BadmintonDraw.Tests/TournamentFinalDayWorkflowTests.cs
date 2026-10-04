using System.Security.Cryptography;
using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class TournamentFinalDayWorkflowTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("final-day-workflow-").FullName;
    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData(false, "2026-09-13")]
    [InlineData(true, "2026-09-16")]
    public void ReopeningPreservesFinalDayPolicyAndEveryProjectSchedule(bool required, string finalDay)
    {
        var workflow = Confirmed(entrants: 4, projects: 2);
        var generated = workflow.GenerateSchedule(Resources(), Policy(required), workflow.CurrentSession!.Workspace.Revision);
        var reopenedWorkflow = new TournamentWorkspaceWorkflow();
        var reopened = reopenedWorkflow.OpenWorkspace(generated.WorkspacePath).Workspace;

        Assert.Equal(required, reopened.Schedule!.Policy.RequireChampionshipFinalsOnLastDay);
        Assert.Equal(Json(generated.Workspace.Schedule), Json(reopened.Schedule));
        Assert.Equal(Json(generated.Workspace.Projects), Json(reopened.Projects));
        Assert.All(reopened.Projects, project =>
        {
            var final = Assert.Single(project.MatchGraph!.Matches, n => n.IsChampionshipFinal);
            Assert.Equal(finalDay, reopened.Schedule.Placements[final.Id].DayLabel);
            Assert.All(project.MatchGraph.Matches.Where(n => !n.IsChampionshipFinal),
                n => Assert.Equal("2026-09-13", reopened.Schedule.Placements[n.Id].DayLabel));
        });
        AssertValid(reopened);

        var regenerated = reopenedWorkflow.GenerateSchedule(reopened.Schedule.Resources,
            reopened.Schedule.Policy, reopened.Revision).Workspace;
        Assert.Equal(required, regenerated.Schedule!.Policy.RequireChampionshipFinalsOnLastDay);
        Assert.All(regenerated.Projects.SelectMany(p => p.MatchGraph!.Matches).Where(n => n.IsChampionshipFinal),
            n => Assert.Equal(finalDay, regenerated.Schedule.Placements[n.Id].DayLabel));
        AssertValid(regenerated);
    }

    [Fact]
    public void InsufficientLastDayCapacityRetainsOriginalArchiveScheduleAndPendingUndo()
    {
        var workflow = Scheduled(required: false);
        var final = ChampionshipFinal(workflow.CurrentSession!.Workspace);
        workflow.MoveMatch(Move(workflow, final, "2026-09-13", new(11, 0)), workflow.CurrentSession.Workspace.Revision);
        var before = workflow.CurrentSession!;
        var resources = Resources() with
        {
            Days = Resources().Days.Select(d => d.Date == new DateOnly(2026, 9, 16)
                ? d with { DayEnd = new(9, 29) } : d).ToArray()
        };

        var error = RejectUnchanged(workflow, () => workflow.GenerateSchedule(resources,
            Policy(true), before.Workspace.Revision), "schedule.generation-failed");

        Assert.NotNull(error.SchedulingFailure);
        Assert.NotEmpty(error.SchedulingFailure.UnplacedMatches);
        Assert.True(workflow.CanUndoScheduleEdit);
        Assert.Equal(Json(before.Workspace), Json(new TournamentWorkspaceStore().Read(before.WorkspacePath)));
        Assert.False(workflow.CurrentSession!.Workspace.Schedule!.Policy.RequireChampionshipFinalsOnLastDay);
        var undone = workflow.UndoLastScheduleEdit(before.Workspace.Revision).Workspace;
        Assert.Equal("2026-09-13", undone.Schedule!.Placements[final.Id].DayLabel);
        Assert.Equal(new TimeOnly(9, 0), undone.Schedule.Placements[final.Id].StartTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalCannotBeMovedBeforeLastDayByDirectOrCascadeConfirmation(bool cascade)
    {
        var workflow = Scheduled();
        var before = workflow.CurrentSession!;
        // Reopening makes this exercise the policy read from the archive, not the original request.
        workflow.OpenWorkspace(before.WorkspacePath);
        var final = ChampionshipFinal(workflow.CurrentSession!.Workspace);
        var request = Move(workflow, final, "2026-09-13", new(11, 0));
        var revision = workflow.CurrentSession.Workspace.Revision;
        var preview = cascade ? workflow.PreviewCascade(request, revision) : workflow.PreviewMove(request, revision);

        Assert.False(preview.CanApply);
        Assert.Empty(preview.Changes);
        Assert.Contains(preview.Violations, v => v.Code == SchedulingConstraintCode.ChampionshipFinalDay && v.MatchId == final.Id);
        var error = RejectUnchanged(workflow, () =>
        {
            if (cascade) workflow.CascadeMove(preview, revision);
            else workflow.MoveMatch(request, revision);
        }, cascade ? "schedule.cascade-blocked" : "schedule.move-blocked");
        Assert.Contains(error.SchedulingFailure!.Violations,
            v => v.Code == SchedulingConstraintCode.ChampionshipFinalDay && v.MatchId == final.Id);
        Assert.False(workflow.CanUndoScheduleEdit);
        Assert.Equal("2026-09-16", workflow.CurrentSession.Workspace.Schedule!.Placements[final.Id].DayLabel);
    }

    [Fact]
    public void LegalCascadeAndUndoKeepChampionshipFinalOnLastDay()
    {
        var workflow = Scheduled(entrants: 4);
        var before = workflow.CurrentSession!.Workspace;
        var semifinal = before.Projects[0].MatchGraph!.Matches.First(n => !n.IsChampionshipFinal);
        var final = ChampionshipFinal(before);
        var request = Move(workflow, semifinal, "2026-09-16", new(10, 0));

        Assert.False(workflow.PreviewMove(request, before.Revision).CanApply);
        var preview = workflow.PreviewCascade(request, before.Revision);
        Assert.True(preview.CanApply);
        var finalChange = Assert.Single(preview.Changes, c => c.Key.MatchId == final.Id);
        Assert.Equal("2026-09-16", finalChange.After.DayLabel);
        Assert.Equal(new TimeOnly(10, 45), finalChange.After.StartTime);

        var moved = workflow.CascadeMove(preview, before.Revision);
        Assert.All(preview.Changes, change => Assert.Equal(change.After, moved.Workspace.Schedule!.Placements[change.Key.MatchId]));
        Assert.True(moved.Workspace.Schedule!.Policy.RequireChampionshipFinalsOnLastDay);
        AssertValid(moved.Workspace);
        var undone = workflow.UndoLastScheduleEdit(moved.Workspace.Revision);
        Assert.Equal(Json(before.Schedule!.Placements), Json(undone.Workspace.Schedule!.Placements));
        Assert.True(undone.Workspace.Schedule.Policy.RequireChampionshipFinalsOnLastDay);
        Assert.Equal("2026-09-16", undone.Workspace.Schedule.Placements[final.Id].DayLabel);
        AssertValid(undone.Workspace);
        var reopened = new TournamentWorkspaceWorkflow().OpenWorkspace(undone.WorkspacePath).Workspace;
        Assert.Equal(Json(undone.Workspace.Schedule), Json(reopened.Schedule));
    }

    [Fact]
    public void EnablingFinalDayRuleInvalidatesOlderCascadeAndUndoThatWouldRestoreAnEarlyFinal()
    {
        var workflow = Scheduled(required: false);
        var final = ChampionshipFinal(workflow.CurrentSession!.Workspace);
        workflow.MoveMatch(Move(workflow, final, "2026-09-16", new(11, 0)), workflow.CurrentSession.Workspace.Revision);
        var before = workflow.CurrentSession!;
        var preview = workflow.PreviewCascade(Move(workflow, final, "2026-09-13", new(12, 0)), before.Workspace.Revision);
        Assert.True(preview.CanApply);
        Assert.True(workflow.CanUndoScheduleEdit);

        var generated = workflow.GenerateSchedule(Resources(), Policy(true), before.Workspace.Revision);

        Assert.False(workflow.CanUndoScheduleEdit);
        RejectUnchanged(workflow, () => workflow.CascadeMove(preview, generated.Workspace.Revision), "schedule.edit-stale");
        RejectUnchanged(workflow, () => workflow.UndoLastScheduleEdit(generated.Workspace.Revision), "schedule.undo-empty");
        Assert.True(workflow.CurrentSession!.Workspace.Schedule!.Policy.RequireChampionshipFinalsOnLastDay);
        Assert.Equal("2026-09-16", workflow.CurrentSession.Workspace.Schedule.Placements[final.Id].DayLabel);
    }

    [Fact]
    public void OpeningAnArchiveWithEarlyFinalAndRequiredLastDayRejectsItWithoutReplacingSession()
    {
        var workflow = Scheduled(required: false);
        var before = workflow.CurrentSession!;
        Assert.Equal("2026-09-13", before.Workspace.Schedule!.Placements[ChampionshipFinal(before.Workspace).Id].DayLabel);
        using (var connection = new SqliteConnection("Data Source=" + before.WorkspacePath + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schedule SET json = json_set(json, '$.Policy.RequireChampionshipFinalsOnLastDay', json('true'));";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        RejectUnchanged(workflow, () => workflow.OpenWorkspace(before.WorkspacePath), "InvalidWorkspace");
        Assert.Same(before, workflow.CurrentSession);
    }

    private TournamentWorkspaceWorkflow Scheduled(bool required = true, int entrants = 2)
    {
        var workflow = Confirmed(entrants);
        workflow.GenerateSchedule(Resources(), Policy(required), workflow.CurrentSession!.Workspace.Revision);
        return workflow;
    }

    private TournamentWorkspaceWorkflow Confirmed(int entrants = 2, int projects = 1)
    {
        var workflow = new TournamentWorkspaceWorkflow();
        workflow.CreateWorkspace(new("冠亚军决赛末日流程", TournamentKind.Individual, TournamentPurpose.FullTournament,
            new[] { EventDiscipline.MenSingles, EventDiscipline.WomenSingles }.Take(projects)
                .Select(d => new WorkspaceProjectRequest(d, CompetitionMode.SinglesKnockout)).ToArray(),
            Path.Combine(directory, "workspace.szbd")));
        foreach (var project in workflow.CurrentSession!.Workspace.Projects)
        {
            var path = Path.Combine(directory, project.Id + ".xlsx");
            using var workbook = new XLWorkbook();
            var sheet = workbook.AddWorksheet("名单");
            sheet.Cell(1, 1).Value = "姓名";
            sheet.Cell(1, 2).Value = "学号";
            for (var i = 0; i < entrants; i++)
            {
                sheet.Cell(i + 2, 1).Value = project.DisplayName + i;
                sheet.Cell(i + 2, 2).Value = project.Id + "-" + i;
            }
            workbook.SaveAs(path);
            workflow.ImportRoster(project.Id, path, workflow.CurrentSession.Workspace.Revision);
        }
        foreach (var project in workflow.CurrentSession.Workspace.Projects)
        {
            workflow.PreviewDraw(project.Id, new(CompetitionMode.SinglesKnockout, EventKind.Singles,
                1, "final-day-workflow"), workflow.CurrentSession.Workspace.Revision);
            workflow.ConfirmDraw(project.Id, workflow.CurrentSession.Workspace.Revision);
        }
        return workflow;
    }

    private static TournamentSchedulingPolicy Policy(bool required) =>
        new(ScheduleAutoSchedulingStrategy.Compact, [], false, [], []) { RequireChampionshipFinalsOnLastDay = required };

    // Deliberately reverse the input days: the latest date determines the finals day.
    private static TournamentResourcePlan Resources() => new([
        new(new(2026, 9, 16), new(9, 0), new(15, 0), ["A", "B"]),
        new(new(2026, 9, 13), new(9, 0), new(15, 0), ["A", "B"])], 2, 15, 6);

    private static MatchNode ChampionshipFinal(TournamentWorkspace workspace) =>
        Assert.Single(workspace.Projects[0].MatchGraph!.Matches, n => n.IsChampionshipFinal);

    private static MoveMatchRequest Move(TournamentWorkspaceWorkflow workflow, MatchNode node, string day, TimeOnly time) =>
        new(new(node.ProjectId, node.Id), day, time, "A", workflow.CaptureScheduleEditBaseline());

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));

    private WorkspaceError RejectUnchanged(TournamentWorkspaceWorkflow workflow, Action action, string code)
    {
        var before = workflow.CurrentSession!;
        var snapshot = Json(before.Workspace);
        var hash = Hash(before.WorkspacePath);
        var files = Directory.GetFiles(directory).Order().ToArray();
        var error = Assert.Throws<WorkspaceCommandException>(action).Error;
        Assert.Equal(code, error.Code);
        Assert.False(error.Committed);
        Assert.Same(before, workflow.CurrentSession);
        Assert.Equal(snapshot, Json(workflow.CurrentSession!.Workspace));
        Assert.Equal(hash, Hash(before.WorkspacePath));
        Assert.Equal(files, Directory.GetFiles(directory).Order().ToArray());
        return error;
    }

    private static void AssertValid(TournamentWorkspace workspace) => Assert.True(new TournamentPlacementValidator(new(
        workspace.Projects.Select(p => p.MatchGraph!).ToArray(), workspace.Resources!, workspace.Schedule!.Policy))
        .ValidateSchedule(workspace.Schedule.Placements).IsValid);
}
