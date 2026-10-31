using System;
using System.Linq;
using System.Reflection;
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
