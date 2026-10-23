using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class PostponedResultDateTests
{
    [Fact]
    public void OldWorkbookCannotAddResultOnExplicitlyPostponedDay()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2);
        var oldWorkbook = f.Export();
        Postpone(f);
        AssertRejectedWithoutMutation(f, oldWorkbook);
    }

    [Fact]
    public void LaterResultIsAcceptedButAuthorizedCorrectionBackToPostponedDayIsRejected()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2);
        Postpone(f);
        var later = f.Export();
        f.Workflow.ImportResults(f.Workflow.PreviewResultImport([later]), new(false, null), f.Revision);
        Assert.Equal(new DateOnly(2026, 9, 14), Assert.Single(f.Session.Workspace.Results).Value.ActualPlayedDay);
        var correction = f.Export("correction.xlsx");
        WorkspaceResultImportFacadeFixture.Edit(correction, s => s.Cell(6, 22).Value = "2026-09-13");
        AssertRejectedWithoutMutation(f, correction);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(null)]
    public void CoreRejectsResultWithoutDateStrictlyAfterExplicitPostponement(int? dayOffset)
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2);
        Postpone(f);
        var path = f.Export();
        f.Workflow.ImportResults(f.Workflow.PreviewResultImport([path]), new(false, null), f.Revision);
        var workspace = f.Session.Workspace;
        var result = Assert.Single(workspace.Results).Value;
        var candidate = workspace with { Results = workspace.Results.ToDictionary(p => p.Key,
            p => p.Value with { ActualPlayedDay = dayOffset is int offset ? new DateOnly(2026, 9, 13).AddDays(offset) : null }) };
        Assert.Throws<WorkspaceValidationException>(() => WorkspaceDayConfirmationRules.Validate(candidate));
        Assert.Equal(new DateOnly(2026, 9, 14), result.ActualPlayedDay);
    }

    [Fact]
    public void BulkImportWithoutExplicitPostponementStillAcceptsDifferentConfiguredActualDate()
    {
        using var f = new WorkspaceResultImportFacadeFixture(2, 4);
        var path = f.Export();
        WorkspaceResultImportFacadeFixture.Edit(path, s => s.Cell(6, 22).Value = "2026-09-14");
        f.Workflow.ImportResults(f.Workflow.PreviewResultImport([path]), new(false, null), f.Revision);
        Assert.Equal(6, f.Session.Workspace.Results.Count);
        Assert.Contains(f.Session.Workspace.Results.Values, r => r.ActualPlayedDay == new DateOnly(2026, 9, 14));
        Assert.Empty(f.Session.Workspace.DayConfirmations);
    }

    private static void Postpone(WorkspaceResultImportFacadeFixture f)
    {
        var workspace = f.Session.Workspace;
        var match = Assert.Single(workspace.Projects).MatchGraph!.Matches.Single(n => n.IsPlayable);
        var preview = f.Workflow.PreviewDayConfirmation(new(new(2026, 9, 13),
            [new(new(match.ProjectId, match.Id), "天气", true)]), f.Revision);
        Assert.True(preview.CanConfirm, string.Join(";", preview.Errors));
        f.Workflow.ConfirmDayResults(preview, f.Revision);
    }

    private static void AssertRejectedWithoutMutation(WorkspaceResultImportFacadeFixture f, string path)
    {
        var before = f.Session.Workspace;
        var hash = WorkspaceResultImportFacadeFixture.Hash(f.Session.WorkspacePath);
        f.Store.Mutations = 0;
        var preview = f.Workflow.PreviewResultImport([path]);
        Assert.Equal(ResultImportEvaluationStatus.Rejected, preview.Evaluation.Status);
        Assert.Contains(preview.Evaluation.Diagnostics, d => d.Code == "result.postponed-day" && d.Source?.Field == "ActualPlayedDay");
        Assert.Throws<WorkspaceCommandException>(() => f.Workflow.ImportResults(preview, new(true, "核对原始记录后更正日期"), f.Revision));
        Assert.Same(before, f.Session.Workspace);
        Assert.Equal(0, f.Store.Mutations);
        Assert.Equal(hash, WorkspaceResultImportFacadeFixture.Hash(f.Session.WorkspacePath));
    }
}
