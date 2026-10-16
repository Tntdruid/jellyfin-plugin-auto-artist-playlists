using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.SmartPlayList.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SmartPlayList;

/// <summary>
/// Adds songs you have listened to (beyond a threshold) to a private playlist per artist and marks them as favorite.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Smart Play List";

    public override Guid Id => Guid.Parse("4ada4936-7154-478d-80f8-99d18faaebc2");

    public override string Description => "Adds songs you have listened to into a private playlist per artist and marks them with a heart.";

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
