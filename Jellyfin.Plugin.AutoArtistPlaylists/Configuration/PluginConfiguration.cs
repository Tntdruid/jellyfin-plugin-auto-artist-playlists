using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AutoArtistPlaylists.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets how much of a song (in percent) must be played before it counts as listened.
    /// </summary>
    public int ThresholdPercent { get; set; } = 90;

    /// <summary>
    /// Gets or sets a value indicating whether the song is marked as favorite (heart).
    /// </summary>
    public bool MarkAsFavorite { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the artist playlist itself is marked as favorite (heart).
    /// </summary>
    public bool MarkPlaylistAsFavorite { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a song is removed from its artist playlist when the heart is removed.
    /// </summary>
    public bool RemoveWhenUnfavorited { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the import task also includes played songs, not only hearted ones.
    /// </summary>
    public bool ImportPlayedSongs { get; set; }

    /// <summary>
    /// Gets or sets an optional text put in front of the artist name, e.g. "❤️ ".
    /// </summary>
    public string PlaylistPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the users the plugin applies to. Empty means all users.
    /// </summary>
    public Guid[] EnabledUserIds { get; set; } = [];
}
