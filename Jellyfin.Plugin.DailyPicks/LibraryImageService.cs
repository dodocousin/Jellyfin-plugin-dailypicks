using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DailyPicks;

/// <summary>
/// Sets the Daily Picks library tile image (shown in "My Media" on TV apps): a collage of today's posters,
/// or a picture uploaded in the plugin settings.
/// </summary>
public class LibraryImageService
{
    /// <summary>Image types accepted for the custom picture, by file extension.</summary>
    public static readonly IReadOnlyDictionary<string, string> MimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp"
    };

    private const string CustomImageName = "library-image";

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IImageEncoder _imageEncoder;
    private readonly ILogger<LibraryImageService> _logger;
    private string? _appliedKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryImageService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="imageEncoder">Image encoder (builds the collage).</param>
    /// <param name="logger">Logger.</param>
    public LibraryImageService(ILibraryManager libraryManager, IProviderManager providerManager, IImageEncoder imageEncoder, ILogger<LibraryImageService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _imageEncoder = imageEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Gets the uploaded picture, if there is one.
    /// </summary>
    public static string? CustomImagePath => Directory.Exists(Plugin.Instance!.DataFolderPath)
        ? Directory.GetFiles(Plugin.Instance.DataFolderPath, CustomImageName + ".*").FirstOrDefault()
        : null;

    /// <summary>
    /// Stores an uploaded picture, replacing the previous one.
    /// </summary>
    /// <param name="bytes">The image.</param>
    /// <param name="extension">File extension (.png, .jpg or .webp).</param>
    public static void SaveCustomImage(byte[] bytes, string extension)
    {
        DeleteCustomImage();
        Directory.CreateDirectory(Plugin.Instance!.DataFolderPath);
        File.WriteAllBytes(Path.Combine(Plugin.Instance.DataFolderPath, CustomImageName + extension), bytes);
    }

    /// <summary>
    /// Removes the uploaded picture.
    /// </summary>
    public static void DeleteCustomImage()
    {
        if (CustomImagePath is string path)
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Sets the library image if it changed: a new day's collage, another mode, or a new picture.
    /// </summary>
    /// <param name="libraryId">The Daily Picks library id.</param>
    /// <param name="picks">Today's household picks (the library content).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ApplyAsync(Guid? libraryId, IReadOnlyList<Guid> picks, CancellationToken cancellationToken)
    {
        if (libraryId is not Guid id || _libraryManager.GetItemById(id) is not Folder library)
        {
            return;
        }

        var config = Plugin.Instance!.Configuration;
        var custom = CustomImagePath;
        var useCustom = config.UseCustomLibraryImage && custom is not null;

        // Re-applied daily even when nothing changed: Jellyfin replaces library images it thinks are stale.
        var key = useCustom
            ? $"custom|{File.GetLastWriteTimeUtc(custom!).Ticks}|{DateTime.Now:yyyyMMdd}"
            : $"collage|{config.Title}|{string.Join(",", picks)}";
        if (key == _appliedKey)
        {
            return;
        }

        string source;
        if (useCustom)
        {
            source = custom!;
        }
        else
        {
            var posters = picks
                .Select(p => _libraryManager.GetItemById(p)?.GetImagePath(ImageType.Primary, 0))
                .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                .Select(p => p!)
                .ToList();
            if (posters.Count == 0)
            {
                return;
            }

            // JPEG: a photo collage is ~5x smaller than as PNG, so TVs load the tile faster.
            source = Path.Combine(Plugin.Instance.DataFolderPath, "library-collage.jpg");
            Directory.CreateDirectory(Plugin.Instance.DataFolderPath);
            if (!TryBuildCollage(posters, config.Title, source))
            {
                return;
            }
        }

        var stream = File.OpenRead(source);
        await using (stream.ConfigureAwait(false))
        {
            await _providerManager.SaveImage(library, stream, MimeTypes[Path.GetExtension(source)], ImageType.Primary, null, cancellationToken).ConfigureAwait(false);
        }

        await library.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        _appliedKey = key;
        _logger.LogInformation("Daily Picks: library image set ({Kind})", useCustom ? "your picture" : "collage of today's picks");
    }

    private bool TryBuildCollage(List<string> posters, string title, string outputPath)
    {
        // Our own poster grid needs the server's SkiaSharp; if a future Jellyfin doesn't ship it,
        // fall back to Jellyfin's own (single picture) library tile.
        try
        {
            CollageBuilder.Build(posters, title, outputPath);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException)
        {
            _logger.LogWarning(ex, "Daily Picks: couldn't draw the poster collage, using Jellyfin's own library image style instead");
        }

        if (!_imageEncoder.SupportsImageCollageCreation)
        {
            _logger.LogWarning("Daily Picks: this server can't build image collages, the library keeps Jellyfin's own image");
            return false;
        }

        _imageEncoder.CreateImageCollage(new ImageCollageOptions { InputPaths = posters, OutputPath = outputPath, Width = 960, Height = 540 }, title);
        return true;
    }

    /// <summary>
    /// Makes the next <see cref="ApplyAsync"/> set the image again (after a new upload or a settings change).
    /// </summary>
    public void Invalidate() => _appliedKey = null;
}
