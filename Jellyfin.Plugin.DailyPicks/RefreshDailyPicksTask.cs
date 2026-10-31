using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.DailyPicks;

/// <summary>
/// Refreshes the Daily Picks library every day just after midnight, at startup, and every few hours.
/// </summary>
public class RefreshDailyPicksTask : IScheduledTask
{
    private readonly DailyPicksService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshDailyPicksTask"/> class.
    /// </summary>
    /// <param name="service">Daily picks service.</param>
    public RefreshDailyPicksTask(DailyPicksService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public string Name => "Refresh Daily Picks";

    /// <inheritdoc />
    public string Key => "DailyPicksRefresh";

    /// <inheritdoc />
    public string Description => "Picks today's random movies and shows and updates the Daily Picks library.";

    /// <inheritdoc />
    public string Category => "Daily Picks";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _service.SyncAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromMinutes(1).Ticks },
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },

            // Picks don't change during the day; this only retries removing titles that were being watched at midnight.
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(3).Ticks }
        ];
    }
}
