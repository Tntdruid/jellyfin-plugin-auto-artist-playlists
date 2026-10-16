using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AutoArtistPlaylists.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoArtistPlaylists;

/// <summary>
/// Listens for playback and handles songs that have been played past the threshold.
/// </summary>
public sealed class PlaybackListener : IHostedService, IDisposable
{
    private static readonly TimeSpan HandledExpiry = TimeSpan.FromHours(6);

    private readonly ISessionManager _sessionManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PlaybackListener> _logger;

    // Remembers which (user, song, play session) have been handled, so one listen is only processed once.
    private readonly ConcurrentDictionary<string, DateTime> _handled = new();

    // Serializes playlist work so two quick events can't create the same playlist twice.
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PlaybackListener(
        ISessionManager sessionManager,
        IPlaylistManager playlistManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        ILogger<PlaybackListener> logger)
    {
        _sessionManager = sessionManager;
        _playlistManager = playlistManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        Handle(e, false, false);
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        Handle(e, e.PlayedToCompletion, true);
    }

    private void Handle(PlaybackProgressEventArgs e, bool playedToCompletion, bool isStop)
    {
        if (e.Item is not Audio song || e.Users is null || e.Users.Count == 0)
        {
            return;
        }

        if (!playedToCompletion && !IsPastThreshold(song, e.PlaybackPositionTicks))
        {
            return;
        }

        foreach (var user in e.Users)
        {
            if (!IsEnabledFor(user))
            {
                continue;
            }

            // Jellyfin's own progress/stop handling saves the whole user data row from an older snapshot,
            // which can wipe a heart set during playback. The stop save is Jellyfin's last write for this
            // playback, so re-check the heart here, even if this listen was already handled.
            if (isStop && Config.MarkAsFavorite)
            {
                try
                {
                    MarkFavorite(user, song);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Auto Artist Playlists: failed to mark {Song} as favorite for {User}", song.Name, user.Username);
                }
            }

            var key =$"{user.Id:N}:{song.Id:N}:{e.PlaySessionId}";
            if (!_handled.TryAdd(key, DateTime.UtcNow))
            {
                continue;
            }

            _ = ProcessAsync(user, song);
        }

        PruneHandled();
    }

    private static bool IsPastThreshold(Audio song, long? positionTicks)
    {
        var runtime = song.RunTimeTicks ?? 0;
        if (runtime <= 0 || positionTicks is null)
        {
            return false;
        }

        var threshold = Math.Clamp(Config.ThresholdPercent, 1, 100) / 100d;
        return positionTicks.Value >= runtime * threshold;
    }

    private static bool IsEnabledFor(User user)
    {
        var ids = Config.EnabledUserIds;
        return ids is null || ids.Length == 0 || ids.Contains(user.Id);
    }

    /// <summary>
    /// Main artist: the first track artist (so "Katy Perry feat. X" lands on Katy Perry),
    /// falling back to the album artist.
    /// </summary>
    private static string? GetMainArtist(Audio song)
    {
        return song.Artists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
            ?? song.AlbumArtists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
    }

    private async Task ProcessAsync(User user, Audio song)
    {
        try
        {
            var config = Config;

            if (config.MarkAsFavorite)
            {
                MarkFavorite(user, song);
            }

            var artist = GetMainArtist(song);
            if (artist is null)
            {
                _logger.LogDebug("Auto Artist Playlists: {Song} has no artist, skipping playlist", song.Name);
                return;
            }

            var playlistName = (config.PlaylistPrefix ?? string.Empty) + artist.Trim();

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await AddToArtistPlaylistAsync(user, song, playlistName).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto Artist Playlists: failed to handle {Song} for {User}", song.Name, user.Username);
        }
    }

    private void MarkFavorite(User user, BaseItem item)
    {
        var data = _userDataManager.GetUserData(user, item);
        if (data is null || data.IsFavorite)
        {
            return;
        }

        data.IsFavorite = true;
        _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
        _logger.LogInformation("Auto Artist Playlists: marked {Item} as favorite for {User}", item.Name, user.Username);
    }

    private async Task AddToArtistPlaylistAsync(User user, Audio song, string playlistName)
    {
        var playlist = await AddSongToPlaylistAsync(user, song, playlistName).ConfigureAwait(false);

        if (playlist is not null && Config.MarkPlaylistAsFavorite)
        {
            MarkFavorite(user, playlist);
        }
    }

    /// <summary>
    /// Adds the song to the user's playlist with the given name, creating it if needed.
    /// </summary>
    /// <returns>The playlist, or null if it could not be looked up after creation.</returns>
    private async Task<BaseItem?> AddSongToPlaylistAsync(User user, Audio song, string playlistName)
    {
        // A playlist deleted outside Jellyfin can linger in the database until the next scan; only use
        // playlists whose folder still exists on disk.
        var playlist = _playlistManager.GetPlaylists(user.Id)
            .FirstOrDefault(p => p.OwnerUserId.Equals(user.Id)
                && string.Equals(p.Name, playlistName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(p.Path)
                && Directory.Exists(p.Path));

        if (playlist is null)
        {
            var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = playlistName,
                ItemIdList = [song.Id],
                MediaType = MediaType.Audio,
                UserId = user.Id,
                Public = false
            }).ConfigureAwait(false);

            _logger.LogInformation("Auto Artist Playlists: created playlist {Playlist} ({PlaylistId}) for {User} with {Song}", playlistName, result.Id, user.Username, song.Name);
            return Guid.TryParse(result.Id, out var newId) ? _libraryManager.GetItemById(newId) : null;
        }

        if (playlist.LinkedChildren.Any(c => c.ItemId.HasValue && c.ItemId.Value.Equals(song.Id)))
        {
            return playlist;
        }

        await _playlistManager.AddItemToPlaylistAsync(playlist.Id, [song.Id], null, user.Id).ConfigureAwait(false);
        _logger.LogInformation("Auto Artist Playlists: added {Song} to {Playlist} ({PlaylistId}, {Path}) for {User}", song.Name, playlistName, playlist.Id.ToString("N"), playlist.Path, user.Username);
        return playlist;
    }

    private void PruneHandled()
    {
        var cutoff = DateTime.UtcNow - HandledExpiry;
        foreach (var entry in _handled)
        {
            if (entry.Value < cutoff)
            {
                _handled.TryRemove(entry.Key, out _);
            }
        }
    }
}
