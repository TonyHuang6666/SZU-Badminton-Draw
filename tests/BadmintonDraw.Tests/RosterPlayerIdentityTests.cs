using System.Security.Cryptography;
using BadmintonDraw.Core;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class RosterPlayerIdentityTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("roster-player-identity-").FullName;
    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DifferentDoublesPairsCannotSharePrimaryOrPartnerIdentity(bool firstPartner, bool secondPartner)
    {
        var first = firstPartner ? Pair("甲", "002", "选手", "001") : Pair("选手", "001", "甲", "002");
        var second = secondPartner ? Pair("乙", "003", "改名选手", "001") : Pair("改名选手", "001", "乙", "003");

        var error = Assert.Throws<WorkspaceValidationException>(() =>
            TournamentWorkspaceRules.ValidateProject(Project(EventDiscipline.MenDoubles, first, second)));

        Assert.Equal("roster.player-duplicate", error.Code);
        Assert.Contains("第 1", error.Message);
        Assert.Contains("第 2", error.Message);
        Assert.Contains("001", error.Message);
    }

    [Theory]
    [InlineData(EventDiscipline.MenSingles)]
    [InlineData(EventDiscipline.WomenSingles)]
    [InlineData(EventDiscipline.MenDoubles)]
    [InlineData(EventDiscipline.WomenDoubles)]
    [InlineData(EventDiscipline.MixedDoubles)]
    public void EveryIndividualDisciplineUsesCaseInsensitiveNormalizedStudentIdentity(EventDiscipline discipline)
    {
        var participants = discipline is EventDiscipline.MenSingles or EventDiscipline.WomenSingles
            ? new[] { new DrawParticipant("甲", PrimaryStudentId: " Sz U-001 "), new DrawParticipant("乙", PrimaryStudentId: "szu-001") }
            : new[] { Pair("甲", " Sz U-001 ", "丙", "002"), Pair("乙", "szu-001", "丁", "003") };

        var error = Assert.Throws<WorkspaceValidationException>(() =>
            TournamentWorkspaceRules.ValidateProject(Project(discipline, participants)));

        Assert.Equal("roster.player-duplicate", error.Code);
    }

    [Fact]
    public void OnePairCannotContainCaseVariantsOfOneStudentIdentity()
    {
        var error = Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.ValidateProject(
            Project(EventDiscipline.MixedDoubles, Pair("甲", "szu-001", "乙", "SZU-001"), Pair("丙", "002", "丁", "003"))));

        Assert.Equal("roster.player-duplicate", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingIdsWithDuplicateNormalizedNamesRequireIdentityClarification(bool doubles)
    {
        var participants = doubles
            ? new[] { Pair("甲", "001", "曾 梦得", null), Pair("乙", "002", "曾梦得", null) }
            : new[] { new DrawParticipant("曾 梦得"), new DrawParticipant("曾梦得") };

        var error = Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.ValidateProject(
            Project(doubles ? EventDiscipline.MixedDoubles : EventDiscipline.MenSingles, participants)));

        Assert.Equal("roster.player-ambiguous", error.Code);
        Assert.Contains("无法区分", error.Message);
        Assert.Contains("不同", error.Message);
        Assert.Contains("学号", error.Message);
    }

    [Fact]
    public void SameNamesWithDistinctStudentIdsRemainSeparateBothWithinAndBetweenPairs()
    {
        var participants = new[] { Pair("同名", "001", "同名", "002"), Pair("同名", "003", "同名", "004") };
        var project = Confirm(Project(EventDiscipline.MenDoubles, participants));

        TournamentWorkspaceRules.ValidateProject(project);

        var players = project.MatchGraph!.Matches.SelectMany(match => new[] { match.SideA, match.SideB })
            .OfType<EntrantSource.Participant>().SelectMany(entrant => entrant.Players).ToArray();
        Assert.Equal(new[] { "001", "002", "003", "004" }, players.Select(player => player.StudentId).Order());
        Assert.Equal(participants, project.Roster!.Participants);
    }

    [Fact]
    public void TeamRosterKeepsTeamIdentityInsteadOfApplyingIndividualPlayerUniqueness()
    {
        var project = Project(EventDiscipline.Team,
            new("甲队", PrimaryName: "同一联络人", PrimaryStudentId: "001", TeamName: "甲队"),
            new("乙队", PrimaryName: "同一联络人", PrimaryStudentId: "001", TeamName: "乙队"));

        TournamentWorkspaceRules.ValidateProject(Confirm(project));
    }

    [Fact]
    public void DrawOnlyWorkspaceCannotBypassRosterGateWithADirectlyConstructedConfirmedDraw()
    {
        var emptyProject = TournamentProject.Create(EventDiscipline.MenDoubles, CompetitionMode.SinglesKnockout, 0);
        var workspace = TournamentWorkspace.Create("公开抽签", TournamentKind.Individual, TournamentPurpose.PublicDrawOnly, [emptyProject]);
        var project = Confirm(emptyProject with { Roster = Roster(Pair("甲", "001", "乙", "002"), Pair("甲", "001", "丙", "003")) });
        var candidate = workspace with { Projects = [project], Stage = TournamentStage.DrawsConfirmed };

        var error = Assert.Throws<WorkspaceValidationException>(() => TournamentWorkspaceRules.Validate(candidate));

        Assert.Equal("roster.player-duplicate", error.Code);
        Assert.Null(candidate.Schedule);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportRejectsTheEntireInvalidRosterWithoutReplacingArchiveOrPreview(bool missingIds)
    {
        var workflow = new TournamentWorkspaceWorkflow();
        var created = workflow.CreateWorkspace(new("公开抽签", TournamentKind.Individual, TournamentPurpose.PublicDrawOnly,
            [new(EventDiscipline.MixedDoubles, CompetitionMode.SinglesKnockout)], Path.Combine(directory, "workspace.szbd")));
        var id = created.Workspace.Projects[0].Id;
        var valid = new[] { Pair("甲", "001", "搭档甲", "002"), Pair("乙", "003", "搭档乙", "004") };
        workflow.ImportRoster(id, WriteRoster("valid.xlsx", valid), 0);
        workflow.PreviewDraw(id, new(CompetitionMode.SinglesKnockout, EventKind.Doubles, 1, "identity-test"), 1);
        var session = workflow.CurrentSession!;
        var archiveHash = SHA256.HashData(File.ReadAllBytes(session.WorkspacePath));
        var backups = Directory.GetFiles(directory, "*.backup.szbd").Order().ToArray();
        var sharedId = missingIds ? null : "002";
        var invalid = new[] { valid[0] with { PartnerStudentId = sharedId }, valid[1], Pair("丙", "005", "搭档甲", sharedId) };

        var error = Assert.Throws<WorkspaceCommandException>(() => workflow.ImportRoster(id,
            WriteRoster("invalid.xlsx", invalid), session.Workspace.Revision)).Error;

        Assert.Equal(missingIds ? "roster.player-ambiguous" : "roster.player-duplicate", error.Code);
        Assert.False(error.Committed);
        Assert.Same(session, workflow.CurrentSession);
        Assert.Equal(archiveHash, SHA256.HashData(File.ReadAllBytes(session.WorkspacePath)));
        Assert.Equal(backups, Directory.GetFiles(directory, "*.backup.szbd").Order().ToArray());
        var reopened = new TournamentWorkspaceStore().Read(session.WorkspacePath);
        Assert.Equal(2, reopened.Revision);
        Assert.Equal(session.Workspace.Projects[0].Roster!.Participants, reopened.Projects[0].Roster!.Participants);
        Assert.Equal(session.Workspace.Projects[0].Draw!.Result.Audit, reopened.Projects[0].Draw!.Result.Audit);
        Assert.Equal(session.Workspace.AuditEvents, reopened.AuditEvents);
    }

    [Fact]
    public void SameStudentCanEnterDifferentProjectsAndConfirmBothPublicDraws()
    {
        var workflow = new TournamentWorkspaceWorkflow();
        var created = workflow.CreateWorkspace(new("兼项公开抽签", TournamentKind.Individual, TournamentPurpose.PublicDrawOnly,
            [new(EventDiscipline.MenSingles, CompetitionMode.SinglesKnockout), new(EventDiscipline.MenDoubles, CompetitionMode.SinglesKnockout)],
            Path.Combine(directory, "workspace.szbd")));
        var singlesId = created.Workspace.Projects[0].Id;
        var doublesId = created.Workspace.Projects[1].Id;
        workflow.ImportRoster(singlesId, WriteRoster("singles.xlsx", [new("甲", PrimaryName: "甲", PrimaryStudentId: "001"),
            new("乙", PrimaryName: "乙", PrimaryStudentId: "002")]), 0);
        workflow.ImportRoster(doublesId, WriteRoster("doubles.xlsx", [Pair("甲", "001", "丙", "003"), Pair("乙", "002", "丁", "004")]), 1);
        workflow.PreviewDraw(singlesId, new(CompetitionMode.SinglesKnockout, EventKind.Singles, 1, "singles"), 2);
        workflow.ConfirmDraw(singlesId, 3);
        workflow.PreviewDraw(doublesId, new(CompetitionMode.SinglesKnockout, EventKind.Doubles, 1, "doubles"), 4);
        var confirmed = workflow.ConfirmDraw(doublesId, 5);

        Assert.Equal(TournamentStage.DrawsConfirmed, confirmed.Workspace.Stage);
        Assert.All(confirmed.Workspace.Projects, project => Assert.NotNull(project.Draw!.ConfirmedAt));
        Assert.Null(confirmed.Workspace.Schedule);
        var reopened = new TournamentWorkspaceStore().Read(confirmed.WorkspacePath);
        Assert.All(reopened.Projects, project => Assert.Contains(project.Roster!.Participants, entrant => entrant.PrimaryStudentId == "001"));
    }

    private static DrawParticipant Pair(string primary, string? primaryId, string partner, string? partnerId) =>
        new($"{primary}/{partner}", PrimaryName: primary, PrimaryStudentId: primaryId, PartnerName: partner, PartnerStudentId: partnerId);

    private static ProjectRoster Roster(params DrawParticipant[] participants) => new(participants, "名单.xlsx", "source-hash", []);

    private static TournamentProject Project(EventDiscipline discipline, params DrawParticipant[] participants) =>
        TournamentProject.Create(discipline, discipline == EventDiscipline.Team ? CompetitionMode.TeamKnockout : CompetitionMode.SinglesKnockout, 0)
            with { Roster = Roster(participants) };

    private static TournamentProject Confirm(TournamentProject project)
    {
        var kind = project.Discipline == EventDiscipline.Team ? EventKind.Team : EventKind.Doubles;
        var draw = new DrawService().Generate(project.Roster!.Participants, new(project.CompetitionMode, kind, 1, "direct-domain"));
        return project with { Draw = new(draw, DateTimeOffset.UtcNow), MatchGraph = MatchGraphFactory.Create(project.Id, draw) };
    }

    private string WriteRoster(string name, IReadOnlyList<DrawParticipant> participants)
    {
        var path = Path.Combine(directory, name);
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("名单");
        string[] headings = ["姓名", "学号", "搭档姓名", "搭档学号"];
        for (var column = 0; column < headings.Length; column++) sheet.Cell(1, column + 1).Value = headings[column];
        for (var index = 0; index < participants.Count; index++)
        {
            var participant = participants[index];
            sheet.Cell(index + 2, 1).Value = participant.PrimaryName ?? participant.DisplayName;
            sheet.Cell(index + 2, 2).Value = participant.PrimaryStudentId ?? "";
            sheet.Cell(index + 2, 3).Value = participant.PartnerName ?? "";
            sheet.Cell(index + 2, 4).Value = participant.PartnerStudentId ?? "";
        }
        workbook.SaveAs(path);
        return path;
    }
}
