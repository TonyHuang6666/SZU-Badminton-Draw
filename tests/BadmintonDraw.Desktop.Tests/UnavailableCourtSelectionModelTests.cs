using BadmintonDraw.Desktop.ViewModels;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class UnavailableCourtSelectionModelTests
{
    [Fact]
    public void EmptySelectionExplicitlyRepresentsAllAvailableCourts()
    {
        var vm = new UnavailableCourtSelectionViewModel(["馆 · 1号场", "馆 · 2号场"], []);
        Assert.True(vm.AllCourts);
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Empty(selected);
        vm.AllCourts = false;
        Assert.False(vm.CanAccept);
        Assert.False(vm.TryCreateSelection(out _));
        vm.Courts[0].IsSelected = true;
        Assert.True(vm.TryCreateSelection(out selected));
        Assert.Equal(["馆 · 1号场"], selected);
        Assert.Equal("1号场", vm.Courts[0].Label);
    }

    [Fact]
    public void PrefillIsIsolatedAndUsesAvailableCourtOrder()
    {
        var original = new[] { "馆 · 2号场", "馆 · 1号场" };
        var vm = new UnavailableCourtSelectionViewModel(["馆 · 1号场", "馆 · 2号场"], original);
        Assert.False(vm.AllCourts);
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(["馆 · 1号场", "馆 · 2号场"], selected);
        vm.Courts[0].IsSelected = false;
        Assert.Equal(["馆 · 2号场", "馆 · 1号场"], original);
    }

    [Fact]
    public void UnknownOldReferencesRequireAnExplicitCorrection()
    {
        var vm = new UnavailableCourtSelectionViewModel(["新馆 · 1号场", "新馆 · 2号场"], ["旧馆 · 1号场", "新馆 · 1号场"]);
        Assert.False(vm.CanAccept);
        Assert.Contains("旧馆 · 1号场", vm.ValidationMessage);
        Assert.False(vm.TryCreateSelection(out _));
        vm.Courts[1].IsSelected = true;
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(["新馆 · 1号场", "新馆 · 2号场"], selected);
    }

    [Fact]
    public void ChoosingAllExplicitlyCorrectsUnknownOldReferences()
    {
        var vm = new UnavailableCourtSelectionViewModel(["新馆 · 1号场"], ["旧馆 · 1号场"]);
        vm.AllCourts = true;
        Assert.True(vm.CanAccept);
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Empty(selected);
        Assert.NotEmpty(vm.SelectionSummary);
    }

    [Fact]
    public void PrefillUsesTheSameCaseInsensitiveIdentityAsScheduling()
    {
        var vm = new UnavailableCourtSelectionViewModel(["粤海东馆 · A1"], ["粤海东馆 · a1"]);
        Assert.True(vm.Courts[0].IsSelected);
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(["粤海东馆 · A1"], selected);
    }
}
