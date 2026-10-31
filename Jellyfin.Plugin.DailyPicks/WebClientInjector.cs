using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DailyPicks;

/// <summary>
/// Adds the Daily Picks script tag to the web client's index.html at startup.
/// </summary>
public class WebClientInjector : IHostedService
{
    private const string ScriptTag = "<script plugin=\"DailyPicks\" defer src=\"../DailyPicks/ClientScript\"></script>";

    private readonly IServerApplicationPaths _paths;
    private readonly ILogger<WebClientInjector> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebClientInjector"/> class.
    /// </summary>
    /// <param name="paths">Server paths.</param>
    /// <param name="logger">Logger.</param>
    public WebClientInjector(IServerApplicationPaths paths, ILogger<WebClientInjector> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(_paths.WebPath, "index.html");
        try
        {
            if (!File.Exists(indexPath))
            {
                _logger.LogWarning("Daily Picks: web client not found at {Path}, home row not installed", indexPath);
                return Task.CompletedTask;
            }

            var html = File.ReadAllText(indexPath);
            if (html.Contains("plugin=\"DailyPicks\"", StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyEnd < 0)
            {
                _logger.LogWarning("Daily Picks: unexpected index.html layout, home row not installed");
                return Task.CompletedTask;
            }

            File.WriteAllText(indexPath, html.Insert(bodyEnd, ScriptTag));
            _logger.LogInformation("Daily Picks: home row script added to {Path}", indexPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(
                ex,
                "Daily Picks: cannot write {Path}. Give the Jellyfin service write access to that file, or add {Tag} before </body> yourself",
                indexPath,
                ScriptTag);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
