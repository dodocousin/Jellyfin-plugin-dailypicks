using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DailyPicks.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the title shown above the home row and used as the library name.
    /// </summary>
    public string Title { get; set; } = "Daily Picks";

    /// <summary>
    /// Gets or sets how many titles are picked each day, split between movies and shows.
    /// </summary>
    public int TotalCount { get; set; } = 10;

    /// <summary>
    /// Gets or sets how titles are picked: "Smart" (based on what users watch) or "Random".
    /// </summary>
    public string PickMode { get; set; } = "Smart";

    /// <summary>
    /// Gets or sets how many of the picks match the viewer's taste (smart mode).
    /// </summary>
    public int ForYouCount { get; set; } = 6;

    /// <summary>
    /// Gets or sets how many of the picks are popular in the house (smart mode). The rest are discoveries.
    /// </summary>
    public int PopularCount { get; set; } = 2;

    /// <summary>
    /// Gets or sets a value indicating whether the web row shows why each title was picked.
    /// </summary>
    public bool ShowReasons { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether favorites not watched in a long time can be picked again.
    /// </summary>
    public bool AllowRewatch { get; set; }

    /// <summary>
    /// Gets or sets how long ago a favorite must have been watched before it can come back.
    /// </summary>
    public int RewatchAfterDays { get; set; } = 365;

    /// <summary>
    /// Gets or sets users whose viewing doesn't shape the household picks or "popular" (e.g. a guest account).
    /// </summary>
    public string[] ExcludedUserIds { get; set; } = [];

    /// <summary>
    /// Gets a value indicating whether smart mode is on.
    /// </summary>
    public bool IsSmart => string.Equals(PickMode, "Smart", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets how many previous days are avoided so the same title doesn't come back right away.
    /// </summary>
    public int AvoidRepeatDays { get; set; } = 7;

    /// <summary>
    /// Gets or sets a value indicating whether the picks are kept in a library, so native TV apps
    /// (Android TV, Fire TV, Roku, Apple TV) show them as a "Recently added" home row.
    /// </summary>
    public bool EnableLibrary { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the home row is injected into the web client (browser, Jellyfin Media Player, webOS, Tizen).
    /// </summary>
    public bool EnableHomeRow { get; set; } = true;

    /// <summary>
    /// Gets or sets a number mixed into the daily seed. Change it to reshuffle today's picks.
    /// </summary>
    public int ShuffleSalt { get; set; }
}
