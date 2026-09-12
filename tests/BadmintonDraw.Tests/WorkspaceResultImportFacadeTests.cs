using System.Text.Json;
using BadmintonDraw.Core.Matches;
using BadmintonDraw.Core.Scheduling;
using BadmintonDraw.Core.Tournaments;
using BadmintonDraw.Persistence;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;
using static BadmintonDraw.Tests.WorkspaceResultImportFacadeFixture;

namespace BadmintonDraw.Tests;

public sealed class WorkspaceResultImportFacadeTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    public void PreviewIsReadOnlyAndAcceptancePublishesAllProjectsExactlyOnce(int projects, int entrants)
    {
        using var f = new WorkspaceResultImportFacadeFixture(projects, entrants);
        var path = f.Export(); var before = f.Session; var hash = Hash(before.WorkspacePath);
        var files = Directory.GetFiles(f.DirectoryPath).Order().ToArray(); var notifications = 0;
        f.Workflow.SessionChanged += (_, _) => notifications++;
        var preview = f.Workflow.PreviewResultImport([path]);
        Assert.Equal(ResultImportEvaluationStatus.Ready, preview.Evaluation.Status);
        Assert.Equal(before.Workspace.Revision, preview.SourceRevision);
        Assert.Same(before, f.Session); Assert.Equal(hash, Hash(before.WorkspacePath));
        Assert.Equal(files, Directory.GetFiles(f.DirectoryPath).Order().ToArray());
        Assert.Equal(0, f.Store.Mutations); Assert.Equal(0, notifications);

        var outcome = f.Workflow.ImportResults(preview, new(false, null), preview.SourceRevision);

        Assert.False(outcome.NoChanges); Assert.Equal(1, f.Store.Mutations); Assert.Equal(1, notifications);
        var saved = new TournamentWorkspaceStore().Read(before.WorkspacePath);
        Assert.Equal(before.Workspace.Revision + 1, saved.Revision);
        Assert.Equal(TournamentStage.Completed, saved.Stage);
        Assert.Equal(saved.Projects.Sum(p => p.MatchGraph!.Matches.Count), saved.Results.Count);
        Assert.Equal(projects, saved.Results.Keys.Select(k => k.ProjectId).Distinct().Count());
        Assert.Single(saved.ImportLogs); Assert.Single(saved.AuditEvents, a => a.Action == "ResultsImported");
        Assert.Equal(hash, Hash(outcome.Command.BackupPath!));
        Assert.Equal(JsonSerializer.Serialize(before.Workspace.Schedule), JsonSerializer.Serialize(saved.Schedule));
        Assert.Equal(JsonSerializer.Serialize(before.Workspace.Projects), JsonSerializer.Serialize(saved.Projects));
        Assert.Equal(saved.Results.Count, outcome.Evaluation.ProposedCounts.AddedResultCount);
    }

    [Fact]
    public void RepeatedBytesAreTrueNoChangesWithoutAnySaveOrNotification()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2); var path = f.Export();
        var first = f.Workflow.PreviewResultImport([path]); f.Workflow.ImportResults(first, new(false, null), f.Revision);
        var before = f.Session; var hash = Hash(before.WorkspacePath); var files = Directory.GetFiles(f.DirectoryPath).Order().ToArray();
        f.Store.Mutations = 0; var notices = 0; f.Workflow.SessionChanged += (_, _) => notices++;
        var preview = f.Workflow.PreviewResultImport([path]);
        Assert.Equal(ResultImportEvaluationStatus.NoChanges, preview.Evaluation.Status);
        var result = f.Workflow.ImportResults(preview, new(false, null), f.Revision);
        Assert.True(result.NoChanges); Assert.Null(result.Command.BackupPath);
        Assert.Same(before, f.Session); Assert.Equal(0, f.Store.Mutations); Assert.Equal(0, notices);
        Assert.Equal(hash, Hash(before.WorkspacePath)); Assert.Equal(files, Directory.GetFiles(f.DirectoryPath).Order().ToArray());
    }

    [Theory]
    [InlineData("file")]
    [InlineData("reopen")]
    [InlineData("owner")]
    [InlineData("external")]
    [InlineData("revision")]
    public void APreviouslyReadyPreviewCannotFollowChangedInputOrSession(string change)
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2); var path = f.Export();
        var preview = f.Workflow.PreviewResultImport([path]); var workflow = f.Workflow;
        if (change == "file") Edit(path, s => s.Cell(6, 10).Value = 25);
        if (change == "reopen") workflow.OpenWorkspace(f.Session.WorkspacePath);
        if (change == "owner") { workflow = new(f.Store); workflow.OpenWorkspace(f.Session.WorkspacePath); }
        if (change == "external") new TournamentWorkspaceStore().Mutate(f.Session.WorkspacePath, f.Revision, w => w with { Name = "外部修改" });
        var hash = Hash(f.Session.WorkspacePath); var before = workflow.CurrentSession;
        var error = Assert.Throws<WorkspaceCommandException>(() => workflow.ImportResults(preview, new(false, null),
            change == "revision" ? f.Revision + 1 : f.Revision)).Error;
        Assert.False(error.Committed); Assert.Equal(0, f.Store.Mutations);
        Assert.Same(before, workflow.CurrentSession); Assert.Equal(hash, Hash(f.Session.WorkspacePath));
        Assert.Empty(new TournamentWorkspaceStore().Read(f.Session.WorkspacePath).Results);
    }

    [Fact]
    public void OneBadRowRejectsTheWholeWorkbookBeforeMutation()
    {
        using var f = new WorkspaceResultImportFacadeFixture(); var path = f.Export();
        Edit(path, s => s.Cell(6, 17).Value = Guid.NewGuid().ToString());
        var hash = Hash(f.Session.WorkspacePath); var preview = f.Workflow.PreviewResultImport([path]);
        Assert.Equal(ResultImportEvaluationStatus.Rejected, preview.Evaluation.Status);
        Assert.Throws<WorkspaceCommandException>(() => f.Workflow.ImportResults(preview, new(true, "不能覆盖此冲突"), f.Revision));
        Assert.Equal(0, f.Store.Mutations); Assert.Equal(hash, Hash(f.Session.WorkspacePath)); Assert.Empty(f.Session.Workspace.Results);
    }

    [Theory]
    [InlineData("missing.xlsx")]
    [InlineData("broken.xlsx")]
    [InlineData("wrong.csv")]
    public void DocumentFailuresAreFilenameBearingResultsErrors(string name)
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2); var path = f.PathFor(name);
        if (!name.StartsWith("missing", StringComparison.Ordinal)) File.WriteAllText(path, "not a workbook");
        var hash = Hash(f.Session.WorkspacePath);
        var error = Assert.Throws<WorkspaceCommandException>(() => f.Workflow.PreviewResultImport([path])).Error;
        Assert.Equal("results.read", error.Code); Assert.Contains(name, error.Message);
        Assert.Equal(0, f.Store.Mutations); Assert.Equal(hash, Hash(f.Session.WorkspacePath));
    }

    [Fact]
    public void MetadataCorrectionNeedsReviewedFactsAndReasonAndCannotBeReversedByAnOldHash()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2); var first = f.Export("原始.xlsx");
        f.Workflow.ImportResults(f.Workflow.PreviewResultImport([first]), new(false, null), f.Revision);
        var oldResult = Assert.Single(f.Session.Workspace.Results).Value;
        var correction = f.Export("更正.xlsx"); Edit(correction, s => s.Cell(6, 10).Value = 25);
        var preview = f.Workflow.PreviewResultImport([correction]);
        Assert.Equal(ResultImportEvaluationStatus.RequiresConfirmation, preview.Evaluation.Status);
        var proposed = Assert.Single(preview.Evaluation.Corrections);
        Assert.Equal(oldResult.RecordedAt, proposed.Before.RecordedAt);
        Assert.Equal(20, proposed.Before.DurationMinutes); Assert.Equal(25, proposed.After.DurationMinutes);
        var hash = Hash(f.Session.WorkspacePath); f.Store.Mutations = 0;
        Assert.Throws<WorkspaceCommandException>(() => f.Workflow.ImportResults(preview, new(false, "更正时长"), f.Revision));
        Assert.Throws<WorkspaceCommandException>(() => f.Workflow.ImportResults(preview, new(true, " "), f.Revision));
        Assert.Equal(0, f.Store.Mutations); Assert.Equal(hash, Hash(f.Session.WorkspacePath));
        var corrected = f.Workflow.ImportResults(preview, new(true, "核对裁判原始记录，更正时长"), f.Revision);
        Assert.Equal(1, f.Store.Mutations); Assert.Equal(1, corrected.Evaluation.ProposedCounts.CorrectionCount);
        var current = f.Session.Workspace;
        var history = Assert.Single(current.ResultHistory);
        Assert.Equal(oldResult.RecordedAt, history.Before.RecordedAt);
        Assert.Equal("核对裁判原始记录，更正时长", history.Reason);
        Assert.Equal(25, current.Results[oldResult.Key].DurationMinutes);
        Assert.Equal(TournamentStage.Completed, current.Stage);
        Assert.Equal(2, current.AuditEvents.Count(a => a.Action == "ResultsImported"));
        var old = f.Workflow.PreviewResultImport([first]); f.Store.Mutations = 0;
        var repeated = f.Workflow.ImportResults(old, new(false, null), f.Revision);
        Assert.True(repeated.NoChanges); Assert.Equal(0, f.Store.Mutations);
        Assert.Same(current.Results[oldResult.Key], f.Session.Workspace.Results[oldResult.Key]);
        Assert.Single(f.Session.Workspace.ResultHistory);
    }

    [Fact]
    public void FileChangedAfterCorrectionPreviewCannotBroadenTheApproval()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2); var path = f.Export();
        f.Workflow.ImportResults(f.Workflow.PreviewResultImport([path]), new(false, null), f.Revision);
        Edit(path, s => s.Cell(6, 10).Value = 25);
        var preview = f.Workflow.PreviewResultImport([path]);
        Assert.Equal(ResultImportEvaluationStatus.RequiresConfirmation, preview.Evaluation.Status);
        Edit(path, s => s.Cell(6, 10).Value = 45);
        var hash = Hash(f.Session.WorkspacePath); f.Store.Mutations = 0;
        var error = Assert.Throws<WorkspaceCommandException>(() => f.Workflow.ImportResults(preview, new(true, "仅同意原预览的25分钟"), f.Revision)).Error;
        Assert.Equal("results.file-changed", error.Code);
        Assert.Equal(0, f.Store.Mutations); Assert.Equal(hash, Hash(f.Session.WorkspacePath));
        Assert.Empty(f.Session.Workspace.ResultHistory);
    }

    [Fact]
    public void PendingCoverageDoesNotPreventReplanningAndTheOldReceiptStaysVoided()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2); var path = f.Export(fill: false);
        f.Workflow.ImportResults(f.Workflow.PreviewResultImport([path]), new(false, null), f.Revision);
        Assert.Empty(f.Session.Workspace.Results); Assert.Single(f.Session.Workspace.ProcessedDays);
        var schedule = f.Session.Workspace.Schedule!;
        f.Workflow.GenerateSchedule(schedule.Resources, schedule.Policy, f.Revision);
        var log = Assert.Single(f.Session.Workspace.ImportLogs);
        Assert.NotNull(log.VoidedAt); Assert.Empty(f.Session.Workspace.ProcessedDays);
        var preview = f.Workflow.PreviewResultImport([path]);
        Assert.Equal(ResultImportEvaluationStatus.NoChanges, preview.Evaluation.Status);
        f.Store.Mutations = 0;
        var result = f.Workflow.ImportResults(preview, new(false, null), f.Revision);
        Assert.True(result.NoChanges); Assert.Equal(0, f.Store.Mutations);
        Assert.Empty(f.Session.Workspace.ProcessedDays); Assert.NotNull(Assert.Single(f.Session.Workspace.ImportLogs).VoidedAt);
    }

    [Fact]
    public void CallerChangingItsPathListDoesNotRetargetCapturedPreview()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 2); var path = f.Export();
        var files = new List<string> { path }; var preview = f.Workflow.PreviewResultImport(files);
        files[0] = f.PathFor("不存在.xlsx"); files.Add(f.PathFor("额外.xlsx"));
        var result = f.Workflow.ImportResults(preview, new(false, null), f.Revision);
        Assert.False(result.NoChanges);
        Assert.Equal(path, Assert.Single(f.Session.Workspace.ImportLogs).SourcePath);
    }

    [Fact]
    public void ReversedPrerequisiteFilesResolveTheRealDownstreamProjectionInOneSave()
    {
        using var f = new WorkspaceResultImportFacadeFixture(1, 4);
        var project = f.Session.Workspace.Projects[0]; var nodes = project.MatchGraph!.Matches;
        var final = Assert.Single(nodes, n => n.Dependencies.Count > 0);
        var prerequisites = f.Export("首轮.xlsx", keys: nodes.Where(n => n.Dependencies.Count == 0)
            .Select(n => new WorkspaceMatchKey(project.Id, n.Id)).ToArray());
        var later = f.Export("决赛.xlsx", keys: [new(project.Id, final.Id)]);
        var preview = f.Workflow.PreviewResultImport([later, prerequisites]);
        Assert.Equal(ResultImportEvaluationStatus.Ready, preview.Evaluation.Status);
        f.Workflow.ImportResults(preview, new(false, null), f.Revision);
        Assert.Equal(1, f.Store.Mutations); Assert.Equal(2, f.Session.Workspace.ImportLogs.Count);
        var saved = f.Session.Workspace;
        var projection = ScheduledMatchProjection.Build(saved.Projects.Select(p => p.MatchGraph!).ToArray(), saved.Schedule!, saved.Results);
        var row = Assert.Single(projection, m => m.MatchId == final.Id.ToString("D"));
        var a = Assert.IsType<EntrantSource.WinnerOf>(final.SideA);
        var b = Assert.IsType<EntrantSource.WinnerOf>(final.SideB);
        Assert.Equal(saved.Results[new(project.Id, a.MatchId)].Winner.DisplayName, row.SideA);
        Assert.Equal(saved.Results[new(project.Id, b.MatchId)].Winner.DisplayName, row.SideB);
        Assert.Equal(TournamentStage.Completed, saved.Stage);
    }
}
