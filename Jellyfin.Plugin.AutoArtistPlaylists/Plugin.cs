using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.AutoArtistPlaylists.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AutoArtistPlaylists;

/// <summary>
/// Adds songs you have listened to (beyond a threshold) to a private playlist per artist and marks them as favorite.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        MigrateOldConfiguration(applicationPaths);
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Auto Artist Playlists";

    public override Guid Id => Guid.Parse("4ada4936-7154-478d-80f8-99d18faaebc2");

    public override string Description => "Adds songs you have listened to into a private playlist per artist and marks them with a heart.";

    /// <summary>
    /// Versions before 1.1 were called "Smart Play List" and stored settings under the old assembly name.
    /// Configuration is loaded lazily, so copying the file here is picked up on first access.
    /// </summary>
    private static void MigrateOldConfiguration(IApplicationPaths applicationPaths)
    {
        try
        {
            var folder = applicationPaths.PluginConfigurationsPath;
            var oldPath = Path.Combine(folder, "Jellyfin.Plugin.SmartPlayList.xml");
            var newPath = Path.Combine(folder, "Jellyfin.Plugin.AutoArtistPlaylists.xml");
            if (File.Exists(oldPath) && !File.Exists(newPath))
            {
                File.Copy(oldPath, newPath);
            }
        }
        catch (IOException)
        {
            // Not worth failing plugin load over; defaults are used instead.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
