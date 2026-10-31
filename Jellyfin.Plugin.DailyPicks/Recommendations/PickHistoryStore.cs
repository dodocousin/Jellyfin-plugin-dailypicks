using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.DailyPicks.Recommendations;

/// <summary>
/// Remembers the picks of each day per audience ("household" or a user id), so picks stay the same all day
/// (also after a restart) and recent picks can be avoided.
/// </summary>
public class PickHistoryStore
{
    private const int KeepDays = 60;

    private readonly object _lock = new();
    private Dictionary<string, Dictionary<string, List<Pick>>>? _days;

    private static string FilePath => Path.Combine(Plugin.Instance!.DataFolderPath, "pick-history.json");

    /// <summary>
    /// Gets the stored picks of a day for an audience.
    /// </summary>
    /// <param name="day">The day.</param>
    /// <param name="audience">"household" or a user id.</param>
    /// <returns>The picks, or null if none were made yet.</returns>
    public List<Pick>? Get(DateTime day, string audience)
    {
        lock (_lock)
        {
            return Load().GetValueOrDefault(Key(day))?.GetValueOrDefault(audience);
        }
    }

    /// <summary>
    /// Stores the picks of a day for an audience.
    /// </summary>
    /// <param name="day">The day.</param>
    /// <param name="audience">"household" or a user id.</param>
    /// <param name="picks">The picks.</param>
    public void Set(DateTime day, string audience, List<Pick> picks)
    {
        lock (_lock)
        {
            var days = Load();
            if (!days.TryGetValue(Key(day), out var audiences))
            {
                days[Key(day)] = audiences = [];
            }

            audiences[audience] = picks;

            var oldest = Key(day.AddDays(-KeepDays));
            foreach (var old in days.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList())
            {
                days.Remove(old);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(days));
        }
    }

    /// <summary>
    /// Gets everything picked for an audience in the days before <paramref name="day"/>.
    /// </summary>
    /// <param name="day">The day.</param>
    /// <param name="audience">"household" or a user id.</param>
    /// <param name="daysBack">How many days to look back.</param>
    /// <returns>The item ids.</returns>
    public HashSet<Guid> Recent(DateTime day, string audience, int daysBack)
    {
        lock (_lock)
        {
            var days = Load();
            return Enumerable.Range(1, Math.Max(0, daysBack))
                .SelectMany(i => days.GetValueOrDefault(Key(day.AddDays(-i)))?.GetValueOrDefault(audience) ?? [])
                .Select(p => p.ItemId)
                .ToHashSet();
        }
    }

    /// <summary>
    /// Forgets today's picks (after a settings change), so they are made again with the new settings.
    /// </summary>
    /// <param name="day">The day.</param>
    public void ClearDay(DateTime day)
    {
        lock (_lock)
        {
            if (Load().Remove(Key(day)))
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_days));
            }
        }
    }

    private static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private Dictionary<string, Dictionary<string, List<Pick>>> Load()
    {
        if (_days is not null)
        {
            return _days;
        }

        try
        {
            _days = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<Pick>>>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (JsonException)
        {
            // A damaged file only costs the repeat-avoidance history.
            _days = [];
        }

        return _days;
    }
}
