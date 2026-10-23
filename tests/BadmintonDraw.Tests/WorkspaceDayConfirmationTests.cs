using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;
using BadmintonDraw.Persistence;
using System.Text.Json;
using BadmintonDraw.Core;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceDayConfirmationTests
{
    [Fact]
    public void PendingDayCannotBeConfirmedWithoutExplicitPostponements()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 4);
        var day = f.Session.Workspace.Resources!.Days.Min(d => d.Date);
        var preview = f.Workflow.PreviewDayConfirmation(new(day, []), f.Revision);
        Assert.False(preview.CanConfirm);
        Assert.NotEmpty(preview.Errors);
        Assert.Throws<WorkspaceCommandException>(() => f.Workflow.ConfirmDayResults(preview, f.Revision));
        Assert.Empty(f.Session.Workspace.DayConfirmations);
        Assert.Equal(0, f.Store.Mutations);
    }

    [Fact]
    public void BatchPostponementIsOneDurableConfirmationAndCannotBeReused()
    {
        using var f = new WorkspaceResultImportFacadeFixture(2, 4);
        var before = f.Session.Workspace;
        var first = before.Resources!.Days.Min(d => d.Date);
        var next = before.Resources.Days.Max(d => d.Date);
        var items = before.Projects.SelectMany(p => p.MatchGraph!.Matches)
            .Where(n => n.IsPlayable && before.Schedule!.Placements[n.Id].DayLabel == first.ToString("yyyy-MM-dd"))
            .Select(n => new WorkspaceMatchPostponement(new(n.ProjectId, n.Id), "雨天", true)).ToArray();
        var preview = f.Workflow.PreviewDayConfirmation(new(first, items), f.Revision);
        Assert.True(preview.CanConfirm, string.Join(";", preview.Errors));
        Assert.Equal(0, f.Store.Mutations);
        Assert.Empty(before.DayConfirmations);
        Assert.All(preview.Changes, c => Assert.Equal(next.ToString("yyyy-MM-dd"), c.After.DayLabel));
        f.Workflow.ConfirmDayResults(preview, f.Revision);
        var durable = new TournamentWorkspaceStore().Read(f.Session.WorkspacePath);
        Assert.Single(durable.DayConfirmations);
        Assert.Empty(durable.ProcessedDays);
        Assert.Equal(1, f.Store.Mutations);
        Assert.Equal(next, WorkspaceDayConfirmationRules.NextUnconfirmedDay(durable));
        Assert.Equal(JsonSerializer.Serialize(f.Session.Workspace.DayConfirmations), JsonSerializer.Serialize(durable.DayConfirmations));
        Assert.Throws<WorkspaceCommandException>(() => f.Workflow.ConfirmDayResults(preview, f.Revision));
        var lastItems = durable.Projects.SelectMany(p => p.MatchGraph!.Matches).Where(n => n.IsPlayable)
            .Select(n => new WorkspaceMatchPostponement(new(n.ProjectId, n.Id), "雨天", true)).ToArray();
        Assert.False(f.Workflow.PreviewDayConfirmation(new(next, lastItems), f.Revision).CanConfirm);
    }

    [Fact]
    public void MissingApprovalAndOutOfOrderConfirmationLeaveArchiveUntouched()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 4);
        var workspace = f.Session.Workspace;
        var hash = WorkspaceResultImportFacadeFixture.Hash(f.Session.WorkspacePath);
        var first = workspace.Resources!.Days.Min(d => d.Date);
        var items = workspace.Projects.SelectMany(p => p.MatchGraph!.Matches).Where(n => n.IsPlayable)
            .Select(n => new WorkspaceMatchPostponement(new(n.ProjectId, n.Id), "天气", false)).ToArray();
        Assert.False(f.Workflow.PreviewDayConfirmation(new(first, items), f.Revision).CanConfirm);
        Assert.False(f.Workflow.PreviewDayConfirmation(new(first.AddDays(1), []), f.Revision).CanConfirm);
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(f.Session.WorkspacePath));
        Assert.Equal(0, f.Store.Mutations);
    }

    [Fact]
    public void CapacityFailureDoesNotPublishPartialPostponements()
    {
        using var f = new WorkspaceResultImportFacadeFixture(2, 4);
        var original = f.Session.Workspace;
        var resources = original.Resources! with { Days = original.Resources!.Days.Select((d, i) =>
            i == 1 ? d with { DayEnd = new TimeOnly(9, 1) } : d).ToArray() };
        new TournamentWorkspaceStore().Mutate(f.Session.WorkspacePath, f.Revision, w => w with
            { Resources = resources, Schedule = w.Schedule! with { Resources = resources } });
        f.Workflow.OpenWorkspace(f.Session.WorkspacePath);
        var hash = WorkspaceResultImportFacadeFixture.Hash(f.Session.WorkspacePath);
        var first = resources.Days.Min(d => d.Date);
        var items = original.Projects.SelectMany(p => p.MatchGraph!.Matches).Where(n => n.IsPlayable)
            .Select(n => new WorkspaceMatchPostponement(new(n.ProjectId, n.Id), "天气", true)).ToArray();
        var preview = f.Workflow.PreviewDayConfirmation(new(first, items), f.Revision);
        Assert.False(preview.CanConfirm);
        Assert.Empty(preview.Changes);
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(f.Session.WorkspacePath));
        Assert.Equal(0, f.Store.Mutations);
    }

    [Fact]
    public void FailedSaveKeepsConfirmationAndScheduleUnpublished()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 4);
        var before = f.Session.Workspace;
        var hash = WorkspaceResultImportFacadeFixture.Hash(f.Session.WorkspacePath);
        var items = before.Projects.SelectMany(p => p.MatchGraph!.Matches).Where(n => n.IsPlayable)
            .Select(n => new WorkspaceMatchPostponement(new(n.ProjectId, n.Id), "天气", true)).ToArray();
        var preview = f.Workflow.PreviewDayConfirmation(new(before.Resources!.Days.Min(d => d.Date), items), f.Revision);
        Assert.True(preview.CanConfirm, string.Join(";", preview.Errors));
        f.Store.BeforeMutation = () => throw new IOException("save unavailable");
        Assert.Throws<WorkspaceCommandException>(() => f.Workflow.ConfirmDayResults(preview, f.Revision));
        Assert.Same(before, f.Session.Workspace);
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(f.Session.WorkspacePath));
        Assert.Empty(f.Session.Workspace.DayConfirmations);
    }

    [Fact]
    public void MoveIntoConfirmedEmptyDayCannotBeSaved()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 4);
        var before = f.Session.Workspace;
        var first = before.Resources!.Days.Min(d => d.Date);
        var items = before.Projects.SelectMany(p => p.MatchGraph!.Matches).Where(n => n.IsPlayable)
            .Select(n => new WorkspaceMatchPostponement(new(n.ProjectId, n.Id), "天气", true)).ToArray();
        f.Workflow.ConfirmDayResults(f.Workflow.PreviewDayConfirmation(new(first, items), f.Revision), f.Revision);
        var baseline = f.Workflow.CaptureScheduleEditBaseline();
        var node = before.Projects.Single().MatchGraph!.Matches.Where(n => n.IsPlayable).OrderBy(n => n.Order).First();
        var hash = WorkspaceResultImportFacadeFixture.Hash(f.Session.WorkspacePath);
        Assert.False(f.Workflow.PreviewMove(new(new(node.ProjectId, node.Id), first.ToString("yyyy-MM-dd"),
            new(9, 0), "B1", baseline), f.Revision).CanApply);
        Assert.Throws<WorkspaceCommandException>(() => f.Workflow.MoveMatch(new(new(node.ProjectId, node.Id),
            first.ToString("yyyy-MM-dd"), new(9, 0), "B1", baseline), f.Revision));
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(f.Session.WorkspacePath));
        Assert.Single(f.Session.Workspace.DayConfirmations);
        Assert.False(f.Workflow.CanUndoScheduleEdit);
    }

    [Fact]
    public void CompletingAndSupplementingMetadataKeepsConfirmedDay()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2);
        var path = f.Export();
        WorkspaceResultImportFacadeFixture.Edit(path, s => { s.Cell(6, 9).Clear(); s.Cell(6, 10).Clear(); });
        f.Workflow.ImportResults(f.Workflow.PreviewResultImport([path]), new(false, null), f.Revision);
        Assert.Empty(f.Session.Workspace.DayConfirmations);
        var day = f.Session.Workspace.Resources!.Days.Min(d => d.Date);
        var preview = f.Workflow.PreviewDayConfirmation(new(day, []), f.Revision);
        Assert.True(preview.CanConfirm, string.Join(";", preview.Errors));
        Assert.Equal(1, preview.CompletedCount);
        f.Workflow.ConfirmDayResults(preview, f.Revision);
        var confirmation = JsonSerializer.Serialize(f.Session.Workspace.DayConfirmations);
        var supplement = f.Export("补齐.xlsx");
        f.Workflow.ImportResults(f.Workflow.PreviewResultImport([supplement]), new(false, null), f.Revision);
        Assert.Equal(confirmation, JsonSerializer.Serialize(f.Session.Workspace.DayConfirmations));
        Assert.Equal("21-10", Assert.Single(f.Session.Workspace.Results).Value.Score);
        Assert.Equal(20, Assert.Single(f.Session.Workspace.Results).Value.DurationMinutes);
        var durable = new TournamentWorkspaceStore().Read(f.Session.WorkspacePath);
        Assert.Equal(confirmation, JsonSerializer.Serialize(durable.DayConfirmations));
    }

    [Fact]
    public void LegacyHeaderWithoutConfirmationFieldsDoesNotInferConfirmationFromProcessedDays()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2);
        var path = f.Export();
        f.Workflow.ImportResults(f.Workflow.PreviewResultImport([path]), new(false, null), f.Revision);
        Assert.NotEmpty(f.Session.Workspace.ProcessedDays);
        WorkspaceStoreTemp.Sql(f.Session.WorkspacePath,
            "UPDATE workspace SET json=json_remove(json,'$.DayConfirmations','$.DayConfirmationCount')");
        var read = new TournamentWorkspaceStore().Read(f.Session.WorkspacePath);
        Assert.Empty(read.DayConfirmations);
        Assert.Equal(new DateOnly(2026, 9, 13), WorkspaceDayConfirmationRules.NextUnconfirmedDay(read));
    }

    [Fact]
    public void MissingNewHeaderConfirmationCountIsRejected()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2);
        WorkspaceStoreTemp.Sql(f.Session.WorkspacePath,
            "UPDATE workspace SET json=json_remove(json,'$.DayConfirmationCount')");
        Assert.Throws<WorkspaceStoreException>(() => new TournamentWorkspaceStore().Read(f.Session.WorkspacePath));
    }

    [Fact]
    public void BatchPostponementKeepsCompletedMatchAtItsOriginalPlacement()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 4);
        var project = f.Session.Workspace.Projects.Single();
        var played = project.MatchGraph!.Matches.Where(n => n.IsPlayable).OrderBy(n => n.Order).First();
        var path = f.Export(keys: [new(project.Id, played.Id)]);
        f.Workflow.ImportResults(f.Workflow.PreviewResultImport([path]), new(false, null), f.Revision);
        var before = f.Session.Workspace;
        var first = before.Resources!.Days.Min(d => d.Date);
        var pending = project.MatchGraph.Matches.Where(n => n.IsPlayable && n.Id != played.Id)
            .Select(n => new WorkspaceMatchPostponement(new(n.ProjectId, n.Id), "天气", true)).ToArray();
        var preview = f.Workflow.PreviewDayConfirmation(new(first, pending), f.Revision);
        Assert.True(preview.CanConfirm, string.Join(";", preview.Errors));
        Assert.Equal(1, preview.CompletedCount);
        Assert.DoesNotContain(preview.Changes, c => c.Key.MatchId == played.Id);
        f.Workflow.ConfirmDayResults(preview, f.Revision);
        Assert.Equal(before.Schedule!.Placements[played.Id], f.Session.Workspace.Schedule!.Placements[played.Id]);
        Assert.Equal(JsonSerializer.Serialize(before.Results[new(project.Id, played.Id)]),
            JsonSerializer.Serialize(f.Session.Workspace.Results[new(project.Id, played.Id)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOverlappingSourceCannotConfirmCompletedOrEmptyDay(bool emptyDay)
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 4);
        if (!emptyDay)
        {
            var path = f.Export();
            f.Workflow.ImportResults(f.Workflow.PreviewResultImport([path]), new(false, null), f.Revision);
        }
        var workspace = f.Session.Workspace;
        var first = workspace.Resources!.Days.Min(d => d.Date);
        var target = emptyDay ? workspace.Resources.Days.Max(d => d.Date) : first;
        new TournamentWorkspaceStore().Mutate(f.Session.WorkspacePath, f.Revision, w => w with
        {
            Schedule = w.Schedule! with { Placements = w.Schedule.Placements.ToDictionary(p => p.Key,
                p => p.Value with { DayLabel = target.ToString("yyyy-MM-dd"), StartTime = new(9, 0), EndTime = new(9, 30), Court = "B1" }) }
        });
        f.Workflow.OpenWorkspace(f.Session.WorkspacePath);
        var hash = WorkspaceResultImportFacadeFixture.Hash(f.Session.WorkspacePath);
        var preview = f.Workflow.PreviewDayConfirmation(new(first, []), f.Revision);
        Assert.False(preview.CanConfirm);
        Assert.NotEmpty(preview.Errors);
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(f.Session.WorkspacePath));
    }
}
