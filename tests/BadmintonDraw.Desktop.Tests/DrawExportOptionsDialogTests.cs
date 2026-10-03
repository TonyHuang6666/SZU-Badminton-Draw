using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using BadmintonDraw.Workflows.Tournaments;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class DrawExportOptionsDialogTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Fact]
    public void ExportUsesTheSelectedSnapshotScopeAndRequestedDefaults()
    {
        var first = new DrawExportScope(Guid.NewGuid(), "当前项目：男子单打", DrawExportState.Preview);
        var all = new DrawExportScope(null, "全部 3 个项目", DrawExportState.Confirmed);
        var scopes = new List<DrawExportScope> { first, all };
        var model = new DrawExportOptionsViewModel(scopes, 1, 4, "2", "3");
        scopes.Clear();

        Assert.True(model.TryCreateSelection(out var selection));
        Assert.Equal(new DrawExportSelection(null, DrawExportState.Confirmed, 4, 2, 3), selection);
        Assert.Equal(2, model.Scopes.Count);
        model.SelectedScope = first;
        Assert.True(model.TryCreateSelection(out selection));
        Assert.Equal(first.ProjectId, selection!.ProjectId);
        Assert.Equal(DrawExportState.Preview, selection.State);
        Assert.Contains("待确认", model.StateHint);
    }

    [Theory]
    [InlineData(1, "0", "1")]
    [InlineData(4, "2", "-1")]
    [InlineData(1, "", "1")]
    [InlineData(4, "2147483648", "2")]
    [InlineData(1, "1.5", "2")]
    public void InvalidPdfDimensionsPreventExport(int format, string rows, string columns)
    {
        var model = CreateModel(format, rows, columns);
        Assert.False(model.CanExport);
        Assert.False(model.TryCreateSelection(out var selection));
        Assert.Null(selection);
        Assert.Contains("正整数", model.ValidationMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void NonPdfExportIgnoresHiddenPaginationInput(int format)
    {
        var model = CreateModel(format, "bad", "0");
        Assert.False(model.UsesPdf);
        Assert.True(model.TryCreateSelection(out var selection));
        Assert.Equal(1, selection!.PdfRows);
        Assert.Equal(1, selection.PdfColumns);
        Assert.Equal(format, selection.FormatIndex);
    }

    [Fact]
    public void ScopeAndFormatMustBelongToTheDialogSnapshot()
    {
        var current = new DrawExportScope(Guid.NewGuid(), "当前项目：女子单打", DrawExportState.Confirmed);
        var unavailable = new DrawExportScope(null, "全部 4 个项目", null);
        var model = new DrawExportOptionsViewModel([current, unavailable]);
        model.SelectedScope = unavailable;
        Assert.False(model.CanExport);
        Assert.Contains("全部导出需要所有项目都已抽签", model.ValidationMessage);
        Assert.False(model.TryCreateSelection(out _));
        model.SelectedScope = current with { State = DrawExportState.Preview };
        Assert.False(model.TryCreateSelection(out _));
        model.SelectedScope = new object();
        Assert.False(model.TryCreateSelection(out _));
        model.SelectedScope = null;
        Assert.False(model.TryCreateSelection(out _));
        model.SelectedScope = current;
        model.ExportFormatIndex = -1;
        Assert.False(model.TryCreateSelection(out _));
        model.ExportFormatIndex = 5;
        Assert.False(model.TryCreateSelection(out _));
        model.ExportFormatIndex = 0;
        Assert.True(model.TryCreateSelection(out _));
        Assert.Contains("已确认", model.StateHint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SmallWindowKeepsActionsVisibleAndPdfSettingsScrollable(bool dark) => ui.Dispatch(() =>
    {
        var model = CreateModel(1);
        var dialog = new DrawExportOptionsDialog(model) { Width = 430, Height = 360 };
        dialog.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            dialog.Show();
            dialog.FindControl<Expander>("PdfPaginationSettings")!.IsExpanded = true;
            Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            var cancel = dialog.FindControl<Button>("CancelButton")!;
            var export = dialog.FindControl<Button>("ExportButton")!;
            Assert.True(cancel.IsFocused);
            Assert.True(cancel.IsDefault);
            Assert.False(export.IsDefault);
            var position = export.TranslatePoint(default, dialog)!.Value;
            Assert.True(position.Y + export.Bounds.Height <= dialog.ClientSize.Height);
            var scroll = dialog.FindControl<ScrollViewer>("SettingsScroll")!;
            Assert.True(scroll.Viewport.Height > 70);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.Equal(2, dialog.GetVisualDescendants().OfType<ComboBox>().Count());
            Assert.True(dialog.FindControl<TextBox>("PdfRowsInput")!.IsEffectivelyVisible);
        }
        finally { dialog.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task InvalidInputIsVisibleAndExportBecomesEnabledOnlyAfterRepair() => ui.Dispatch(() =>
    {
        var model = CreateModel(1);
        var dialog = new DrawExportOptionsDialog(model);
        try
        {
            dialog.Show();
            dialog.FindControl<Expander>("PdfPaginationSettings")!.IsExpanded = true;
            var rows = dialog.FindControl<TextBox>("PdfRowsInput")!;
            rows.Text = "0";
            Dispatcher.UIThread.RunJobs();
            var export = dialog.FindControl<Button>("ExportButton")!;
            Assert.False(export.IsEnabled);
            Assert.Contains("正整数", dialog.FindControl<TextBlock>("ValidationText")!.Text);
            rows.Text = "2";
            Dispatcher.UIThread.RunJobs();
            Assert.True(export.IsEnabled);
            Assert.Equal("", dialog.FindControl<TextBlock>("ValidationText")!.Text);
            dialog.FindControl<ComboBox>("FormatSelector")!.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.FindControl<Expander>("PdfPaginationSettings")!.IsEffectivelyVisible);
        }
        finally { dialog.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData("export", true)]
    [InlineData("cancel", false)]
    [InlineData("escape", false)]
    [InlineData("close", false)]
    [InlineData("enter", false)]
    public Task OnlyExplicitExportAcceptsTheOptions(string action, bool expected) => ui.Dispatch(async () =>
    {
        var owner = new Window();
        var dialog = new DrawExportOptionsDialog(CreateModel());
        try
        {
            owner.Show();
            var result = dialog.ShowDialog<bool>(owner);
            Dispatcher.UIThread.RunJobs();
            if (action is "export" or "cancel")
                dialog.FindControl<Button>(action == "export" ? "ExportButton" : "CancelButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if (action == "close") dialog.Close();
            else
            {
                var key = action == "escape" ? Key.Escape : Key.Enter;
                var physical = action == "escape" ? PhysicalKey.Escape : PhysicalKey.Enter;
                dialog.KeyPress(key, RawInputModifiers.None, physical, null);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.True(result.IsCompleted);
            Assert.Equal(expected, await result);
        }
        finally { dialog.Close(); owner.Close(); }
        return 0;
    }, CancellationToken.None);

    [Fact]
    public Task UnavailableScopeIsDisabledInTheScopeMenu() => ui.Dispatch(() =>
    {
        var current = new DrawExportScope(Guid.NewGuid(), "当前项目：男子双打", DrawExportState.Preview);
        var model = new DrawExportOptionsViewModel([current, new(null, "全部 5 个项目", null)]);
        var dialog = new DrawExportOptionsDialog(model);
        try
        {
            dialog.Show();
            Assert.True(model.CanExport);
            Assert.Contains("全部导出需要所有项目都已抽签", dialog.FindControl<TextBlock>("ScopeHintText")!.Text);
            var scopes = dialog.FindControl<ComboBox>("ScopeSelector")!;
            scopes.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            Assert.True(scopes.ContainerFromIndex(0)!.IsEnabled);
            Assert.False(scopes.ContainerFromIndex(1)!.IsEnabled);
            scopes.IsDropDownOpen = false;
            model.SelectedScope = model.Scopes[1];
            dialog.FindControl<Button>("ExportButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(dialog.IsVisible);
        }
        finally { dialog.Close(); }
    }, CancellationToken.None);

    private static DrawExportOptionsViewModel CreateModel(int format = 0, string rows = "1", string columns = "1") =>
        new([new(Guid.NewGuid(), "当前项目：男子单打", DrawExportState.Confirmed)], 0, format, rows, columns);

    public void Dispose() => ui.Dispose();
}
