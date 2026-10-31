using Jellyfin.Plugin.DailyPicks.Recommendations;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DailyPicks;

/// <summary>
/// Registers the plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<JellyfinDataSource>();
        serviceCollection.AddSingleton<PickHistoryStore>();
        serviceCollection.AddSingleton<DailyPicksLibrary>();
        serviceCollection.AddSingleton<LibraryImageService>();
        serviceCollection.AddSingleton<DailyPicksService>();
        serviceCollection.AddHostedService<WebClientInjector>();
    }
}
