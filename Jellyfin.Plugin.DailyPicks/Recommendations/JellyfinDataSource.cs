using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.DailyPicks.Recommendations;

/// <summary>
/// Reads the catalog, watch history and library access from Jellyfin for the taste engine.
/// </summary>
public class JellyfinDataSource
{
    private const int ActorsPerTitle = 5;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly object _catalogLock = new();
    private DateTime _catalogDay;
    private List<CatalogItem> _catalog = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinDataSource"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    public JellyfinDataSource(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    /// <summary>
    /// Gets the enabled users.
    /// </summary>
    /// <returns>The users.</returns>
    public List<User> GetUsers()
    {
        return _userManager.GetUsers().Where(u => !u.HasPermission(PermissionKind.IsDisabled)).ToList();
    }

    /// <summary>
    /// Checks whether a user can open the Daily Picks library. Users limited to chosen libraries don't get new libraries
    /// automatically, so they only see it if an admin adds it for them.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="libraryId">The Daily Picks library id, if it exists yet.</param>
    /// <returns>True if the user sees the library.</returns>
    public static bool CanSeeLibrary(User user, Guid? libraryId)
    {
        return user.HasPermission(PermissionKind.EnableAllFolders)
            || (libraryId is Guid id && user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Contains(id));
    }

    /// <summary>
    /// Gets a user by id.
    /// </summary>
    /// <param name="id">User id.</param>
    /// <returns>The user, or null.</returns>
    public User? GetUser(Guid id) => _userManager.GetUserById(id);

    /// <summary>
    /// Gets every pickable movie and show with its features. Built once a day.
    /// </summary>
    /// <param name="isPickable">Filter for pickable items (excludes our own links, disc folders...).</param>
    /// <returns>The catalog.</returns>
    public List<CatalogItem> GetCatalog(Func<BaseItem, bool> isPickable)
    {
        lock (_catalogLock)
        {
            if (_catalogDay == DateTime.Now.Date && _catalog.Count > 0)
            {
                return _catalog;
            }

            var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                    Recursive = true,
                    IsVirtualItem = false
                })
                .Where(isPickable)
                .ToList();

            var people = _libraryManager.GetPeopleByItems(items.Select(i => i.Id).ToList());
            _catalog = items.Select(item => new CatalogItem(
                    item.Id,
                    item is MediaBrowser.Controller.Entities.TV.Series,
                    item.Name,
                    Features(item, people.GetValueOrDefault(item.Id)),
                    item.CommunityRating))
                .ToList();
            _catalogDay = DateTime.Now.Date;
            return _catalog;
        }
    }

    /// <summary>
    /// Gets a user's watch history, with show episodes rolled up per show.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="catalog">The catalog (decides what can be picked).</param>
    /// <returns>The history.</returns>
    public UserHistory GetHistory(User user, IReadOnlyList<CatalogItem> catalog)
    {
        var records = new List<WatchRecord>();

        // Movies and shows: anything watched, started or favorited.
        var titles = new Dictionary<Guid, BaseItem>();
        foreach (var filter in new Action<InternalItemsQuery>[] { q => q.IsPlayed = true, q => q.IsResumable = true, q => q.IsFavorite = true })
        {
            var query = new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series], Recursive = true };
            filter(query);
            foreach (var item in _libraryManager.GetItemList(query))
            {
                titles[item.Id] = item;
            }
        }

        var titleData = _userDataManager.GetUserDataBatch(titles.Values.ToList(), user);
        foreach (var (id, item) in titles)
        {
            if (!titleData.TryGetValue(id, out var data))
            {
                continue;
            }

            var progress = item.RunTimeTicks is long runtime && runtime > 0 ? Math.Clamp((double)data.PlaybackPositionTicks / runtime, 0, 1) : 0;
            records.Add(new WatchRecord(id, data.Played && item is not Series, data.PlayCount, data.IsFavorite, data.Rating, data.LastPlayedDate, data.Played ? 0 : progress));
        }

        // Episodes: count the watched ones per show.
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery(user) { IncludeItemTypes = [BaseItemKind.Episode], Recursive = true, IsPlayed = true })
            .OfType<Episode>()
            .ToList();
        var episodeData = _userDataManager.GetUserDataBatch(episodes.Cast<BaseItem>().ToList(), user);
        foreach (var show in episodes.GroupBy(e => e.SeriesId).Where(g => g.Key != Guid.Empty))
        {
            var lastPlayed = show.Select(e => episodeData.GetValueOrDefault(e.Id)?.LastPlayedDate).Max();
            var existing = records.FindIndex(r => r.ItemId == show.Key);
            var record = new WatchRecord(show.Key, true, show.Count(), false, null, lastPlayed, 0);
            if (existing >= 0)
            {
                // Keep the favorite / rating from the show itself.
                record = record with { IsFavorite = records[existing].IsFavorite, Rating = records[existing].Rating };
                records[existing] = record;
            }
            else
            {
                records.Add(record);
            }
        }

        return new UserHistory(user.Id, records, GetAccessible(user, catalog));
    }

    private HashSet<Guid> GetAccessible(User user, IReadOnlyList<CatalogItem> catalog)
    {
        // Library access and parental rating limits both apply. The Daily Picks library itself never counts.
        var topParents = _libraryManager.GetUserRootFolder().GetChildren(user, true)
            .OfType<CollectionFolder>()
            .Where(f => !f.PhysicalLocations.Any(l => l.Contains(DailyPicksLibrary.PicksFolderName, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(f => f.PhysicalFolderIds)
            .ToArray();

        if (topParents.Length == 0)
        {
            return [];
        }

        var allowed = _libraryManager.GetItemIds(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            TopParentIds = topParents
        }).ToHashSet();

        return catalog.Where(c => allowed.Contains(c.Id)).Select(c => c.Id).ToHashSet();
    }

    private static List<string> Features(BaseItem item, IReadOnlyList<PersonInfo>? people)
    {
        var features = new List<string>();
        features.AddRange(item.Genres.Select(g => "g:" + g.ToLowerInvariant()));
        features.AddRange(item.Studios.Take(3).Select(s => "s:" + s.ToLowerInvariant()));
        features.AddRange(item.Tags.Select(t => "t:" + t.ToLowerInvariant()));
        if (item.ProductionYear is int year)
        {
            features.Add("d:" + (year / 10 * 10));
        }

        if (people is not null)
        {
            // Top-billed actors and the directors say the most about a title.
            features.AddRange(people.Where(p => p.Type == PersonKind.Actor).Take(ActorsPerTitle).Select(p => "p:" + p.Name.ToLowerInvariant()));
            features.AddRange(people.Where(p => p.Type == PersonKind.Director).Select(p => "p:" + p.Name.ToLowerInvariant()));
        }

        return features.Distinct().ToList();
    }
}
