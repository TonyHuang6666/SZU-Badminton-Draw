using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BadmintonDraw.Desktop.ViewModels;
using BadmintonDraw.Desktop.Views;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

[Collection("Avalonia UI dispatcher")]
public sealed class NewWorkspaceWizardChoiceCardTests : IDisposable
{
    private readonly HeadlessUnitTestSession ui = HeadlessUnitTestSession.StartNew(typeof(App));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ChoiceCardTextUsesTheFullPaddedWidthWithoutARadioIndicator(bool dark) => ui.Dispatch(() =>
    {
        using var fixture = new WizardFixture(dark);
        Assert.Equal(4, fixture.Choices.Length);
        foreach (var card in fixture.Choices)
        {
            Assert.Empty(card.GetVisualDescendants().OfType<Ellipse>());
            var content = Assert.IsType<StackPanel>(card.Content);
            var position = content.TranslatePoint(default, card)!.Value;
            Assert.InRange(position.X, 18, 20);
            Assert.InRange(card.Bounds.Width - position.X - content.Bounds.Width, 18, 20);
        }
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CheckedCardKeepsItsSelectionColorsDuringPointerInteraction(bool dark) => ui.Dispatch(() =>
    {
        using var fixture = new WizardFixture(dark);
        var card = fixture.Choices[0];
        card.IsChecked = true;
        Layout(fixture.Window);
        var surface = card.GetVisualDescendants().OfType<Border>().First();
        void AssertSelectionColors()
        {
            string? Brush(string key) => Assert.IsAssignableFrom<IBrush>(fixture.Window.FindResource(fixture.Window.ActualThemeVariant, key)).ToString();
            Assert.Equal(Brush("AppSuccessCardBackgroundBrush"), surface.Background?.ToString());
            Assert.Equal(Brush("AppAccentBrush"), surface.BorderBrush?.ToString());
        }
        AssertSelectionColors();
        var point = card.TranslatePoint(new Point(10, 10), fixture.Window)!.Value;
        fixture.Window.MouseMove(point, RawInputModifiers.None);
        Layout(fixture.Window);
        AssertSelectionColors();
        fixture.Window.MouseDown(point, MouseButton.Left);
        Layout(fixture.Window);
        AssertSelectionColors();
        fixture.Window.MouseUp(point, MouseButton.Left);
    }, CancellationToken.None);

    [Fact]
    public Task CardKeyboardAndAccessibleSelectionKeepTheTwoGroupsIndependent() => ui.Dispatch(() =>
    {
        using var fixture = new WizardFixture(false);
        var kind = fixture.Choices.Where(card => card.GroupName == "kind").ToArray();
        var purpose = fixture.Choices.Where(card => card.GroupName == "purpose").ToArray();
        Assert.Equal(2, kind.Length);
        Assert.Equal(2, purpose.Length);
        foreach (var card in fixture.Choices)
        {
            var peer = ControlAutomationPeer.CreatePeerForElement(card)!;
            Assert.Equal(AutomationControlType.RadioButton, peer.GetAutomationControlType());
            Assert.False(string.IsNullOrWhiteSpace(peer.GetName()));
        }
        SelectWithKeyboard(kind[0]);
        SelectWithKeyboard(purpose[0]);
        SelectWithKeyboard(kind[1]);
        Assert.False(kind[0].IsChecked);
        Assert.True(kind[1].IsChecked);
        Assert.True(purpose[0].IsChecked);
        Assert.True(fixture.Model.IsIndividual);
        Assert.True(fixture.Model.IsDrawOnly);
        var selection = Assert.IsAssignableFrom<ISelectionItemProvider>(ControlAutomationPeer.CreatePeerForElement(purpose[1]));
        selection.Select();
        Layout(fixture.Window);
        Assert.True(selection.IsSelected);
        Assert.False(purpose[0].IsChecked);
        Assert.True(fixture.Model.IsFullTournament);
        Assert.True(kind[1].IsChecked);

        void SelectWithKeyboard(RadioButton card)
        {
            Assert.True(card.Focus(NavigationMethod.Tab));
            Layout(fixture.Window);
            Assert.True(card.IsFocused);
            var adorners = AdornerLayer.GetAdornerLayer(card)!;
            Assert.Contains(adorners.Children, adorner => ReferenceEquals(adorner.GetValue(AdornerLayer.AdornedElementProperty), card) && adorner.IsEffectivelyVisible);
            fixture.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            fixture.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Layout(fixture.Window);
            Assert.True(card.IsChecked);
        }
    }, CancellationToken.None);

    private static void Layout(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

    private sealed class WizardFixture : IDisposable
    {
        public NewWorkspaceWizardViewModel Model { get; } = new() { Name = "卡片选择测试" };
        public Window Window { get; }
        public RadioButton[] Choices { get; }

        public WizardFixture(bool dark)
        {
            var page = new NewWorkspaceWizardPage { DataContext = Model };
            Window = new Window { Width = 1080, Height = 1000, Content = page,
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
            Window.Show();
            Layout(Window);
            Choices = page.GetVisualDescendants().OfType<RadioButton>().ToArray();
        }

        public void Dispose() => Window.Close();
    }

    public void Dispose() => ui.Dispose();
}
