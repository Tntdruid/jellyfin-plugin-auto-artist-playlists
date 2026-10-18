using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AutoArtistPlaylists;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ArtistPlaylistService>();
        serviceCollection.AddHostedService<PlaybackListener>();
        serviceCollection.AddHostedService<UnfavoriteListener>();
    }
}
