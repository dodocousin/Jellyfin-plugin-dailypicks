using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.DailyPicks.Configuration;
using Jellyfin.Plugin.DailyPicks.Recommendations;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DailyPicks;

/// <summary>
/// Makes the picks of the day (household and per user) and keeps the Daily Picks library in sync.
/// </summary>
public class DailyPicksService
{
    private const string Household = "household";

    private readonly JellyfinDataSource _data;
    private readonly PickHistoryStore _store;
    private readonly DailyPicksLibrary _library;
    private readonly LibraryImageService _image;
    private readonly ILogger<DailyPicksService> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly object _pickLock = new();
    private Snapshot? _snapshot;
    private string _pickSettings;

    /// <summary>
    /// Initializes a new instance of the <see cref="DailyPicksService"/> class.
    /// </summary>
    /// <param name="data">Jellyfin data for the taste engine.</param>
    /// <param name="store">Pick history.</param>
    /// <param name="library">Daily Picks library.</param>
    /// <param name="image">Library tile image.</param>
    /// <param name="logger">Logger.</param>
    public DailyPicksService(JellyfinDataSource data, PickHistoryStore store, DailyPicksLibrary library, LibraryImageService image, ILogger<DailyPicksService> logger)
    {
        _data = data;
        _store = store;
        _library = library;
        _image = image;
        _logger = logger;

        // Saving the settings page makes today's picks again with fresh watch history, but only when a setting that
        // affects the picks changed (switching the library image shouldn't reload everyone's history).
        _pickSettings = PickSettings(Config);
        Plugin.Instance!.ConfigurationChanged += async (_, _) =>
        {
            try
            {
                var settings = PickSettings(Config);
                lock (_pickLock)
                {
                    if (settings != _pickSettings)
                    {
                        _pickSettings = settings;
                        _store.ClearDay(DateTime.Now.Date);
                        _snapshot = null;
                    }
                }

                _image.Invalidate();

                await SyncAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily Picks: refresh after settings change failed");
            }
        };
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Gets the household picks (used by the library, so they show on TV apps). Stable for the whole day.
    /// </summary>
    /// <returns>The picks.</returns>
    public IReadOnlyList<Pick> GetHouseholdPicks()
    {
        return GetOrMake(Household, snapshot =>
        {
            // Only people who can open the Daily Picks library decide what is allowed in it.
            var libraryId = _library.GetLibraryId();
            var audience = snapshot.Users
                .Where(u => JellyfinDataSource.CanSeeLibrary(u, libraryId))
                .Select(u => snapshot.Histories[u.Id])
                .ToList();
            var taste = Config.IsSmart ? audience.Where(h => !IsExcluded(h.UserId)).ToList() : [];

            var picks = TasteEngine.ForHousehold(snapshot.Catalog, taste, audience, Options(Household, allowRewatch: false));
            if (picks.Count == 0)
            {
                LogWhyNothing(snapshot, audience);
            }

            return picks;
        });
    }

    /// <summary>
    /// Gets a user's own picks (smart mode), or the household picks (random mode). Stable for the whole day.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The picks.</returns>
    public IReadOnlyList<Pick> GetPicksFor(Guid userId)
    {
        var user = _data.GetUser(userId);
        if (!Config.IsSmart || user is null)
        {
            return GetHouseholdPicks();
        }

        return GetOrMake(userId.ToString("N"), snapshot =>
        {
            // A user created after today's snapshot gets their history loaded on the spot.
            if (!snapshot.Histories.TryGetValue(userId, out var history))
            {
                history = _data.GetHistory(user, snapshot.Catalog);
            }

            var others = snapshot.Histories.Values.Where(h => h.UserId != userId && !IsExcluded(h.UserId)).ToList();
            return TasteEngine.ForUser(snapshot.Catalog, history, others, Options(userId.ToString("N"), Config.AllowRewatch));
        });
    }

    /// <summary>
    /// Gets the id of the Daily Picks library, if it exists.
    /// </summary>
    /// <returns>The library id.</returns>
    public Guid? GetLibraryId() => Config.EnableLibrary ? _library.GetLibraryId() : null;

    /// <summary>
    /// Makes today's picks for everyone (so nobody waits for them on the home screen) and brings the Daily Picks
    /// library up to date, or removes it when disabled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var household = GetHouseholdPicks();
            if (Config.IsSmart && Config.EnableHomeRow)
            {
                foreach (var user in _data.GetUsers())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    GetPicksFor(user.Id);
                }
            }

            if (Config.EnableLibrary)
            {
                var ids = household.Select(p => p.ItemId).ToList();
                await _library.SyncAsync(ids, cancellationToken).ConfigureAwait(false);
                await ApplyImageAsync(ids, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _library.RemoveAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Sets the library tile image again right away (after a picture is uploaded or removed).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RefreshLibraryImageAsync(CancellationToken cancellationToken)
    {
        _image.Invalidate();
        if (Config.EnableLibrary)
        {
            await ApplyImageAsync(GetHouseholdPicks().Select(p => p.ItemId).ToList(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyImageAsync(IReadOnlyList<Guid> picks, CancellationToken cancellationToken)
    {
        // A failed image must never stop the picks themselves.
        try
        {
            await _image.ApplyAsync(_library.GetLibraryId(), picks, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Daily Picks: could not set the library image");
        }
    }

    private static bool IsPickable(BaseItem item)
    {
        // Never our own links, and only movies that are a plain video file (a link can't point at a disc folder).
        return !DailyPicksLibrary.Contains(item)
            && (item is not Video video || (!video.IsShortcut && video.VideoType == VideoType.VideoFile));
    }

    private static string PickSettings(PluginConfiguration c)
    {
        return string.Join(
            "|",
            c.PickMode,
            c.TotalCount,
            c.ForYouCount,
            c.PopularCount,
            c.AvoidRepeatDays,
            c.AllowRewatch,
            c.RewatchAfterDays,
            c.ShuffleSalt,
            string.Join(",", c.ExcludedUserIds.Order()));
    }

    private static bool IsExcluded(Guid userId)
    {
        return Config.ExcludedUserIds.Any(id => Guid.TryParse(id, out var excluded) && excluded == userId);
    }

    private IReadOnlyList<Pick> GetOrMake(string audience, Func<Snapshot, List<Pick>> make)
    {
        lock (_pickLock)
        {
            var today = DateTime.Now.Date;
            // An empty result is never kept, so the next refresh tries again (e.g. after access settings are fixed).
            var picks = _store.Get(today, audience);
            if (picks is null || picks.Count == 0)
            {
                picks = make(GetSnapshot());
                if (picks.Count > 0)
                {
                    _store.Set(today, audience, picks);
                }

                _logger.LogInformation(
                    "Daily Picks: made {Count} picks for {Audience} ({Reasons})",
                    picks.Count,
                    audience,
                    string.Join(", ", picks.GroupBy(p => p.Reason).Select(g => $"{g.Count()} {g.Key}")));
            }

            return picks;
        }
    }

    private Snapshot GetSnapshot()
    {
        // Loading every title and everyone's history is the slow part, so it is done once per day (or settings change)
        // and shared by the household and every user's picks.
        var today = DateTime.Now.Date;
        if (_snapshot is not null && _snapshot.Day == today)
        {
            return _snapshot;
        }

        var timer = Stopwatch.StartNew();
        var catalog = _data.GetCatalog(IsPickable);
        var catalogTime = timer.Elapsed;

        var users = _data.GetUsers();
        var histories = users.ToDictionary(u => u.Id, u => _data.GetHistory(u, catalog));

        _logger.LogInformation(
            "Daily Picks: loaded {Titles} titles in {CatalogSeconds:0.0}s and the history of {Users} users in {HistorySeconds:0.0}s",
            catalog.Count,
            catalogTime.TotalSeconds,
            users.Count,
            (timer.Elapsed - catalogTime).TotalSeconds);

        _snapshot = new Snapshot(today, catalog, users, histories);
        return _snapshot;
    }

    private void LogWhyNothing(Snapshot snapshot, List<UserHistory> audience)
    {
        if (snapshot.Catalog.Count == 0)
        {
            _logger.LogWarning("Daily Picks: no movies or shows found to pick from");
            return;
        }

        if (audience.Count == 0)
        {
            _logger.LogWarning(
                "Daily Picks: no user can see the Daily Picks library (none has access to all libraries), so there is nothing to pick for it. "
                + "Give at least one user access to all libraries, or to the Daily Picks library");
            return;
        }

        var names = snapshot.Users.ToDictionary(u => u.Id, u => u.Username);
        _logger.LogWarning(
            "Daily Picks: no title is visible to every user who sees the Daily Picks library. Titles each user can see: {Counts}",
            string.Join(", ", audience.Select(h => $"{names[h.UserId]}={h.Accessible.Count}")));
    }

    private RecommendOptions Options(string audience, bool allowRewatch)
    {
        var config = Config;
        var today = DateTime.Now.Date;
        var total = Math.Max(0, config.TotalCount);
        var forYou = config.IsSmart ? Math.Clamp(config.ForYouCount, 0, total) : 0;
        var popular = config.IsSmart ? Math.Clamp(config.PopularCount, 0, total - forYou) : 0;

        // Seed from the date and audience only (not string.GetHashCode, which changes per process) so picks survive restarts.
        var audienceSeed = audience.Aggregate(17, (hash, c) => unchecked((hash * 31) + c));
        var seed = unchecked((((today.Year * 10000) + (today.Month * 100) + today.Day) * 31) + audienceSeed + config.ShuffleSalt);

        return new RecommendOptions
        {
            Now = DateTime.UtcNow,
            Seed = seed,
            ForYouCount = forYou,
            PopularCount = popular,
            DiscoveryCount = total - forYou - popular,
            Recent = _store.Recent(today, audience, config.AvoidRepeatDays),
            AllowRewatch = allowRewatch,
            RewatchAfterDays = config.RewatchAfterDays
        };
    }

    private sealed record Snapshot(DateTime Day, List<CatalogItem> Catalog, List<User> Users, Dictionary<Guid, UserHistory> Histories);
}
