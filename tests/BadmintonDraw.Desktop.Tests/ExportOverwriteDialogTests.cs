using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.Views;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class ExportOverwriteDialogTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExistingFilesAreSelectableAndScrollableInBothThemes(bool dark) => ui.Dispatch(() =>
    {
        var paths = Enumerable.Range(1, 30)
            .Select(index => $"/Users/organizer/羽毛球比赛/一个很长的导出目录名称/场次-{index:00}-对阵与成绩.xlsx").ToArray();
        var dialog = CreateDialog(paths);
        dialog.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();
            var shownPaths = dialog.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
            Assert.Equal(paths, shownPaths.Select(block => block.Text));
            Assert.All(shownPaths, block => Assert.Equal(TextWrapping.Wrap, block.TextWrapping));
            Assert.Contains("30", dialog.FindControl<TextBlock>("FileCount")!.Text);
            var scroll = dialog.FindControl<ScrollViewer>("ExistingFilesScroll")!;
            Assert.True(scroll.Viewport.Height > 100);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            var cancel = dialog.FindControl<Button>("CancelButton")!;
            var replace = dialog.FindControl<Button>("ReplaceButton")!;
            Assert.True(cancel.IsFocused);
            Assert.True(cancel.IsDefault);
            Assert.False(replace.IsDefault);
            Assert.True(cancel.Bounds.Height > 0);
            Assert.True(replace.Bounds.Bottom <= dialog.ClientSize.Height);
        }
        finally { dialog.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData("replace", true)]
    [InlineData("cancel", false)]
    [InlineData("escape", false)]
    [InlineData("close", false)]
    [InlineData("enter", false)]
    public Task OnlyExplicitReplaceConfirmsOverwrite(string action, bool expected) => ui.Dispatch(async () =>
    {
        var owner = new Window();
        var dialog = CreateDialog(["/exports/秩序册.pdf", "/exports/成绩.xlsx"]);
        try
        {
            owner.Show();
            var result = dialog.ShowDialog<bool>(owner);
            Dispatcher.UIThread.RunJobs();
            if (action is "replace" or "cancel")
                dialog.FindControl<Button>(action == "replace" ? "ReplaceButton" : "CancelButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if (action == "close") dialog.Close();
            else
            {
                var key = action == "escape" ? Key.Escape : Key.Enter;
                var physicalKey = action == "escape" ? PhysicalKey.Escape : PhysicalKey.Enter;
                dialog.KeyPress(key, RawInputModifiers.None, physicalKey, null);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.True(result.IsCompleted, $"The {action} action should dismiss the dialog.");
            Assert.Equal(expected, await result);
        }
        finally { dialog.Close(); owner.Close(); }
        return 0;
    }, CancellationToken.None);

    private static Window CreateDialog(IReadOnlyList<string> paths) => new ExportOverwriteDialog(paths);

    public void Dispose() => ui.Dispose();
}
