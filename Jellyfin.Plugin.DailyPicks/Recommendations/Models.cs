using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.DailyPicks.Recommendations;

/// <summary>
/// Why a title was picked.
/// </summary>
public enum PickReason
{
    /// <summary>Random pick (random mode).</summary>
    Random,

    /// <summary>Matches the viewer's taste.</summary>
    ForYou,

    /// <summary>Watched a lot by other people in the house lately.</summary>
    Popular,

    /// <summary>Random title, so picks don't get stuck in one genre.</summary>
    Discovery,

    /// <summary>A favorite not watched in a long time.</summary>
    Rewatch
}

/// <summary>
/// A movie or show that can be picked, reduced to what the taste engine needs.
/// </summary>
/// <param name="Id">Item id.</param>
/// <param name="IsSeries">True for shows.</param>
/// <param name="Name">Title.</param>
/// <param name="Features">Group-prefixed features: "g:" genre, "p:" person, "s:" studio, "t:" tag, "d:" decade.</param>
/// <param name="CommunityRating">Community rating (0-10).</param>
public sealed record CatalogItem(Guid Id, bool IsSeries, string Name, IReadOnlyList<string> Features, float? CommunityRating);

/// <summary>
/// What one user did with one title. For shows, the episodes are rolled up into one record.
/// </summary>
/// <param name="ItemId">Movie or series id.</param>
/// <param name="Played">Watched to the end (shows: at least one episode).</param>
/// <param name="PlayCount">Times watched (shows: episodes watched).</param>
/// <param name="IsFavorite">Marked as favorite.</param>
/// <param name="Rating">User rating (0-10), if any.</param>
/// <param name="LastPlayed">Last time it was played.</param>
/// <param name="Progress">How far an unfinished movie got (0-1).</param>
public sealed record WatchRecord(Guid ItemId, bool Played, int PlayCount, bool IsFavorite, double? Rating, DateTime? LastPlayed, double Progress);

/// <summary>
/// A user's watch history and the titles they are allowed to see.
/// </summary>
/// <param name="UserId">User id.</param>
/// <param name="Records">Watch records.</param>
/// <param name="Accessible">Ids of the movies and shows this user can access.</param>
public sealed record UserHistory(Guid UserId, IReadOnlyList<WatchRecord> Records, IReadOnlySet<Guid> Accessible);

/// <summary>
/// One pick of the day.
/// </summary>
/// <param name="ItemId">Item id.</param>
/// <param name="Reason">Why it was picked.</param>
/// <param name="BecauseOfId">For taste picks, the watched title it is most similar to.</param>
public sealed record Pick(Guid ItemId, PickReason Reason, Guid? BecauseOfId = null);

/// <summary>
/// Settings for one recommendation run.
/// </summary>
public sealed class RecommendOptions
{
    /// <summary>Gets the current time (decides recency).</summary>
    public DateTime Now { get; init; }

    /// <summary>Gets the seed for today's random choices.</summary>
    public int Seed { get; init; }

    /// <summary>Gets how many taste picks to make.</summary>
    public int ForYouCount { get; init; }

    /// <summary>Gets how many popular picks to make.</summary>
    public int PopularCount { get; init; }

    /// <summary>Gets how many discovery picks to make.</summary>
    public int DiscoveryCount { get; init; }

    /// <summary>Gets titles picked in the last few days, avoided unless there is nothing else.</summary>
    public IReadOnlySet<Guid> Recent { get; init; } = new HashSet<Guid>();

    /// <summary>Gets a value indicating whether favorites not watched in a long time can come back.</summary>
    public bool AllowRewatch { get; init; }

    /// <summary>Gets how long ago a favorite must have been watched to come back.</summary>
    public int RewatchAfterDays { get; init; } = 365;
}
