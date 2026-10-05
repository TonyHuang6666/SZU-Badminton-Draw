using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Workflows.Templates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClosedXML.Excel;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class TemplateExportTests
{
    [Fact]
    public async Task DefaultSelectionExportsFiveUsableFilesWithoutATournament()
    {
        using var destination = new ExportDirectory();
        using var vm = new TemplateExportViewModel(() => Task.FromResult<string?>(destination.Path), _ => Task.FromResult(false));
        await vm.ExportCommand.ExecuteAsync();
        Assert.False(vm.HasError);
        Assert.Equal(new[] { "参赛名单.xlsx", "空白单场比赛计分表.pdf", "空白单场比赛计分表.xlsx", "空白团体比赛计分表.pdf", "空白团体比赛计分表.xlsx" }.Order(),
            Directory.GetFiles(destination.Path).Select(System.IO.Path.GetFileName).Order());
        Assert.Equal(5, vm.OutputPaths.Count);
        foreach (var path in vm.OutputPaths)
        {
            if (path.EndsWith(".xlsx")) { using var workbook = new XLWorkbook(path); Assert.NotEmpty(workbook.Worksheets); }
            else Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4));
        }
        Assert.Empty(Directory.GetDirectories(destination.Path));
    }

    [Fact]
    public async Task SingleRosterSelectionExportsOnlyTheGenericExcel()
    {
        using var destination = new ExportDirectory();
        using var vm = new TemplateExportViewModel(() => Task.FromResult<string?>(destination.Path), _ => Task.FromResult(false))
        { IncludeIndividualScoreSheet = false, IncludeTeamScoreSheet = false, IncludeExcel = false, IncludePdf = false };
        Assert.True(vm.ExportCommand.CanExecute(null));
        await vm.ExportCommand.ExecuteAsync();
        Assert.Equal("参赛名单.xlsx", System.IO.Path.GetFileName(Assert.Single(vm.OutputPaths)));
        using var workbook = new XLWorkbook(vm.OutputPaths[0]);
        Assert.Equal("姓名", workbook.Worksheet(1).Cell("A1").GetString());
    }

    [Fact]
    public async Task PdfOnlySelectionExportsOnlyTheSelectedScoreSheet()
    {
        using var destination = new ExportDirectory();
        using var vm = new TemplateExportViewModel(() => Task.FromResult<string?>(destination.Path), _ => Task.FromResult(false))
        { IncludeRoster = false, IncludeIndividualScoreSheet = false, IncludeExcel = false };
        await vm.ExportCommand.ExecuteAsync();
        Assert.Equal("空白团体比赛计分表.pdf", System.IO.Path.GetFileName(Assert.Single(vm.OutputPaths)));
        Assert.Single(Directory.GetFiles(destination.Path));
    }

    [Fact]
    public void EmptySelectionOrScoreSheetsWithoutAFormatCannotExport()
    {
        using var vm = new TemplateExportViewModel(() => Task.FromResult<string?>(null), _ => Task.FromResult(false))
        { IncludeRoster = false, IncludeIndividualScoreSheet = false, IncludeTeamScoreSheet = false };
        Assert.False(vm.ExportCommand.CanExecute(null));
        vm.IncludeIndividualScoreSheet = true; vm.IncludeExcel = false; vm.IncludePdf = false;
        Assert.False(vm.ExportCommand.CanExecute(null));
        vm.IncludePdf = true;
        Assert.True(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancellingFolderSelectionCreatesNothingAndPreservesChoices()
    {
        using var destination = new ExportDirectory();
        using var vm = new TemplateExportViewModel(() => Task.FromResult<string?>(null), _ => Task.FromResult(false))
        { IncludeRoster = false, IncludeExcel = false };
        await vm.ExportCommand.ExecuteAsync();
        Assert.Empty(vm.OutputPaths); Assert.Empty(Directory.GetFileSystemEntries(destination.Path));
        Assert.False(vm.IncludeRoster); Assert.False(vm.IncludeExcel); Assert.True(vm.IncludePdf);
        Assert.False(vm.IsWorking); Assert.False(vm.HasError);
    }

    [Fact]
    public async Task DecliningOverwriteListsOnlyConflictsAndWritesNothing()
    {
        using var destination = new ExportDirectory();
        var existing = System.IO.Path.Combine(destination.Path, "参赛名单.xlsx");
        File.WriteAllText(existing, "keep existing");
        IReadOnlyList<string>? conflicts = null;
        using var vm = new TemplateExportViewModel(() => Task.FromResult<string?>(destination.Path), paths =>
        { conflicts = paths; return Task.FromResult(false); });
        await vm.ExportCommand.ExecuteAsync();
        Assert.Equal(new[] { existing }, conflicts);
        Assert.Equal("keep existing", File.ReadAllText(existing));
        Assert.Single(Directory.GetFileSystemEntries(destination.Path)); Assert.Empty(vm.OutputPaths);
    }

    [Fact]
    public async Task ConfirmedOverwriteReplacesOnlySelectedOutputs()
    {
        using var destination = new ExportDirectory();
        var existing = System.IO.Path.Combine(destination.Path, "参赛名单.xlsx");
        var unrelated = System.IO.Path.Combine(destination.Path, "保留.txt");
        File.WriteAllText(existing, "replace me"); File.WriteAllText(unrelated, "keep me");
        using var vm = new TemplateExportViewModel(() => Task.FromResult<string?>(destination.Path), _ => Task.FromResult(true))
        { IncludeIndividualScoreSheet = false, IncludeTeamScoreSheet = false };
        await vm.ExportCommand.ExecuteAsync();
        using var workbook = new XLWorkbook(existing);
        Assert.Equal("姓名", workbook.Worksheet(1).Cell("A1").GetString());
        Assert.Equal("keep me", File.ReadAllText(unrelated)); Assert.Single(vm.OutputPaths); Assert.False(vm.HasError);
    }

    [Fact]
    public async Task WriterFailurePreservesExistingFilesAndSelectionsWithoutPublishingEarlierFiles()
    {
        using var destination = new ExportDirectory();
        var existing = System.IO.Path.Combine(destination.Path, "参赛名单.xlsx");
        File.WriteAllText(existing, "keep existing");
        using var vm = new TemplateExportViewModel(() => Task.FromResult<string?>(destination.Path), _ => Task.FromResult(true), new FailingScoreWriter());
        await vm.ExportCommand.ExecuteAsync();
        Assert.True(vm.HasError); Assert.Contains("无法生成", vm.StateMessage);
        Assert.Equal("keep existing", File.ReadAllText(existing));
        Assert.Single(Directory.GetFileSystemEntries(destination.Path)); Assert.Empty(vm.OutputPaths);
        Assert.True(vm.IncludeRoster); Assert.True(vm.IncludeIndividualScoreSheet); Assert.True(vm.IncludeTeamScoreSheet);
        Assert.True(vm.IncludeExcel); Assert.True(vm.IncludePdf); Assert.True(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task NewConflictAfterConfirmationIsNotImplicitlyOverwritten()
    {
        using var destination = new ExportDirectory();
        var existing = System.IO.Path.Combine(destination.Path, "参赛名单.xlsx");
        var newConflict = System.IO.Path.Combine(destination.Path, "空白单场比赛计分表.xlsx");
        File.WriteAllText(existing, "original");
        using var vm = new TemplateExportViewModel(() => Task.FromResult<string?>(destination.Path), _ =>
        { File.WriteAllText(newConflict, "appeared later"); return Task.FromResult(true); });
        await vm.ExportCommand.ExecuteAsync();
        Assert.True(vm.HasError); Assert.Empty(vm.OutputPaths);
        Assert.Equal("original", File.ReadAllText(existing)); Assert.Equal("appeared later", File.ReadAllText(newConflict));
        Assert.Equal(2, Directory.GetFileSystemEntries(destination.Path).Length);
    }

    [Fact]
    public async Task BusyExportRejectsDuplicateSubmissionAndClosingCancelsPendingPicker()
    {
        using var destination = new ExportDirectory();
        var picked = new TaskCompletionSource<string?>(); var pickerCalls = 0;
        using var vm = new TemplateExportViewModel(() => { pickerCalls++; return picked.Task; }, _ => Task.FromResult(false));
        var pending = vm.ExportCommand.ExecuteAsync();
        Assert.True(vm.IsWorking); Assert.False(vm.ExportCommand.CanExecute(null));
        await vm.ExportCommand.ExecuteAsync();
        Assert.Equal(1, pickerCalls);
        vm.Dispose(); picked.SetResult(destination.Path); await pending;
        Assert.Empty(Directory.GetFileSystemEntries(destination.Path)); Assert.Empty(vm.OutputPaths); Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task ClosingDuringGenerationRemovesStagedFilesWithoutPublishing()
    {
        using var destination = new ExportDirectory();
        using var writer = new PausedRosterWriter();
        using var vm = new TemplateExportViewModel(() => Task.FromResult<string?>(destination.Path), _ => Task.FromResult(false), writer)
        { IncludeIndividualScoreSheet = false, IncludeTeamScoreSheet = false };
        var pending = vm.ExportCommand.ExecuteAsync();
        try
        {
            await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            vm.Dispose();
        }
        finally { writer.Continue.Set(); }
        await pending;
        Assert.Empty(Directory.GetFileSystemEntries(destination.Path)); Assert.Empty(vm.OutputPaths);
    }

    [Fact]
    public async Task PickerFailureKeepsSelectionsAndAllowsRetry()
    {
        using var vm = new TemplateExportViewModel(() => Task.FromException<string?>(new IOException("无法选择目录")), _ => Task.FromResult(false))
        { IncludeRoster = false, IncludeIndividualScoreSheet = false, IncludeExcel = false };
        await vm.ExportCommand.ExecuteAsync();
        Assert.True(vm.HasError); Assert.Contains("无法选择目录", vm.StateMessage);
        Assert.False(vm.IncludeRoster); Assert.False(vm.IncludeIndividualScoreSheet); Assert.False(vm.IncludeExcel);
        Assert.True(vm.IncludeTeamScoreSheet); Assert.True(vm.IncludePdf); Assert.True(vm.ExportCommand.CanExecute(null));
        Assert.Empty(vm.OutputPaths);
    }

    private sealed class FailingScoreWriter : ITemplateExportWriter
    {
        public void Write(TemplateExportKind kind, string path)
        {
            if (kind != TemplateExportKind.RosterExcel) throw new IOException("无法生成计分表");
            new BlankTemplateExportWriter().Write(kind, path);
        }
    }

    private sealed class PausedRosterWriter : ITemplateExportWriter, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Continue { get; } = new();
        public void Write(TemplateExportKind kind, string path)
        {
            Started.TrySetResult();
            if (!Continue.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test export was not released.");
            new BlankTemplateExportWriter().Write(kind, path);
        }
        public void Dispose() => Continue.Dispose();
    }

    private sealed class ExportDirectory : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("template-export-tests-");
        public string Path => directory.FullName;
        public void Dispose() => directory.Delete(true);
    }
}

[Collection("Avalonia UI dispatcher")]
public sealed class TemplateExportWindowTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ThreeChoicesAndExportActionFitMinimumSizeInBothThemes(bool dark) => ui.Dispatch(() =>
    {
        var window = new TemplateExportWindow { Width = 720, Height = 600, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var labels = window.GetVisualDescendants().OfType<CheckBox>().Select(c => c.Content?.ToString()).ToArray();
            Assert.Contains("参赛名单", labels); Assert.Contains("空白单场比赛计分表", labels); Assert.Contains("空白团体比赛计分表", labels);
            Assert.Equal(5, labels.Length); // Three templates plus Excel/PDF format controls.
            Assert.Empty(window.GetVisualDescendants().OfType<TextBox>());
            var export = window.FindControl<Button>("ExportButton");
            Assert.NotNull(export); Assert.True(export.IsEffectivelyEnabled);
            var position = export.TranslatePoint(default, window)!.Value;
            Assert.True(position.X >= 0 && position.X + export.Bounds.Width <= window.Bounds.Width);
            Assert.True(position.Y >= 0 && position.Y + export.Bounds.Height <= window.Bounds.Height);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    public void Dispose() => ui.Dispose();
}
