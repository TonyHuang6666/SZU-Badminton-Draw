using BadmintonDraw.Desktop.ViewModels;
using Xunit;

namespace BadmintonDraw.Desktop.Tests;

public sealed class VenueCourtSelectionModelTests
{
    [Fact]
    public void NewDayRequiresExplicitVenueAndCourtChoice()
    {
        var vm = new VenueCourtSelectionViewModel([]);
        Assert.Equal(-1, vm.VenueIndex);
        Assert.False(vm.CanAccept);
        vm.VenueIndex = 0;
        Assert.True(vm.IsPreset);
        Assert.False(vm.IsCustom);
        Assert.All(vm.Groups.SelectMany(g => g.Courts), c => Assert.False(c.IsSelected));
        Assert.False(vm.TryCreateSelection(out _));
        vm.Groups[0].Courts[0].IsSelected = true;
        Assert.True(vm.TryCreateSelection(out var courts));
        Assert.Equal(["粤海东馆 · A1"], courts);
    }

    [Fact]
    public void PresetPrefillAndDraftSwitchingNeverMutateInput()
    {
        var input = new[] { "粤海东馆 · A2", "粤海东馆 · D8" };
        var vm = new VenueCourtSelectionViewModel(input);
        Assert.Equal(0, vm.VenueIndex);
        Assert.True(vm.Groups[0].Courts[1].IsSelected);
        vm.Groups[0].ClearCommand.Execute(null);
        vm.VenueIndex = 1;
        vm.Groups[0].Courts[11].IsSelected = true;
        vm.VenueIndex = 0;
        Assert.False(vm.Groups[0].Courts[1].IsSelected);
        Assert.True(vm.Groups[3].Courts[7].IsSelected);
        vm.VenueIndex = 1;
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(["丽湖至快 · 12号场"], selected);
        Assert.Equal(["粤海东馆 · A2", "粤海东馆 · D8"], input);
    }

    [Fact]
    public void GroupAndVenueBatchActionsUseNaturalOrder()
    {
        var vm = new VenueCourtSelectionViewModel([]) { VenueIndex = 0 };
        vm.Groups[2].SelectAllCommand.Execute(null);
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(8, selected.Count);
        Assert.Equal("粤海东馆 · C1", selected[0]);
        Assert.Equal("粤海东馆 · C8", selected[7]);
        vm.SelectAllCommand.Execute(null);
        Assert.True(vm.TryCreateSelection(out selected));
        Assert.Equal(32, selected.Count);
        vm.ClearCommand.Execute(null);
        Assert.False(vm.CanAccept);
        vm.VenueIndex = 1;
        vm.SelectAllCommand.Execute(null);
        Assert.True(vm.TryCreateSelection(out selected));
        Assert.Equal("丽湖至快 · 9号场", selected[8]);
        Assert.Equal("丽湖至快 · 10号场", selected[9]);
    }

    [Fact]
    public void CustomExistingNamesRestoreOnlyACommonQualifiedPrefix()
    {
        var vm = new VenueCourtSelectionViewModel(["社区馆 · 甲场", "社区馆 · 乙场"]);
        Assert.Equal(3, vm.VenueIndex);
        Assert.Equal("社区馆", vm.CustomVenueName);
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(["社区馆 · 甲场", "社区馆 · 乙场"], selected);
        vm.VenueIndex = 0;
        vm.VenueIndex = 3;
        Assert.True(vm.TryCreateSelection(out selected));
        Assert.Equal(["社区馆 · 甲场", "社区馆 · 乙场"], selected);
        var unknown = new VenueCourtSelectionViewModel(["B1", "1号场", "中央场"]);
        Assert.Equal("", unknown.CustomVenueName);
        Assert.True(unknown.TryCreateSelection(out selected));
        Assert.Equal(["B1", "1号场", "中央场"], selected);
    }

    [Fact]
    public void CustomParserAcceptsChineseAndEnglishSeparators()
    {
        var vm = new VenueCourtSelectionViewModel([]) { VenueIndex = 3, CustomVenueName = "城北馆", CustomCourtsText = "A1, A2，A3;A4；A5\nA6" };
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(["城北馆 · A1", "城北馆 · A2", "城北馆 · A3", "城北馆 · A4", "城北馆 · A5", "城北馆 · A6"], selected);
    }

    [Theory]
    [InlineData("A1,a1")]
    [InlineData(" ")]
    public void InvalidCustomCourtListsCannotBeAccepted(string text)
    {
        var vm = new VenueCourtSelectionViewModel([]) { VenueIndex = 3, CustomCourtsText = text };
        Assert.False(vm.TryCreateSelection(out _));
        Assert.False(vm.CanAccept);
        Assert.NotEmpty(vm.ValidationMessage);
    }

    [Theory]
    [InlineData("馆,名称")]
    [InlineData("馆，名称")]
    [InlineData("馆;名称")]
    [InlineData("馆；名称")]
    [InlineData("馆\n名称")]
    public void CustomVenueNamesCannotContainCourtSeparators(string name)
    {
        var vm = new VenueCourtSelectionViewModel([]) { VenueIndex = 3, CustomVenueName = name, CustomCourtsText = "1号场" };
        Assert.False(vm.CanAccept);
        Assert.NotEmpty(vm.ValidationMessage);
    }

    [Fact]
    public void NumberedGenerationReplacesExistingTextAndKeepsNaturalOrder()
    {
        var vm = new VenueCourtSelectionViewModel([]) { VenueIndex = 3, CustomVenueName = "社区馆" };
        Assert.Equal("8", vm.NumberedCourtCountText);
        Assert.Contains("生成", vm.GenerateNumberedCourtsLabel);
        vm.CustomCourtsText = "旧场";
        Assert.Contains("替换", vm.GenerateNumberedCourtsLabel);
        vm.NumberedCourtCountText = "12";
        vm.GenerateNumberedCourtsCommand.Execute(null);
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(12, selected.Count);
        Assert.Equal("社区馆 · 9号场", selected[8]);
        Assert.Equal("社区馆 · 10号场", selected[9]);
        Assert.DoesNotContain("旧场", vm.CustomCourtsText);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("257")]
    [InlineData("八")]
    public void InvalidNumberedCountPreservesTheCurrentDraft(string count)
    {
        var vm = new VenueCourtSelectionViewModel([]) { VenueIndex = 3, CustomCourtsText = "甲场", NumberedCourtCountText = count };
        vm.GenerateNumberedCourtsCommand.Execute(null);
        Assert.Equal("甲场", vm.CustomCourtsText);
        Assert.Contains("256", vm.ValidationMessage);
    }

    [Fact]
    public void NumberedGenerationSupportsTheUpperBoundary()
    {
        var vm = new VenueCourtSelectionViewModel([]) { VenueIndex = 3, NumberedCourtCountText = "256" };
        vm.GenerateNumberedCourtsCommand.Execute(null);
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(256, selected.Count);
        Assert.Equal("256号场", selected[^1]);
    }

    [Fact]
    public void RemovingUnavailableCourtsRequiresFreshConfirmationAfterEachChange()
    {
        var original = new[] { "粤海东馆 · A1", "粤海东馆 · A2" };
        var restricted = new[] { "粤海东馆 · A2" };
        var vm = new VenueCourtSelectionViewModel(original, [new("午间封场", restricted), new("全天维护", [])]);
        vm.Groups[0].Courts[1].IsSelected = false;
        Assert.True(vm.HasAffectedWindows);
        Assert.False(vm.CanAccept);
        Assert.Contains("午间封场", vm.AffectedWindowsMessage);
        Assert.Contains("粤海东馆 · A2", vm.AffectedWindowsMessage);
        Assert.Contains("全天维护", vm.AffectedWindowsMessage);
        vm.ConfirmAdjustUnavailable = true;
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(["粤海东馆 · A1"], selected);
        vm.Groups[0].Courts[2].IsSelected = true;
        Assert.False(vm.ConfirmAdjustUnavailable);
        Assert.False(vm.CanAccept);
        Assert.Equal(["粤海东馆 · A1", "粤海东馆 · A2"], original);
        Assert.Equal(["粤海东馆 · A2"], restricted);
    }

    [Fact]
    public void AllCourtWindowsRemainApplicableWithoutRequiringConfirmation()
    {
        var vm = new VenueCourtSelectionViewModel([], [new("全场休息", [])]) { VenueIndex = 2 };
        vm.Groups[0].Courts[0].IsSelected = true;
        Assert.False(vm.HasAffectedWindows);
        Assert.True(vm.CanAccept);
        Assert.Contains("全场休息", vm.AffectedWindowsMessage);
    }

    [Fact]
    public void SelectionChangesPublishValidationAndSummaryNotifications()
    {
        var vm = new VenueCourtSelectionViewModel([]) { VenueIndex = 2 };
        var properties = new List<string?>();
        vm.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        vm.Groups[0].Courts[0].IsSelected = true;
        Assert.Contains(nameof(vm.CanAccept), properties);
        Assert.Contains(nameof(vm.SelectionSummary), properties);
        Assert.Contains(nameof(vm.ValidationMessage), properties);
        Assert.NotEmpty(vm.SelectionSummary);
    }

    [Fact]
    public void InvalidOriginalNamesRemainVisibleUntilUserCorrectsThem()
    {
        var vm = new VenueCourtSelectionViewModel(["A1", "a1", "", "旧场"]);
        Assert.Contains("A1", vm.CustomCourtsText);
        Assert.Contains("a1", vm.CustomCourtsText);
        Assert.Contains("旧场", vm.CustomCourtsText);
        Assert.False(vm.CanAccept);
    }

    [Fact]
    public void RepeatedSeparatorsAndTheFinalNewlineDoNotCreatePhantomCourts()
    {
        var vm = new VenueCourtSelectionViewModel([]) { VenueIndex = 3, CustomVenueName = "社区馆", CustomCourtsText = "A1,,A2；；\nA3\n" };
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(["社区馆 · A1", "社区馆 · A2", "社区馆 · A3"], selected);
    }

    [Fact]
    public void PresetCaseDifferencesDoNotRemoveUnavailableCourtReferences()
    {
        var vm = new VenueCourtSelectionViewModel(["粤海东馆 · a1"], [new("晨间维护", ["粤海东馆 · a1"]) ]);
        Assert.Equal(0, vm.VenueIndex);
        Assert.True(vm.Groups[0].Courts[0].IsSelected);
        Assert.False(vm.HasAffectedWindows);
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(["粤海东馆 · A1"], selected);
    }

    [Fact]
    public void SelectionSummaryStaysCompactWhenEveryCourtIsSelected()
    {
        var vm = new VenueCourtSelectionViewModel([]) { VenueIndex = 0 };
        vm.SelectAllCommand.Execute(null);
        Assert.Equal("已选 32 / 32 片", vm.SelectionSummary);
        vm.VenueIndex = 3;
        vm.CustomCourtsText = "1号场,2号场";
        Assert.Equal("共 2 片场地", vm.SelectionSummary);
    }

    [Fact]
    public void InvalidGenerationCountIsExplainedEvenBeforeAnyCustomCourtsExist()
    {
        var vm = new VenueCourtSelectionViewModel([]) { VenueIndex = 3, NumberedCourtCountText = "0" };
        vm.GenerateNumberedCourtsCommand.Execute(null);
        Assert.Contains("256", vm.ValidationMessage);
        Assert.Empty(vm.CustomCourtsText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UneditedCustomCourtNamesPreserveTheirExactOriginalWhitespace(bool switchVenue)
    {
        var vm = new VenueCourtSelectionViewModel(["社区馆  · A1", "社区馆  · A2 "]);
        if (switchVenue)
        {
            vm.VenueIndex = 0;
            vm.Groups[0].Courts[0].IsSelected = true;
            vm.VenueIndex = 3;
        }
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(["社区馆  · A1", "社区馆  · A2 "], selected);
    }

    [Fact]
    public void OriginalCustomCourtSnapshotKeepsUnavailableReferencesAndIgnoresCallerMutation()
    {
        var original = new[] { "社区馆  · A1", "社区馆  · A2" };
        var vm = new VenueCourtSelectionViewModel(original, [new("维护", ["社区馆  · A1"]) ]);
        original[0] = "调用方后来改写";
        Assert.False(vm.HasAffectedWindows);
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(["社区馆  · A1", "社区馆  · A2"], selected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitCustomFieldEditsReplaceTheOriginalNameSnapshot(bool editVenueName)
    {
        var original = new[] { "社区馆  · A1", "社区馆  · A2" };
        var vm = new VenueCourtSelectionViewModel(original);
        if (editVenueName) vm.CustomVenueName = "新馆";
        else vm.CustomCourtsText = "B1\nB2";
        Assert.True(vm.TryCreateSelection(out var selected));
        Assert.Equal(editVenueName ? ["新馆 · A1", "新馆 · A2"] : new[] { "社区馆 · B1", "社区馆 · B2" }, selected);
        Assert.Equal(["社区馆  · A1", "社区馆  · A2"], original);
    }
}
