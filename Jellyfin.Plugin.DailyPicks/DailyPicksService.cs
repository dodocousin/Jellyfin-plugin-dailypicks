using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly ILogger<DailyPicksService> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly object _pickLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DailyPicksService"/> class.
    /// </summary>
    /// <param name="data">Jellyfin data for the taste engine.</param>
    /// <param name="store">Pick history.</param>
    /// <param name="library">Daily Picks library.</param>
    /// <param name="logger">Logger.</param>
    public DailyPicksService(JellyfinDataSource data, PickHistoryStore store, DailyPicksLibrary library, ILogger<DailyPicksService> logger)
    {
        _data = data;
        _store = store;
        _library = library;
        _logger = logger;

        // Saving the settings page makes today's picks again with the new settings.
        Plugin.Instance!.ConfigurationChanged += async (_, _) =>
        {
            try
            {
                _store.ClearDay(DateTime.Now.Date);
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
        return GetOrMake(Household, () =>
        {
            var config = Config;
            var catalog = _data.GetCatalog(IsPickable);
            var audience = _data.GetUsers().Select(u => _data.GetHistory(u, catalog)).ToList();
            var taste = config.IsSmart ? audience.Where(h => !IsExcluded(h.UserId)).ToList() : [];
            return TasteEngine.ForHousehold(catalog, taste, audience, Options(Household, allowRewatch: false));
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

        return GetOrMake(userId.ToString("N"), () =>
        {
            var catalog = _data.GetCatalog(IsPickable);
            var history = _data.GetHistory(user, catalog);
            var others = _data.GetUsers()
                .Where(u => u.Id != userId && !IsExcluded(u.Id))
                .Select(u => _data.GetHistory(u, catalog))
                .ToList();
            return TasteEngine.ForUser(catalog, history, others, Options(userId.ToString("N"), Config.AllowRewatch));
        });
    }

    /// <summary>
    /// Gets the id of the Daily Picks library, if it exists.
    /// </summary>
    /// <returns>The library id.</returns>
    public Guid? GetLibraryId() => Config.EnableLibrary ? _library.GetLibraryId() : null;

    /// <summary>
    /// Brings the Daily Picks library up to date with today's household picks, or removes it when disabled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Config.EnableLibrary)
            {
                await _library.SyncAsync(GetHouseholdPicks().Select(p => p.ItemId).ToList(), cancellationToken).ConfigureAwait(false);
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

    private static bool IsPickable(BaseItem item)
    {
        // Never our own links, and only movies that are a plain video file (a link can't point at a disc folder).
        return !DailyPicksLibrary.Contains(item)
            && (item is not Video video || (!video.IsShortcut && video.VideoType == VideoType.VideoFile));
    }

    private static bool IsExcluded(Guid userId)
    {
        return Config.ExcludedUserIds.Any(id => Guid.TryParse(id, out var excluded) && excluded == userId);
    }

    private IReadOnlyList<Pick> GetOrMake(string audience, Func<List<Pick>> make)
    {
        lock (_pickLock)
        {
            var today = DateTime.Now.Date;
            var picks = _store.Get(today, audience);
            if (picks is null)
            {
                picks = make();
                _store.Set(today, audience, picks);
                _logger.LogInformation(
                    "Daily Picks: made {Count} picks for {Audience} ({Reasons})",
                    picks.Count,
                    audience,
                    string.Join(", ", picks.GroupBy(p => p.Reason).Select(g => $"{g.Count()} {g.Key}")));
            }

            return picks;
        }
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
}
