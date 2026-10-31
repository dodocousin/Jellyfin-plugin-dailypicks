using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DailyPicks;

/// <summary>
/// Keeps the "Daily Picks" library: links to the picked movies and episodes, with NFO files and artwork
/// copied from the originals so nothing has to be downloaded.
/// </summary>
/// <remarks>
/// Jellyfin 12 refuses .strm files that point to local files, so the picks are hard links (no extra disk space,
/// play like the original). Hard links only work within one drive, so each drive holding picked media gets its own
/// "Jellyfin Daily Picks" folder at its root, and the library has one location per drive.
/// </remarks>
public class DailyPicksLibrary
{
    /// <summary>
    /// Name of the folder created at the root of each media drive.
    /// </summary>
    public const string PicksFolderName = "Jellyfin Daily Picks";

    private static readonly string[] _metadataTypes = ["Movie", "Series", "Season", "Episode"];
    private static readonly string[] _subtitleExtensions = [".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup", ".smi"];

    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<DailyPicksLibrary> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DailyPicksLibrary"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="fileSystem">File system.</param>
    /// <param name="logger">Logger.</param>
    public DailyPicksLibrary(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<DailyPicksLibrary> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Gets the id of the Daily Picks library, if it exists.
    /// </summary>
    /// <returns>The library id.</returns>
    public Guid? GetLibraryId()
    {
        var folder = FindVirtualFolder();
        return folder is not null && Guid.TryParse(folder.ItemId, out var id) ? id : null;
    }

    /// <summary>
    /// Checks whether an item lives inside a Daily Picks folder (so it is never picked itself).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True if the item is one of our links.</returns>
    public static bool Contains(BaseItem item)
    {
        var marker = Path.DirectorySeparatorChar + PicksFolderName + Path.DirectorySeparatorChar;
        return item.Path?.Contains(marker, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Writes today's picks, creates the library if needed and scans it.
    /// </summary>
    /// <param name="picks">Today's picks.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SyncAsync(IReadOnlyList<Guid> picks, CancellationToken cancellationToken)
    {
        var library = FindVirtualFolder();
        // GetVirtualFolders returns new objects on every call, so compare by id, and never count our own folders.
        var otherLocations = _libraryManager.GetVirtualFolders()
            .Where(f => library is null || !string.Equals(f.ItemId, library.ItemId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => f.Locations)
            .Select(NormalizePath)
            .Where(l => !IsPicksFolder(l))
            .ToList();

        // Work out which drive folder each pick goes into.
        var wanted = new Dictionary<string, Dictionary<string, BaseItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in picks.Select(id => _libraryManager.GetItemById(id)))
        {
            var mediaPath = item switch
            {
                Movie movie => movie.Path,
                Series series => GetEpisodes(series).FirstOrDefault()?.Path,
                _ => null
            };

            if (item is null || string.IsNullOrEmpty(mediaPath))
            {
                continue;
            }

            var root = PicksRootFor(mediaPath);
            if (otherLocations.Any(l => IsSameOrInside(root, l)))
            {
                _logger.LogWarning("Daily Picks: {Root} is inside another library, skipping {Name} so it isn't listed twice", root, item.Name);
                continue;
            }

            if (!wanted.TryGetValue(root, out var folders))
            {
                wanted[root] = folders = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
            }

            folders[ItemFolderName(item)] = item;
        }

        // Remove yesterday's picks from every drive folder, keep the ones still picked today.
        var roots = wanted.Keys.Concat(library?.Locations ?? []).Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(IsPicksFolder).Where(Directory.Exists))
        {
            var keep = wanted.TryGetValue(root, out var folders) ? folders : [];
            foreach (var dir in Directory.GetDirectories(root).Where(d => !keep.ContainsKey(Path.GetFileName(d))))
            {
                RemovePick(dir);
            }
        }

        foreach (var (root, folders) in wanted)
        {
            Directory.CreateDirectory(root);
            foreach (var (name, item) in folders)
            {
                var dir = Path.Combine(root, name);
                if (Directory.Exists(dir))
                {
                    continue;
                }

                try
                {
                    var written = item is Movie movie ? WriteMovie(movie, dir) : WriteSeries((Series)item, dir);
                    if (!written)
                    {
                        _logger.LogWarning("Daily Picks: could not link the files of {Name}, skipping it", item.Name);
                        TryDelete(dir);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Daily Picks: could not write {Name}", item.Name);
                    TryDelete(dir);
                }
            }
        }

        if (library is null)
        {
            if (wanted.Count > 0)
            {
                await CreateLibraryAsync(wanted.Keys).ConfigureAwait(false);
                _logger.LogInformation("Daily Picks: created library {Name} in {Roots}", Plugin.Instance!.Configuration.Title, string.Join(", ", wanted.Keys));
            }

            return;
        }

        foreach (var root in wanted.Keys.Where(r => !library.Locations.Any(l => string.Equals(NormalizePath(l), r, StringComparison.OrdinalIgnoreCase))))
        {
            _libraryManager.AddMediaPath(library.Name, new MediaPathInfo(root));
        }

        await ScanAsync(library, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Daily Picks: library refreshed with {Count} picks", wanted.Values.Sum(f => f.Count));
    }

    /// <summary>
    /// Removes the library and its folders.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task RemoveAsync()
    {
        var folder = FindVirtualFolder();
        if (folder is null)
        {
            return;
        }

        await _libraryManager.RemoveVirtualFolder(folder.Name, true).ConfigureAwait(false);
        foreach (var location in folder.Locations.Select(NormalizePath).Where(IsPicksFolder))
        {
            TryDelete(location);
        }

        _logger.LogInformation("Daily Picks: library disabled, removed {Name}", folder.Name);
    }

    private VirtualFolderInfo? FindVirtualFolder()
    {
        return _libraryManager.GetVirtualFolders().FirstOrDefault(f => f.Locations.Any(l => IsPicksFolder(NormalizePath(l))));
    }

    private Task CreateLibraryAsync(IEnumerable<string> roots)
    {
        // Everything comes from our NFO files and copied artwork, so no internet lookups and no extra extraction work.
        var options = new LibraryOptions
        {
            PathInfos = roots.Select(r => new MediaPathInfo(r)).ToArray(),
            EnableRealtimeMonitor = false,
            SaveLocalMetadata = false,
            EnableChapterImageExtraction = false,
            ExtractChapterImagesDuringLibraryScan = false,
            EnableTrickplayImageExtraction = false,
            ExtractTrickplayImagesDuringLibraryScan = false,
            EnableEmbeddedTitles = false,
            AutomaticRefreshIntervalDays = 0,
            TypeOptions = _metadataTypes.Select(t => new TypeOptions { Type = t, MetadataFetchers = [], ImageFetchers = [] }).ToArray()
        };

        return _libraryManager.AddVirtualFolder(Plugin.Instance!.Configuration.Title, CollectionTypeOptions.mixed, options, true);
    }

    private async Task ScanAsync(VirtualFolderInfo folder, CancellationToken cancellationToken)
    {
        // A library's items live under one physical folder per location (scanning the library item itself does nothing).
        // A location added today has no physical folder yet, which only a full scan creates.
        var physicalFolders = Guid.TryParse(folder.ItemId, out var id) && _libraryManager.GetItemById(id) is CollectionFolder library
            ? library.GetPhysicalFolders().ToList()
            : [];

        var allIndexed = folder.Locations.All(l => physicalFolders.Any(p => string.Equals(NormalizePath(p.Path), NormalizePath(l), StringComparison.OrdinalIgnoreCase)));
        if (!allIndexed)
        {
            _logger.LogInformation("Daily Picks: new library location, running a full library scan");
            await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var physical in physicalFolders)
        {
            var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem));
            await physical.ValidateChildren(new Progress<double>(), refreshOptions, true, false, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool WriteMovie(Movie movie, string dir)
    {
        Directory.CreateDirectory(dir);
        var baseName = Path.Combine(dir, Path.GetFileName(dir));
        if (!LinkMedia(movie.Path, baseName))
        {
            return false;
        }

        var nfo = new XElement("movie", CommonNfo(movie));
        AddIfSet(nfo, "tagline", movie.Tagline);
        AddIfSet(nfo, "runtime", movie.RunTimeTicks is long ticks ? ((int)TimeSpan.FromTicks(ticks).TotalMinutes).ToString(CultureInfo.InvariantCulture) : null);
        nfo.Save(baseName + ".nfo");

        CopyImage(movie, ImageType.Primary, Path.Combine(dir, "poster"));
        CopyImage(movie, ImageType.Backdrop, Path.Combine(dir, "fanart"));
        CopyImage(movie, ImageType.Logo, Path.Combine(dir, "logo"));
        return true;
    }

    private bool WriteSeries(Series series, string dir)
    {
        Directory.CreateDirectory(dir);
        var linked = 0;

        foreach (var season in GetEpisodes(series).GroupBy(e => e.ParentIndexNumber!.Value))
        {
            var seasonDir = Path.Combine(dir, string.Format(CultureInfo.InvariantCulture, "Season {0:00}", season.Key));
            Directory.CreateDirectory(seasonDir);

            var seasonItem = season.First().Season;
            if (seasonItem is not null)
            {
                CopyImage(seasonItem, ImageType.Primary, Path.Combine(seasonDir, "folder"));
            }

            foreach (var episode in season)
            {
                var name = string.Format(CultureInfo.InvariantCulture, "S{0:00}E{1:00}", episode.ParentIndexNumber, episode.IndexNumber);
                if (episode.IndexNumberEnd is int end && end > episode.IndexNumber)
                {
                    name += string.Format(CultureInfo.InvariantCulture, "-E{0:00}", end);
                }

                var baseName = Path.Combine(seasonDir, name);
                if (!LinkMedia(episode.Path, baseName))
                {
                    continue;
                }

                var nfo = new XElement(
                    "episodedetails",
                    new XElement("title", episode.Name),
                    new XElement("season", episode.ParentIndexNumber),
                    new XElement("episode", episode.IndexNumber));
                AddIfSet(nfo, "episodenumberend", episode.IndexNumberEnd?.ToString(CultureInfo.InvariantCulture));
                AddIfSet(nfo, "plot", episode.Overview);
                AddIfSet(nfo, "aired", episode.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                nfo.Save(baseName + ".nfo");

                CopyImage(episode, ImageType.Primary, baseName + "-thumb");
                linked++;
            }
        }

        if (linked == 0)
        {
            return false;
        }

        new XElement("tvshow", CommonNfo(series)).Save(Path.Combine(dir, "tvshow.nfo"));
        CopyImage(series, ImageType.Primary, Path.Combine(dir, "poster"));
        CopyImage(series, ImageType.Backdrop, Path.Combine(dir, "fanart"));
        CopyImage(series, ImageType.Logo, Path.Combine(dir, "logo"));
        return true;
    }

    private List<Episode> GetEpisodes(Series series)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [series.Id],
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsVirtualItem = false
            })
            .OfType<Episode>()
            .Where(e => e.ParentIndexNumber.HasValue && e.IndexNumber.HasValue && !e.IsShortcut && e.VideoType == VideoType.VideoFile && File.Exists(e.Path))
            .OrderBy(e => e.ParentIndexNumber)
            .ThenBy(e => e.IndexNumber)
            .ToList();
    }

    /// <summary>
    /// Links a media file and its external subtitles (e.g. "Movie.fr.srt") next to <paramref name="baseName"/>.
    /// </summary>
    private static bool LinkMedia(string mediaPath, string baseName)
    {
        if (!TryLink(mediaPath, baseName + Path.GetExtension(mediaPath)))
        {
            return false;
        }

        var sourceDir = Path.GetDirectoryName(mediaPath)!;
        var sourceBase = Path.GetFileNameWithoutExtension(mediaPath);
        foreach (var subtitle in Directory.EnumerateFiles(sourceDir, sourceBase + ".*"))
        {
            if (_subtitleExtensions.Contains(Path.GetExtension(subtitle), StringComparer.OrdinalIgnoreCase))
            {
                TryLink(subtitle, baseName + Path.GetFileName(subtitle)[sourceBase.Length..]);
            }
        }

        return true;
    }

    private static bool TryLink(string target, string link)
    {
        // Hard link first: needs no special rights, but only works on the same drive. Symbolic link as a fallback.
        if (OperatingSystem.IsWindows() ? CreateHardLink(link, target, IntPtr.Zero) : CreateUnixHardLink(target, link) == 0)
        {
            return true;
        }

        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateUnixHardLink(string oldpath, string newpath);

    private static List<XElement> CommonNfo(BaseItem item)
    {
        var nfo = new XElement("root", new XElement("title", item.Name));
        AddIfSet(nfo, "originaltitle", item.OriginalTitle);
        AddIfSet(nfo, "year", item.ProductionYear?.ToString(CultureInfo.InvariantCulture));
        AddIfSet(nfo, "premiered", item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        AddIfSet(nfo, "plot", item.Overview);
        AddIfSet(nfo, "mpaa", item.OfficialRating);
        AddIfSet(nfo, "rating", item.CommunityRating?.ToString(CultureInfo.InvariantCulture));

        foreach (var genre in item.Genres)
        {
            nfo.Add(new XElement("genre", genre));
        }

        foreach (var studio in item.Studios)
        {
            nfo.Add(new XElement("studio", studio));
        }

        foreach (var (provider, value) in item.ProviderIds)
        {
            if (!string.IsNullOrEmpty(value))
            {
                nfo.Add(new XElement("uniqueid", new XAttribute("type", provider.ToLowerInvariant()), value));
            }
        }

        return nfo.Elements().ToList();
    }

    private static void AddIfSet(XElement parent, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parent.Add(new XElement(name, value));
        }
    }

    private static void CopyImage(BaseItem item, ImageType type, string targetWithoutExtension)
    {
        var source = item.GetImagePath(type, 0);
        if (!string.IsNullOrEmpty(source) && File.Exists(source))
        {
            File.Copy(source, targetWithoutExtension + Path.GetExtension(source), true);
        }
    }

    private static string ItemFolderName(BaseItem item)
    {
        // The id suffix keeps two titles with the same name apart; the NFO gives Jellyfin the real title.
        var name = item.ProductionYear is int year ? $"{item.Name} ({year})" : item.Name;
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return $"{safe} [{item.Id.ToString("N")[..8]}]";
    }

    private static string PicksRootFor(string mediaPath)
    {
        var full = Path.GetFullPath(mediaPath);
        return NormalizePath(Path.Combine(Path.GetPathRoot(full)!, PicksFolderName));
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // Only ever delete inside folders we own.
    private static bool IsPicksFolder(string path) => string.Equals(Path.GetFileName(path), PicksFolderName, StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrInside(string path, string folder)
    {
        return string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void RemovePick(string dir)
    {
        // Video links first: if one is open (someone is watching it), leave the whole title intact
        // instead of deleting its posters and NFO, and try again at the next refresh.
        var sidecars = new[] { ".nfo", ".jpg", ".jpeg", ".png", ".webp" }.Concat(_subtitleExtensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videos = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => !sidecars.Contains(Path.GetExtension(f)));
        try
        {
            foreach (var video in videos)
            {
                File.Delete(video);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation("Daily Picks: {Name} is in use (probably being watched), it will be removed at the next refresh", Path.GetFileName(dir));
            return;
        }

        TryDelete(dir);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Daily Picks: could not delete {Path}", path);
        }
    }
}
