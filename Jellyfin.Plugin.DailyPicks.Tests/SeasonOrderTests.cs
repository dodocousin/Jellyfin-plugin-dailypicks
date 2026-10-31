using Xunit;

namespace Jellyfin.Plugin.DailyPicks.Tests;

public class SeasonOrderTests
{
    [Fact]
    public void FirstRegularSeasonComesFirstAndSpecialsLast()
    {
        Assert.Equal([1, 2, 5, 0], DailyPicksLibrary.SeasonOrder([5, 0, 2, 1, 2]));
    }

    [Fact]
    public void ShowWithoutSeasonOneStartsAtItsFirstSeason()
    {
        Assert.Equal(3, DailyPicksLibrary.SeasonOrder([4, 3, 0]).First());
    }

    [Fact]
    public void SpecialsOnlyShowStillGetsSpecials()
    {
        Assert.Equal([0], DailyPicksLibrary.SeasonOrder([0]));
    }
}
