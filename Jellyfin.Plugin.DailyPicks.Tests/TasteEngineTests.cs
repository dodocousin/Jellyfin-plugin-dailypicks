using Jellyfin.Plugin.DailyPicks.Recommendations;
using Xunit;

namespace Jellyfin.Plugin.DailyPicks.Tests;

public class TasteEngineTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    // 20 animated family movies, 20 horror movies, 10 animated shows, 10 crime shows.
    private static readonly List<CatalogItem> Catalog =
    [
        .. Make(20, false, "Animation", "g:animation", "g:family", "s:pixar", "d:2000"),
        .. Make(20, false, "Horror", "g:horror", "g:thriller", "s:blumhouse", "d:2010"),
        .. Make(10, true, "Cartoon", "g:animation", "g:comedy", "d:2010"),
        .. Make(10, true, "Crime", "g:crime", "g:drama", "d:2010")
    ];

    private static readonly HashSet<Guid> Everything = Catalog.Select(c => c.Id).ToHashSet();

    [Fact]
    public void AnimationFanGetsMostlyAnimation()
    {
        var kid = User(Watched(Named("Animation", 0..5), daysAgo: 3));

        var picks = TasteEngine.ForUser(Catalog, kid, [], Options());
        var forYou = picks.Where(p => p.Reason == PickReason.ForYou).ToList();

        Assert.Equal(6, forYou.Count);
        Assert.All(forYou, p => Assert.Contains("g:animation", Item(p).Features));
    }

    [Fact]
    public void DifferentTastesGetDifferentPicks()
    {
        var kid = User(Watched(Named("Animation", 0..5), daysAgo: 3));
        var adult = User(Watched(Named("Horror", 0..5), daysAgo: 3));

        var kidPicks = ForYouIds(TasteEngine.ForUser(Catalog, kid, [adult], Options()));
        var adultPicks = ForYouIds(TasteEngine.ForUser(Catalog, adult, [kid], Options()));

        Assert.Empty(kidPicks.Intersect(adultPicks));
        Assert.All(adultPicks, id => Assert.Contains("g:horror", Catalog.Single(c => c.Id == id).Features));
    }

    [Fact]
    public void FanOfBothMoviesAndShowsGetsBoth()
    {
        var records = Watched(Named("Animation", 0..3), daysAgo: 3);
        records.AddRange(Watched(Named("Cartoon", 0..3), daysAgo: 3));

        var forYou = TasteEngine.ForUser(Catalog, User(records), [], Options()).Where(p => p.Reason == PickReason.ForYou).ToList();

        Assert.InRange(forYou.Count(p => Item(p).IsSeries), 2, 4);
    }

    [Fact]
    public void WatchedMoviesAndStartedShowsAreNotPicked()
    {
        var watched = Named("Animation", 0..18).Concat(Named("Cartoon", 0..1)).ToList();
        var user = User(Watched(watched, daysAgo: 3));

        var picks = TasteEngine.ForUser(Catalog, user, [], Options());

        Assert.DoesNotContain(picks, p => watched.Any(w => w.Id == p.ItemId));
    }

    [Fact]
    public void SameDayGivesSamePicksAndAnotherDayDiffers()
    {
        var user = User(Watched(Named("Animation", 0..5), daysAgo: 3));

        var first = TasteEngine.ForUser(Catalog, user, [], Options(seed: 1)).Select(p => p.ItemId);
        var again = TasteEngine.ForUser(Catalog, user, [], Options(seed: 1)).Select(p => p.ItemId);
        var tomorrow = TasteEngine.ForUser(Catalog, user, [], Options(seed: 2)).Select(p => p.ItemId);

        Assert.Equal(first, again);
        Assert.NotEqual(first, tomorrow);
    }

    [Fact]
    public void NewUserGetsPopularAndDiscoveryUntilTheyWatchSomething()
    {
        var newcomer = User([]);
        var sibling = User(Watched(Named("Cartoon", 0..3), daysAgo: 2));

        var picks = TasteEngine.ForUser(Catalog, newcomer, [sibling], Options());

        Assert.Equal(10, picks.Count);
        Assert.DoesNotContain(picks, p => p.Reason == PickReason.ForYou);
        Assert.Equal(3, picks.Count(p => p.Reason == PickReason.Popular));
    }

    [Fact]
    public void PopularPicksAreWhatOthersWatchedLately()
    {
        var user = User(Watched(Named("Horror", 0..5), daysAgo: 3));
        var lately = User(Watched(Named("Crime", 0..2), daysAgo: 5));
        var longAgo = User(Watched(Named("Crime", 5..7), daysAgo: 200));

        var popular = TasteEngine.ForUser(Catalog, user, [lately, longAgo], Options())
            .Where(p => p.Reason == PickReason.Popular)
            .Select(p => p.ItemId)
            .ToList();

        Assert.Equal(2, popular.Count);
        Assert.All(popular, id => Assert.Contains(id, Named("Crime", 0..2).Select(c => c.Id)));
    }

    [Fact]
    public void ReasonPointsToASimilarWatchedTitle()
    {
        var source = Named("Horror", 0..1).Single();
        var user = User(Watched([source], daysAgo: 1));

        var forYou = TasteEngine.ForUser(Catalog, user, [], Options()).Where(p => p.Reason == PickReason.ForYou);

        Assert.All(forYou, p => Assert.Equal(source.Id, p.BecauseOfId));
    }

    [Fact]
    public void ReasonsNameDifferentTitlesNotJustTheFavorite()
    {
        var watched = Named("Animation", 0..5).ToList();
        var records = Watched(watched, daysAgo: 3);
        records[0] = records[0] with { IsFavorite = true };

        var reasons = TasteEngine.ForUser(Catalog, User(records), [], Options())
            .Where(p => p.Reason == PickReason.ForYou)
            .Select(p => p.BecauseOfId)
            .ToList();

        Assert.True(reasons.Distinct().Count() >= 3, $"only {reasons.Distinct().Count()} different titles named");
    }

    [Fact]
    public void DroppedTitlesCountAgainstTheirGenre()
    {
        // Loved one horror movie long ago, but quit five horror movies early this month.
        var records = new List<WatchRecord> { new(Named("Horror", 0..1).Single().Id, true, 1, false, null, Now.AddDays(-400), 0) };
        records.AddRange(Named("Horror", 1..6).Select(c => new WatchRecord(c.Id, false, 0, false, null, Now.AddDays(-20), 0.1)));
        records.AddRange(Watched(Named("Animation", 0..1), daysAgo: 30));
        var profile = TasteEngine.BuildProfile(records, Catalog.ToDictionary(c => c.Id), Now);

        var horror = TasteEngine.Score(Named("Horror", 10..11).Single(), profile);
        var animation = TasteEngine.Score(Named("Animation", 10..11).Single(), profile);

        Assert.True(horror < animation, $"horror {horror} should score below animation {animation}");
    }

    [Fact]
    public void HouseholdOnlyUsesTitlesEveryoneMaySee()
    {
        var kidAllowed = Named("Animation", 0..20).Concat(Named("Cartoon", 0..10)).Select(c => c.Id).ToHashSet();
        var kid = new UserHistory(Guid.NewGuid(), Watched(Named("Animation", 0..3), daysAgo: 2), kidAllowed);
        var adult = User(Watched(Named("Horror", 0..10), daysAgo: 2));

        var picks = TasteEngine.ForHousehold(Catalog, [kid, adult], [kid, adult], Options());

        Assert.Equal(10, picks.Count);
        Assert.All(picks, p => Assert.Contains(p.ItemId, kidAllowed));
    }

    [Fact]
    public void HouseholdSkipsWhatMostOfTheHouseHasSeen()
    {
        var seenByBoth = Named("Animation", 0..10).ToList();
        var a = User(Watched(seenByBoth, daysAgo: 2));
        var b = User(Watched(seenByBoth, daysAgo: 2));

        var picks = TasteEngine.ForHousehold(Catalog, [a, b], [a, b], Options());

        Assert.DoesNotContain(picks, p => seenByBoth.Any(s => s.Id == p.ItemId));
    }

    [Fact]
    public void RecentPicksAreAvoided()
    {
        var user = User(Watched(Named("Animation", 0..5), daysAgo: 3));
        var yesterday = TasteEngine.ForUser(Catalog, user, [], Options(seed: 1)).Select(p => p.ItemId).ToHashSet();

        var today = TasteEngine.ForUser(Catalog, user, [], Options(seed: 2, recent: yesterday));

        Assert.DoesNotContain(today, p => yesterday.Contains(p.ItemId));
    }

    [Fact]
    public void RandomModeBalancesMoviesAndShows()
    {
        var picks = TasteEngine.ForHousehold(Catalog, [], [User([])], Options(forYou: 0, popular: 0, discovery: 10));

        Assert.Equal(10, picks.Count);
        Assert.Equal(5, picks.Count(p => Item(p).IsSeries));
    }

    [Fact]
    public void FavoritesComeBackOnlyWhenRewatchIsOn()
    {
        var favorite = Named("Animation", 0..1).Single();
        var records = new List<WatchRecord> { new(favorite.Id, true, 3, true, null, Now.AddDays(-500), 0) };
        records.AddRange(Watched(Named("Animation", 1..19), daysAgo: 3));
        var user = User(records);

        var off = TasteEngine.ForUser(Catalog, user, [], Options());
        var on = TasteEngine.ForUser(Catalog, user, [], Options(rewatch: true));

        Assert.DoesNotContain(off, p => p.ItemId == favorite.Id);
        Assert.Contains(on, p => p.ItemId == favorite.Id && p.Reason == PickReason.Rewatch);
    }

    private static IEnumerable<CatalogItem> Make(int count, bool isSeries, string name, params string[] features)
    {
        return Enumerable.Range(0, count).Select(i => new CatalogItem(Guid.NewGuid(), isSeries, $"{name} {i}", features, 7f));
    }

    private static IEnumerable<CatalogItem> Named(string name, Range range)
    {
        var all = Catalog.Where(c => c.Name.StartsWith(name + " ", StringComparison.Ordinal)).ToList();
        return all.Take(range);
    }

    private static List<WatchRecord> Watched(IEnumerable<CatalogItem> items, int daysAgo)
    {
        return items.Select(i => new WatchRecord(i.Id, true, 1, false, null, Now.AddDays(-daysAgo), 0)).ToList();
    }

    private static UserHistory User(List<WatchRecord> records) => new(Guid.NewGuid(), records, Everything);

    private static CatalogItem Item(Pick pick) => Catalog.Single(c => c.Id == pick.ItemId);

    private static List<Guid> ForYouIds(List<Pick> picks) => picks.Where(p => p.Reason == PickReason.ForYou).Select(p => p.ItemId).ToList();

    private static RecommendOptions Options(int seed = 1, IReadOnlySet<Guid>? recent = null, bool rewatch = false, int forYou = 6, int popular = 2, int discovery = 2)
    {
        return new RecommendOptions
        {
            Now = Now,
            Seed = seed,
            ForYouCount = forYou,
            PopularCount = popular,
            DiscoveryCount = discovery,
            Recent = recent ?? new HashSet<Guid>(),
            AllowRewatch = rewatch,
            RewatchAfterDays = 365
        };
    }
}
