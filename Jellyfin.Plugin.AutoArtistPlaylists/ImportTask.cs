using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoArtistPlaylists;

/// <summary>
/// One-off import: builds artist playlists from songs the user already has hearted (and optionally played).
/// </summary>
public class ImportTask : IScheduledTask
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ArtistPlaylistService _service;
    private readonly ILogger<ImportTask> _logger;

    public ImportTask(
        IUserManager userManager,
        ILibraryManager libraryManager,
        ArtistPlaylistService service,
        ILogger<ImportTask> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _service = service;
        _logger = logger;
    }

    public string Name => "Import existing songs";

    public string Key => "AutoArtistPlaylistsImport";

    public string Description => "Adds songs you have already hearted (and, if enabled in the plugin settings, songs you have played) to the artist playlists.";

    public string Category => "Auto Artist Playlists";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Run by hand from Scheduled Tasks.
        return [];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = ArtistPlaylistService.Config;
        var users = _userManager.GetUsers().Where(ArtistPlaylistService.IsEnabledFor).ToList();

        for (var u = 0; u < users.Count; u++)
        {
            var user = users[u];
            var songs = GetSongs(user, true);
            if (config.ImportPlayedSongs)
            {
                songs = songs.Concat(GetSongs(user, false)).DistinctBy(s => s.Id).ToList();
            }

            var byArtist = songs
                .Select(s => (Song: s, Artist: ArtistPlaylistService.GetMainArtist(s)))
                .Where(x => x.Artist is not null)
                .GroupBy(x => x.Artist!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.LogInformation(
                "Auto Artist Playlists: importing {Count} song(s) by {Artists} artist(s) for {User}",
                songs.Count,
                byArtist.Count,
                user.Username);

            var added = 0;
            for (var a = 0; a < byArtist.Count; a++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var group = byArtist[a].Select(x => x.Song).ToList();
                try
                {
                    if (config.MarkAsFavorite)
                    {
                        foreach (var song in group)
                        {
                            _service.MarkFavorite(user, song);
                        }
                    }

                    added += await _service.AddSongsAsync(user, byArtist[a].Key, group).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Auto Artist Playlists: import failed for {Artist} for {User}", byArtist[a].Key, user.Username);
                }

                progress.Report(100d * (u + ((a + 1d) / byArtist.Count)) / users.Count);
            }

            _logger.LogInformation("Auto Artist Playlists: import for {User} done, {Added} song(s) added", user.Username, added);
            progress.Report(100d * (u + 1) / users.Count);
        }

        progress.Report(100);
    }

    private List<Audio> GetSongs(Jellyfin.Database.Implementations.Entities.User user, bool favorites)
    {
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true
        };

        if (favorites)
        {
            query.IsFavorite = true;
        }
        else
        {
            query.IsPlayed = true;
        }

        return _libraryManager.GetItemList(query).OfType<Audio>().ToList();
    }
}
