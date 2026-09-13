using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Tests;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class ResultImportPanelTests : IDisposable
{
    private readonly HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public Task EvidenceKeepsBlankLinesAndCopiesTheWholeUnmodifiedText(string newline) => session.Dispatch(async () =>
    {
        var original = string.Join(newline, new[] { "首行", "", new string('名', 90) + " / SHA-256: abc", "", "末行", "" });
        var evidence = new ResultImportEvidenceText { Text = original };
        Assert.All(evidence.Items.OfType<string>(), line => Assert.DoesNotContain('\r', line));
        var window = new Window { Width = 500, Height = 650, Content = new ScrollViewer { Content = evidence, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        try
        {
            window.Show(); window.UpdateLayout(); AssertEvidence(evidence);
            var lines = evidence.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
            Assert.All(lines.Where(l => l.Text == ""), line => Assert.True(line.Bounds.Height >= line.FontSize));
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var point = lines[0].TranslatePoint(new Avalonia.Point(8, 8), window)!.Value;
            var pointer = 0; var context = 0;
            lines[0].AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, _) => pointer++, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            lines[0].AddHandler(Control.ContextRequestedEvent, (_, _) => context++, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            window.MouseMove(point, RawInputModifiers.None);
            window.MouseDown(point, MouseButton.Right, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs(); Assert.True(pointer > 0); Assert.True(context > 0); Assert.True(lines[0].ContextMenu!.IsOpen);
            var copy = Assert.IsType<MenuItem>(Assert.Single(lines[0].ContextMenu!.Items));
            Assert.Same(evidence.CopyAllCommand, copy.Command); Assert.True(copy.Command!.CanExecute(null));
            await evidence.CopyAllCommand.ExecuteAsync();
            Assert.Equal(original, await window.Clipboard!.TryGetTextAsync());
            lines[0].ContextMenu!.Close();
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);
    [Fact]
    public Task CompiledPanelBindingsRequireExplicitConsentAndDisplayRealSavedOutcome() => session.Dispatch(async () =>
    {
        using var f = new ResultImportUiFixture(1, 2, post: a => Dispatcher.UIThread.Post(a)); f.NextPick = [f.Data.Export()];
        var panel = new ResultImportPanel { DataContext = f.ViewModel }; var window = new Window { Width = 880, Height = 680, Content = panel };
        try
        {
            window.Show(); window.UpdateLayout();
            var pick = panel.FindControl<Button>("PickResultFiles"); Assert.NotNull(pick);
            await Assert.IsType<AsyncCommand>(pick.Command).ExecuteAsync();
            Assert.Contains(f.NextPick[0], panel.FindControl<ResultImportEvidenceText>("SelectedResultPaths")!.Text);
            await Assert.IsType<AsyncCommand>(panel.FindControl<Button>("PreviewResultImport")!.Command).ExecuteAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var confirm = panel.FindControl<CheckBox>("ConfirmResultImport")!; var apply = panel.FindControl<Button>("ApplyResultImport")!;
            Assert.False(confirm.IsChecked); Assert.False(apply.IsEffectivelyEnabled);
            confirm.IsChecked = true; Assert.True(f.ViewModel.Confirmed); Assert.True(apply.IsEffectivelyEnabled);
            await Assert.IsType<AsyncCommand>(apply.Command).ExecuteAsync(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.False(confirm.IsChecked); Assert.False(apply.IsEffectivelyEnabled);
            Assert.Contains("已保存", panel.FindControl<ResultImportEvidenceText>("ResultImportState")!.Text);
            Assert.Contains(f.Shell.WorkspacePath, panel.FindControl<ResultImportEvidenceText>("ResultImportOutcome")!.Text);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CompiledPanelShowsEveryLongConflictSourceAndFullCorrectionFacts(bool longNames) => session.Dispatch(async () =>
    {
        using var f = new ResultImportUiFixture(1, 2, post: a => Dispatcher.UIThread.Post(a));
        var first = f.Data.Export(longNames ? new string('原', 45) + ".xlsx" : "first.xlsx"); var second = f.Data.Export(longNames ? new string('异', 45) + ".xlsx" : "second.xlsx");
        WorkspaceResultImportFacadeFixture.Edit(second, s => { s.Cell(6, 12).Value = "B"; s.Cell(6, 9).Value = "10-21"; });
        var panel = new ResultImportPanel { DataContext = f.ViewModel }; var window = new Window { Width = 880, Height = 620, Content = panel };
        try
        {
            window.Show(); await f.Preview(first, second); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(ResultImportEvaluationStatus.Rejected, f.ViewModel.PreviewStatus);
            var diagnostics = panel.FindControl<ResultImportEvidenceText>("ResultImportDiagnostics"); Assert.NotNull(diagnostics);
            Assert.Contains(first, diagnostics.Text); Assert.Contains(second, diagnostics.Text);
            Assert.Contains(WorkspaceResultImportFacadeFixture.Hash(first), diagnostics.Text);
            Assert.Contains(WorkspaceResultImportFacadeFixture.Hash(second), diagnostics.Text);
            Assert.Contains("第 6 行", diagnostics.Text);
            AssertEvidence(diagnostics);
            Assert.True(panel.Bounds.Width <= window.ClientSize.Width); Assert.True(diagnostics.Bounds.Width <= window.ClientSize.Width);
            await f.Preview(first); await f.Accept();
            var correction = f.Data.Export("correction.xlsx"); WorkspaceResultImportFacadeFixture.Edit(correction, s => s.Cell(6, 10).Value = 25);
            await f.Preview(correction); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var change = Assert.Single(f.ViewModel.Corrections); var correctionText = panel.FindControl<ResultImportEvidenceText>("ResultImportCorrections")!; var details = correctionText.Text!;
            Assert.Contains(change.Key.ProjectId.ToString(), details); Assert.Contains(change.Key.MatchId.ToString(), details);
            Assert.Contains(change.Before.Winner.IdentityKey, details); Assert.Contains(change.Before.Loser.IdentityKey, details);
            Assert.Contains(change.Before.RecordedAt.ToString("O"), details); Assert.Contains("20", details); Assert.Contains("25", details);
            Assert.Contains(change.Source.SourcePath, details); Assert.Contains("正常", details);
            AssertEvidence(correctionText);
            panel.FindControl<TextBox>("ResultCorrectionReason")!.Text = "确认裁判原始记录";
            panel.FindControl<CheckBox>("AllowResultCorrections")!.IsChecked = true;
            var apply = panel.FindControl<Button>("ApplyResultImport")!; Assert.False(apply.IsEffectivelyEnabled);
            panel.FindControl<CheckBox>("ConfirmResultImport")!.IsChecked = true; Assert.True(apply.IsEffectivelyEnabled);
            await Assert.IsType<AsyncCommand>(apply.Command).ExecuteAsync();
            Assert.Single(f.Shell.CurrentSession!.Workspace.ResultHistory);
        }
        finally { window.Close(); }
        return 0;
    }, CancellationToken.None);
    private static void AssertEvidence(ResultImportEvidenceText evidence)
    {
        var lines = evidence.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
        Assert.NotEmpty(lines); Assert.Equal(evidence.Text?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), string.Join("\n", lines.Select(l => l.Text)));
        Assert.All(lines, line => { Assert.Equal(TextWrapping.Wrap, line.TextWrapping); Assert.True(line.Bounds.Width <= evidence.Bounds.Width); });
    }
    public void Dispose() => session.Dispose();
}
