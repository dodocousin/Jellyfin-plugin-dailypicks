using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.DailyPicks.Recommendations;

/// <summary>
/// Turns watch history into picks. Pure logic, no Jellyfin types, so it can be tested on its own.
/// </summary>
public static class TasteEngine
{
    /// <summary>Recent plays count more; a play loses half its weight every this many days.</summary>
    private const double HalfLifeDays = 120;

    /// <summary>Only plays this recent count towards "popular in the house".</summary>
    private const int PopularWindowDays = 30;

    private static readonly Dictionary<char, double> _groupWeights = new()
    {
        ['g'] = 1.0, // genre
        ['p'] = 0.8, // actor / director
        ['s'] = 0.4, // studio
        ['t'] = 0.3, // tag
        ['d'] = 0.3  // decade
    };

    /// <summary>
    /// Picks for one user: titles matching their taste, popular with the others, and a few discoveries.
    /// </summary>
    /// <param name="catalog">All pickable titles.</param>
    /// <param name="user">The user.</param>
    /// <param name="others">The other users counted for "popular in the house".</param>
    /// <param name="options">Options.</param>
    /// <returns>The picks, best first.</returns>
    public static List<Pick> ForUser(IReadOnlyList<CatalogItem> catalog, UserHistory user, IReadOnlyList<UserHistory> others, RecommendOptions options)
    {
        var byId = catalog.ToDictionary(i => i.Id);
        var profile = BuildProfile(user.Records, byId, options.Now);
        var excluded = ExcludedFor(user, byId);
        var rewatch = options.AllowRewatch ? RewatchCandidates(user, byId, options) : [];
        var candidates = catalog.Where(i => user.Accessible.Contains(i.Id) && (!excluded.Contains(i.Id) || rewatch.Contains(i.Id))).ToList();
        var popularity = Popularity(others, options.Now);

        return Select(candidates, profile, user.Records, popularity, rewatch, byId, options);
    }

    /// <summary>
    /// Picks shared by the whole house: everyone's taste counts equally, and only titles every user may see are used.
    /// </summary>
    /// <param name="catalog">All pickable titles.</param>
    /// <param name="tasteUsers">Users whose taste counts.</param>
    /// <param name="audience">Everyone who will see the picks (decides what is allowed).</param>
    /// <param name="options">Options.</param>
    /// <returns>The picks, best first.</returns>
    public static List<Pick> ForHousehold(IReadOnlyList<CatalogItem> catalog, IReadOnlyList<UserHistory> tasteUsers, IReadOnlyList<UserHistory> audience, RecommendOptions options)
    {
        var byId = catalog.ToDictionary(i => i.Id);

        // Average the normalized profiles so the heaviest watcher doesn't drown out the others.
        var profiles = tasteUsers.Select(u => BuildProfile(u.Records, byId, options.Now)).Where(p => p.Count > 0).ToList();
        var profile = new Dictionary<string, double>();
        foreach (var (feature, weight) in profiles.SelectMany(p => p))
        {
            profile[feature] = profile.GetValueOrDefault(feature) + (weight / profiles.Count);
        }

        // A title is skipped once most of the house has seen it.
        var seenCount = new Dictionary<Guid, int>();
        foreach (var id in tasteUsers.SelectMany(u => ExcludedFor(u, byId)))
        {
            seenCount[id] = seenCount.GetValueOrDefault(id) + 1;
        }

        var candidates = catalog
            .Where(i => audience.All(u => u.Accessible.Contains(i.Id)))
            .Where(i => seenCount.GetValueOrDefault(i.Id) * 2 <= tasteUsers.Count)
            .ToList();

        var allRecords = tasteUsers.SelectMany(u => u.Records).ToList();
        return Select(candidates, profile, allRecords, Popularity(tasteUsers, options.Now), [], byId, options);
    }

    /// <summary>
    /// How much a record says about taste: positive for liked, negative for dropped.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The weight.</returns>
    public static double SignalWeight(WatchRecord record, DateTime now)
    {
        var daysAgo = record.LastPlayed is DateTime last ? Math.Max(0, (now - last).TotalDays) : HalfLifeDays;

        // Started, gave up early, and didn't come back for two weeks.
        if (!record.Played && !record.IsFavorite && record.Progress is > 0 and < 0.3 && daysAgo > 14)
        {
            return -0.5;
        }

        double played = 0;
        if (record.Played)
        {
            played = 1 + (0.25 * Math.Min(Math.Max(record.PlayCount - 1, 0), 4));
        }
        else if (record.Progress >= 0.3)
        {
            played = 0.5;
        }

        var recency = Math.Max(0.15, Math.Pow(0.5, daysAgo / HalfLifeDays));
        var weight = played * recency;
        if (record.IsFavorite)
        {
            weight += 1.5;
        }

        if (record.Rating is double rating)
        {
            weight += (rating - 5) / 5;
        }

        return weight;
    }

    /// <summary>
    /// Builds a taste profile: a weight per feature, normalized per feature group.
    /// </summary>
    /// <param name="records">Watch records.</param>
    /// <param name="catalog">Catalog by id.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The profile (empty when nothing is known yet).</returns>
    public static Dictionary<string, double> BuildProfile(IEnumerable<WatchRecord> records, IReadOnlyDictionary<Guid, CatalogItem> catalog, DateTime now)
    {
        var raw = new Dictionary<string, double>();
        foreach (var record in records)
        {
            var weight = SignalWeight(record, now);
            if (weight == 0 || !catalog.TryGetValue(record.ItemId, out var item))
            {
                continue;
            }

            foreach (var group in item.Features.GroupBy(f => f[0]))
            {
                // Items with many genres/actors shouldn't count more than items with few.
                var share = weight / Math.Sqrt(group.Count());
                foreach (var feature in group)
                {
                    raw[feature] = raw.GetValueOrDefault(feature) + share;
                }
            }
        }

        // Normalize each group by its positive mass so groups are comparable between users.
        var positiveMass = raw.Where(kv => kv.Value > 0).GroupBy(kv => kv.Key[0]).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
        if (positiveMass.Count == 0)
        {
            return [];
        }

        return raw.Where(kv => positiveMass.ContainsKey(kv.Key[0]))
            .ToDictionary(kv => kv.Key, kv => kv.Value / positiveMass[kv.Key[0]]);
    }

    /// <summary>
    /// How well a title matches a profile.
    /// </summary>
    /// <param name="item">The title.</param>
    /// <param name="profile">The profile.</param>
    /// <returns>The score; above zero means a match.</returns>
    public static double Score(CatalogItem item, IReadOnlyDictionary<string, double> profile)
    {
        double score = 0;
        foreach (var group in item.Features.GroupBy(f => f[0]))
        {
            var sum = group.Sum(f => profile.GetValueOrDefault(f));
            score += _groupWeights.GetValueOrDefault(group.Key, 0.2) * sum / Math.Sqrt(group.Count());
        }

        // A small nudge towards well-rated titles, only to break ties.
        if (score > 0 && item.CommunityRating is float rating)
        {
            score += 0.05 * rating / 10;
        }

        return score;
    }

    private static HashSet<Guid> ExcludedFor(UserHistory user, IReadOnlyDictionary<Guid, CatalogItem> catalog)
    {
        // Seen movies, unfinished movies (Continue Watching covers them) and any show they started (Next Up covers it).
        var excluded = new HashSet<Guid>();
        foreach (var record in user.Records)
        {
            var isSeries = catalog.TryGetValue(record.ItemId, out var item) && item.IsSeries;
            if (record.Played || record.Progress > 0 || (isSeries && record.PlayCount > 0))
            {
                excluded.Add(record.ItemId);
            }
        }

        return excluded;
    }

    private static HashSet<Guid> RewatchCandidates(UserHistory user, IReadOnlyDictionary<Guid, CatalogItem> catalog, RecommendOptions options)
    {
        return user.Records
            .Where(r => r.Played && (r.IsFavorite || r.Rating >= 7))
            .Where(r => r.LastPlayed is DateTime last && (options.Now - last).TotalDays >= options.RewatchAfterDays)
            .Where(r => catalog.TryGetValue(r.ItemId, out var item) && !item.IsSeries)
            .Select(r => r.ItemId)
            .ToHashSet();
    }

    private static Dictionary<Guid, double> Popularity(IEnumerable<UserHistory> users, DateTime now)
    {
        // One point per person who watched it lately, plus a little for repeat plays.
        var popularity = new Dictionary<Guid, double>();
        foreach (var record in users.SelectMany(u => u.Records))
        {
            if ((record.Played || record.PlayCount > 0) && record.LastPlayed is DateTime last && (now - last).TotalDays <= PopularWindowDays)
            {
                popularity[record.ItemId] = popularity.GetValueOrDefault(record.ItemId) + 1 + (0.2 * Math.Min(record.PlayCount, 5));
            }
        }

        return popularity;
    }

    private static List<Pick> Select(
        List<CatalogItem> candidates,
        Dictionary<string, double> profile,
        IReadOnlyList<WatchRecord> sources,
        Dictionary<Guid, double> popularity,
        HashSet<Guid> rewatch,
        IReadOnlyDictionary<Guid, CatalogItem> byId,
        RecommendOptions options)
    {
        var random = new Random(options.Seed);
        var picks = new List<Pick>();
        var taken = new HashSet<Guid>();
        var fresh = candidates.Where(c => !options.Recent.Contains(c.Id)).ToList();

        // Already-seen favorites may only come back as a taste pick, never as "popular" or "discovery".
        var unseen = fresh.Where(c => !rewatch.Contains(c.Id)).ToList();

        // Nothing known about the taste yet: the taste slots go to popular, then discovery.
        var forYouCount = profile.Count > 0 ? options.ForYouCount : 0;
        var popularCount = options.PopularCount + (options.ForYouCount - forYouCount);

        // 1. For you: weighted draw among the best matches, so the top 6 aren't the same every day.
        var scored = fresh
            .Select(c => (Item: c, Score: Score(c, profile) * (rewatch.Contains(c.Id) ? 0.6 : 1)))
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .Take(Math.Max(20, forYouCount * 4))
            .ToList();

        // At most two thirds of one kind (movie/show) when the other kind has candidates.
        var maxPerKind = forYouCount - (forYouCount / 3);
        var usedSources = new Dictionary<Guid, int>();
        foreach (var (item, _) in WeightedDraw(scored, s => s.Score * s.Score, forYouCount, random, maxPerKind))
        {
            var reason = rewatch.Contains(item.Id) ? PickReason.Rewatch : PickReason.ForYou;
            picks.Add(new Pick(item.Id, reason, reason == PickReason.ForYou ? BestSource(item, sources, byId, options.Now, usedSources) : item.Id));
            taken.Add(item.Id);
        }

        // 2. Popular in the house.
        var popular = unseen
            .Where(c => !taken.Contains(c.Id) && popularity.ContainsKey(c.Id))
            .Select(c => (Item: c, Score: popularity[c.Id]))
            .OrderByDescending(p => p.Score)
            .Take(Math.Max(10, popularCount * 3))
            .ToList();
        foreach (var (item, _) in WeightedDraw(popular, p => p.Score, popularCount, random, int.MaxValue))
        {
            picks.Add(new Pick(item.Id, PickReason.Popular));
            taken.Add(item.Id);
        }

        // 3. Discovery fills the rest; well-rated titles first, anything if that runs short.
        var target = options.ForYouCount + options.PopularCount + options.DiscoveryCount;
        var pools = new[]
        {
            unseen.Where(c => c.CommunityRating is null or >= 6),
            unseen,
            candidates.Where(c => !rewatch.Contains(c.Id)) // last resort for small libraries: allow recent picks again
        };

        foreach (var pool in pools)
        {
            var remaining = pool.Where(c => !taken.Contains(c.Id)).OrderBy(c => c.Id).ToList();
            while (picks.Count < target && remaining.Count > 0)
            {
                // Keep movies and shows balanced: take the kind there are fewer of so far, if there is one left.
                var shows = picks.Count(p => byId[p.ItemId].IsSeries);
                var wantShow = shows < picks.Count - shows;
                var kind = remaining.Where(c => c.IsSeries == wantShow).ToList();
                var from = kind.Count > 0 ? kind : remaining;

                var choice = from[random.Next(from.Count)];
                picks.Add(new Pick(choice.Id, PickReason.Discovery));
                taken.Add(choice.Id);
                remaining.Remove(choice);
            }
        }

        return picks;
    }

    private static List<(CatalogItem Item, double Score)> WeightedDraw(
        List<(CatalogItem Item, double Score)> pool,
        Func<(CatalogItem Item, double Score), double> weight,
        int count,
        Random random,
        int maxPerKind)
    {
        var drawn = new List<(CatalogItem Item, double Score)>();
        var left = pool.ToList();
        var goodScore = pool.Count > 0 ? pool.Max(p => p.Score) / 2 : 0;
        while (drawn.Count < count && left.Count > 0)
        {
            var total = left.Sum(weight);
            var roll = random.NextDouble() * total;
            var index = 0;
            for (; index < left.Count - 1; index++)
            {
                roll -= weight(left[index]);
                if (roll <= 0)
                {
                    break;
                }
            }

            var choice = left[index];
            left.RemoveAt(index);
            drawn.Add(choice);

            // Once one kind hits its cap, drop the rest of that kind, but only if the other kind still has a good
            // match (at least half the best score): a movie-only watcher shouldn't get weak show picks for balance.
            var kindCount = drawn.Count(d => d.Item.IsSeries == choice.Item.IsSeries);
            if (kindCount >= maxPerKind && left.Any(l => l.Item.IsSeries != choice.Item.IsSeries && l.Score >= goodScore))
            {
                left.RemoveAll(l => l.Item.IsSeries == choice.Item.IsSeries);
            }
        }

        return drawn;
    }

    private static Guid? BestSource(CatalogItem item, IReadOnlyList<WatchRecord> sources, IReadOnlyDictionary<Guid, CatalogItem> byId, DateTime now, Dictionary<Guid, int> used)
    {
        // The liked title sharing the most with this pick: "Because you watched ...". Similarity decides; how much it was
        // liked only nudges (otherwise one favorite wins every time), and a title already named today counts half each time.
        var features = item.Features.ToHashSet();
        Guid? best = null;
        double bestScore = 0;
        foreach (var record in sources)
        {
            var weight = SignalWeight(record, now);
            if (weight <= 0 || record.ItemId == item.Id || !byId.TryGetValue(record.ItemId, out var source))
            {
                continue;
            }

            var overlap = source.Features.Where(features.Contains).Sum(f => _groupWeights.GetValueOrDefault(f[0], 0.2));
            var score = overlap * (1 + (0.1 * weight)) * Math.Pow(0.5, used.GetValueOrDefault(source.Id));
            if (score > bestScore)
            {
                bestScore = score;
                best = source.Id;
            }
        }

        if (best is Guid chosen)
        {
            used[chosen] = used.GetValueOrDefault(chosen) + 1;
        }

        return best;
    }
}
