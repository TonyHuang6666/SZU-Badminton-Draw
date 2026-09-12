using System.Security.Cryptography;
using System.Text.Json;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class RosterSeedWorkflowTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("roster-seeds-").FullName;
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public void EditingSeedsPreservesOriginalSourceAndPlayerIdentityAndOnlyClearsSelectedPreview()
    {
        var workflow = Ready();
        var initial = workflow.CurrentSession!.Workspace;
        var first = initial.Projects[0]; var second = initial.Projects[1];
        workflow.PreviewDraw(first.Id, Settings(), initial.Revision);
        workflow.ConfirmDraw(first.Id, Revision(workflow));
        workflow.PreviewDraw(second.Id, Settings(), Revision(workflow));
        var before = workflow.CurrentSession!.Workspace;
        var updated = workflow.UpdateRosterSeeds(second.Id, [new(1, true, null)], before.Revision);
        var roster = updated.Workspace.Projects[1].Roster!;
        Assert.Equal(before.Revision + 1, updated.Workspace.Revision);
        Assert.Equal(JsonSerializer.Serialize(before.Projects[0]), JsonSerializer.Serialize(updated.Workspace.Projects[0]));
        Assert.Null(updated.Workspace.Projects[1].Draw);
        Assert.Null(updated.Workspace.Projects[1].MatchGraph);
        Assert.Null(updated.Workspace.Schedule);
        Assert.Equal(TournamentStage.RostersReady, updated.Workspace.Stage);
        Assert.Equal(second.Roster!.ContentHash, roster.ContentHash);
        Assert.Equal(second.Roster.SourceFileName, roster.SourceFileName);
        Assert.Equal(second.Roster.Participants[0], roster.Participants[0]);
        Assert.Equal(second.Roster.Participants[1] with { IsSeed = true, SeedRank = null }, roster.Participants[1]);
        Assert.Equal("partner-2", roster.Participants[1].PartnerStudentId);
        Assert.Contains(second.Roster.Warnings, w => w.Code == "roster.duplicate-player-name");
        Assert.Contains(roster.Warnings, w => w.Code == "roster.duplicate-player-name");
        Assert.Single(roster.Warnings, w => w.Code == "roster.unranked-seed");
        Assert.Single(updated.Workspace.AuditEvents, a => a.Action == "RosterSeedsUpdated");
        var reopened = new TournamentWorkspaceWorkflow(); reopened.OpenWorkspace(updated.WorkspacePath);
        Assert.True(reopened.CurrentSession!.Workspace.Projects[1].Roster!.Participants[1].IsSeed);
        reopened.PreviewDraw(second.Id, Settings(), Revision(reopened));
        Assert.NotEqual(before.Projects[1].Draw!.Result.Audit.InputHash,
            reopened.CurrentSession.Workspace.Projects[1].Draw!.Result.Audit.InputHash);
        var ranked = reopened.UpdateRosterSeeds(second.Id, [new(1, true, 1)], Revision(reopened));
        Assert.DoesNotContain(ranked.Workspace.Projects[1].Roster!.Warnings, w => w.Code == "roster.unranked-seed");
        Assert.Contains(ranked.Workspace.Projects[1].Roster!.Warnings, w => w.Code == "roster.duplicate-player-name");
    }

    [Theory]
    [InlineData(-1, true, 1, "roster.seed-index")]
    [InlineData(4, true, 1, "roster.seed-index")]
    [InlineData(0, true, 0, "roster.seed-rank")]
    [InlineData(0, true, 3, "roster.seed-rank")]
    [InlineData(0, false, 1, "roster.seed-flag")]
    public void InvalidEditsLeaveStoredRosterAndPreviewUntouched(int index, bool seed, int rank, string code)
    {
        var workflow = Ready();
        var id = workflow.CurrentSession!.Workspace.Projects[0].Id;
        workflow.PreviewDraw(id, Settings(), Revision(workflow));
        AssertUnchanged(workflow, () => workflow.UpdateRosterSeeds(id, [new(index, seed, rank)], Revision(workflow)), code);
    }

    [Fact]
    public void FullResultSeedValidationRejectsDuplicatesAndExcessIncludingUneditedSeeds()
    {
        var workflow = Ready();
        var id = workflow.CurrentSession!.Workspace.Projects[0].Id;
        AssertUnchanged(workflow, () => workflow.UpdateRosterSeeds(id, [new(0, true, 1), new(0, false, null)], Revision(workflow)),
            "roster.seed-index");
        workflow.UpdateRosterSeeds(id, [new(0, true, 1)], Revision(workflow));
        AssertUnchanged(workflow, () => workflow.UpdateRosterSeeds(id, [new(1, true, 1)], Revision(workflow)), "roster.seed-duplicate");
        AssertUnchanged(workflow, () => workflow.UpdateRosterSeeds(id, [new(1, true, null), new(2, true, null)], Revision(workflow)),
            "roster.seed-count");
    }

    [Fact]
    public void SeedReviewWorksBeforeAllRostersAreReadyAndConfirmedRosterRequiresReopen()
    {
        var workflow = Ready();
        var id = workflow.CurrentSession!.Workspace.Projects[0].Id;
        var request = new UpdateWorkspaceConfigurationRequest("种子测试", [
            new(EventDiscipline.MenDoubles, CompetitionMode.SinglesKnockout, ProjectId: id),
            new(EventDiscipline.WomenSingles, CompetitionMode.SinglesKnockout)]);
        workflow.UpdateConfiguration(request, Revision(workflow));
        var updated = workflow.UpdateRosterSeeds(id, [new(0, true, 1)], Revision(workflow));
        Assert.Equal(TournamentStage.Draft, updated.Workspace.Stage);
        Assert.Null(updated.Workspace.Projects[0].Draw);
        Assert.DoesNotContain(updated.Workspace.Projects, p => p.MatchGraph is not null);
        workflow.ImportRoster(updated.Workspace.Projects[1].Id, WriteRoster(false), Revision(workflow));
        workflow.PreviewDraw(id, Settings(), Revision(workflow));
        workflow.ConfirmDraw(id, Revision(workflow));
        AssertUnchanged(workflow, () => workflow.UpdateRosterSeeds(id, [new(0, false, null)], Revision(workflow)), "draw.frozen");
        workflow.ReopenDraw(id, "种子审核更正", Revision(workflow));
        Assert.False(workflow.UpdateRosterSeeds(id, [new(0, false, null)], Revision(workflow)).Workspace.Projects[0].Roster!.Participants[0].IsSeed);
    }

    [Fact]
    public void StaleRevisionCannotApplySourceIndicesToNewRoster()
    {
        var workflow = Ready();
        var id = workflow.CurrentSession!.Workspace.Projects[0].Id;
        var stale = Revision(workflow);
        workflow.ImportRoster(id, WriteRoster(), stale);
        AssertUnchanged(workflow, () => workflow.UpdateRosterSeeds(id, [new(0, true, 1)], stale), "RevisionConflict");
    }

    private TournamentWorkspaceWorkflow Ready()
    {
        var workflow = new TournamentWorkspaceWorkflow();
        var created = workflow.CreateWorkspace(new("种子测试", TournamentKind.Individual, TournamentPurpose.PublicDrawOnly,
            [new(EventDiscipline.MenDoubles, CompetitionMode.SinglesKnockout), new(EventDiscipline.MixedDoubles, CompetitionMode.SinglesKnockout)],
            Path.Combine(directory, "workspace.szbd")));
        var input = WriteRoster();
        foreach (var project in created.Workspace.Projects) workflow.ImportRoster(project.Id, input, Revision(workflow));
        return workflow;
    }
    private string WriteRoster(bool doubles = true)
    {
        var path = Path.Combine(directory, doubles ? "双打.xlsx" : "单打.xlsx");
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("名单");
        sheet.Cell(1, 1).Value = "姓名"; sheet.Cell(1, 2).Value = "学号";
        sheet.Cell(1, 3).Value = "搭档姓名"; sheet.Cell(1, 4).Value = "搭档学号";
        for (var i = 1; i <= 4; i++)
        {
            sheet.Cell(i + 1, 1).Value = "同名选手"; sheet.Cell(i + 1, 2).Value = "primary-" + i;
            if (doubles) { sheet.Cell(i + 1, 3).Value = "搭档" + i; sheet.Cell(i + 1, 4).Value = "partner-" + i; }
        }
        book.SaveAs(path); return path;
    }
    private static DrawSettings Settings() => new(CompetitionMode.SinglesKnockout, EventKind.Doubles, 1, "seed-review");
    private static long Revision(TournamentWorkspaceWorkflow workflow) => workflow.CurrentSession!.Workspace.Revision;
    private static void AssertUnchanged(TournamentWorkspaceWorkflow workflow, Action action, string code)
    {
        var session = workflow.CurrentSession!;
        var hash = SHA256.HashData(File.ReadAllBytes(session.WorkspacePath));
        Assert.Equal(code, Assert.Throws<WorkspaceCommandException>(action).Error.Code);
        Assert.Same(session, workflow.CurrentSession);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(session.WorkspacePath)));
    }
}
