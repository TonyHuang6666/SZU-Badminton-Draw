using BadmintonDraw.Core.Scheduling;
using Xunit;

namespace BadmintonDraw.Tests;

public sealed class BuiltInVenueCatalogTests
{
    [Fact]
    public void CatalogOffersTheThreeBookableVenuePresets()
    {
        Assert.Equal(3, BuiltInVenueCatalog.Venues.Count);
        Assert.Equal(["szu-yuehai-east", "szu-lihu-zhikuai", "szu-lihu-zhichang"], BuiltInVenueCatalog.Venues.Select(v => v.Id));
    }

    [Fact]
    public void VenueCourtLabelsFollowRealCourtCountsAndNaturalOrder()
    {
        var venues = BuiltInVenueCatalog.Venues;
        Assert.Equal(["A", "B", "C", "D"], venues[0].Groups.Select(g => g.Name));
        Assert.Equal(32, venues[0].Groups.Sum(g => g.Courts.Count));
        Assert.Equal(["A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8"], venues[0].Groups[0].Courts);
        Assert.Equal(12, venues[1].Groups.Single().Courts.Count);
        Assert.Equal(["1号场", "2号场", "3号场", "4号场", "5号场", "6号场", "7号场", "8号场", "9号场", "10号场", "11号场", "12号场"], venues[1].Groups.Single().Courts);
        Assert.Equal(10, venues[2].Groups.Single().Courts.Count);
    }

    [Fact]
    public void PersistedCourtNamesDistinguishVenuesAndRequireOneRecognizedPreset()
    {
        Assert.Equal("丽湖至快 · 1号场", BuiltInVenueCatalog.Venues[1].QualifiedCourtName("1号场"));
        Assert.Equal("丽湖至畅 · 1号场", BuiltInVenueCatalog.Venues[2].QualifiedCourtName("1号场"));
        Assert.Same(BuiltInVenueCatalog.Venues[0], BuiltInVenueCatalog.FindForCourts(["粤海东馆 · A1", "粤海东馆 · D8"]));
        Assert.Null(BuiltInVenueCatalog.FindForCourts([]));
        Assert.Null(BuiltInVenueCatalog.FindForCourts(["1号场"]));
        Assert.Null(BuiltInVenueCatalog.FindForCourts(["粤海东馆 · A1", "B2"]));
        Assert.Null(BuiltInVenueCatalog.FindForCourts(["丽湖至快 · 1号场", "丽湖至畅 · 1号场"]));
    }

    [Fact]
    public void CatalogCannotBeMutatedThroughItsPublicCollections()
    {
        var venues = Assert.IsAssignableFrom<IList<VenuePreset>>(BuiltInVenueCatalog.Venues);
        Assert.Throws<NotSupportedException>(() => venues.Clear());
        var groups = Assert.IsAssignableFrom<IList<VenueCourtGroup>>(venues[0].Groups);
        Assert.Throws<NotSupportedException>(() => groups.Clear());
        var courts = Assert.IsAssignableFrom<IList<string>>(groups[0].Courts);
        Assert.Throws<NotSupportedException>(() => courts[0] = "坏数据");
    }

    [Fact]
    public void LookupUsesTheSchedulersCaseInsensitiveCourtIdentity()
    {
        Assert.Same(BuiltInVenueCatalog.Venues[0], BuiltInVenueCatalog.FindForCourts(["粤海东馆 · a1", "粤海东馆 · d8"]));
    }
}
