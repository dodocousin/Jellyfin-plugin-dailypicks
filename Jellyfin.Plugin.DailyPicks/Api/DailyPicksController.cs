using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.DailyPicks.Recommendations;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DailyPicks.Api;

/// <summary>
/// Endpoints used by the web client home row.
/// </summary>
[ApiController]
[Route("DailyPicks")]
public class DailyPicksController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";

    // 10 MB picture, base64-encoded.
    private const int MaxImageBase64Length = 10 * 1024 * 1024 * 4 / 3;

    private readonly DailyPicksService _service;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DailyPicksController"/> class.
    /// </summary>
    /// <param name="service">Daily picks service.</param>
    /// <param name="libraryManager">Library manager.</param>
    public DailyPicksController(DailyPicksService service, ILibraryManager libraryManager)
    {
        _service = service;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets today's picks for the signed-in user. The client fetches the items itself so the user's permissions apply.
    /// </summary>
    /// <returns>The row title and picks.</returns>
    [HttpGet("Today")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<TodaysPicksDto> GetToday()
    {
        var config = Plugin.Instance!.Configuration;
        var result = new TodaysPicksDto
        {
            Enabled = config.EnableHomeRow,
            Title = config.Title,
            LibraryId = _service.GetLibraryId()?.ToString("N")
        };

        if (!config.EnableHomeRow)
        {
            return result;
        }

        var picks = Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out var userId)
            ? _service.GetPicksFor(userId)
            : _service.GetHouseholdPicks();

        result.Items = picks
            .Select(p => new PickDto { Id = p.ItemId.ToString("N"), Reason = config.ShowReasons && config.IsSmart ? ReasonText(p) : null })
            .ToArray();
        return result;
    }

    /// <summary>
    /// Uploads the picture used for the Daily Picks library tile. The body is the image, base64-encoded
    /// (like Jellyfin's own image upload).
    /// </summary>
    /// <returns>No content, or bad request if the body isn't a PNG, JPEG or WebP image.</returns>
    [HttpPost("LibraryImage")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> UploadLibraryImage()
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (body.Length > MaxImageBase64Length)
        {
            return BadRequest("The picture must be smaller than 10 MB.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(body);
        }
        catch (FormatException)
        {
            return BadRequest("The picture must be sent base64-encoded.");
        }

        // Decide the type from the file's first bytes, not from what the browser claims.
        var extension = bytes switch
        {
            [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
            [0xFF, 0xD8, 0xFF, ..] => ".jpg",
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ".webp",
            _ => null
        };
        if (extension is null)
        {
            return BadRequest("Use a PNG, JPEG or WebP picture.");
        }

        LibraryImageService.SaveCustomImage(bytes, extension);
        await _service.RefreshLibraryImageAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Removes the uploaded picture; the library tile goes back to the collage of today's picks.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpDelete("LibraryImage")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteLibraryImage()
    {
        LibraryImageService.DeleteCustomImage();
        await _service.RefreshLibraryImageAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Tells the settings page whether a picture has been uploaded.
    /// </summary>
    /// <returns>Whether a custom picture exists.</returns>
    [HttpGet("LibraryImage/Info")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LibraryImageInfoDto> GetLibraryImageInfo()
    {
        return new LibraryImageInfoDto { HasCustomImage = LibraryImageService.CustomImagePath is not null, LibraryId = _service.GetLibraryId()?.ToString("N") };
    }

    /// <summary>
    /// Gets the script that draws the home row in the web client.
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.DailyPicks.Web.dailyPicks.js");
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, "application/javascript");
    }

    private string? ReasonText(Pick pick)
    {
        return pick.Reason switch
        {
            PickReason.ForYou when pick.BecauseOfId is Guid source && _libraryManager.GetItemById(source) is { } item => $"Because you watched {item.Name}",
            PickReason.ForYou => "Picked for you",
            PickReason.Popular => "Popular in the house",
            PickReason.Discovery => "Something different",
            PickReason.Rewatch => "Worth another watch",
            _ => null
        };
    }
}

/// <summary>
/// Today's picks.
/// </summary>
public class TodaysPicksDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the home row is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the row title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the picks, best first.
    /// </summary>
    public PickDto[] Items { get; set; } = [];

    /// <summary>
    /// Gets or sets the id of the Daily Picks library, whose own "Recently added" row the web client hides.
    /// </summary>
    public string? LibraryId { get; set; }
}

/// <summary>
/// Library image state for the settings page.
/// </summary>
public class LibraryImageInfoDto
{
    /// <summary>
    /// Gets or sets a value indicating whether a picture has been uploaded.
    /// </summary>
    public bool HasCustomImage { get; set; }

    /// <summary>
    /// Gets or sets the Daily Picks library id, to preview its current image.
    /// </summary>
    public string? LibraryId { get; set; }
}

/// <summary>
/// One pick.
/// </summary>
public class PickDto
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets why it was picked, shown under the poster (null to show the year instead).
    /// </summary>
    public string? Reason { get; set; }
}
