using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoArtistPlaylists;

/// <summary>
/// Listens for playback and handles songs that have been played past the threshold.
/// </summary>
public sealed class PlaybackListener : IHostedService
{
    private static readonly TimeSpan HandledExpiry = TimeSpan.FromHours(6);

    private readonly ISessionManager _sessionManager;
    private readonly ArtistPlaylistService _service;
    private readonly ILogger<PlaybackListener> _logger;

    // Remembers which (user, song, play session) have been handled, so one listen is only processed once.
    private readonly ConcurrentDictionary<string, DateTime> _handled = new();

    public PlaybackListener(
        ISessionManager sessionManager,
        ArtistPlaylistService service,
        ILogger<PlaybackListener> logger)
    {
        _sessionManager = sessionManager;
        _service = service;
        _logger = logger;
    }

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
            // Respect a heart the user just removed, even if the song keeps playing.
            if (!ArtistPlaylistService.IsEnabledFor(user) || _service.WasRecentlyUnfavorited(user, song))
            {
                continue;
            }

            // Jellyfin's own progress/stop handling saves the whole user data row from an older snapshot,
            // which can wipe a heart set during playback. The stop save is Jellyfin's last write for this
            // playback, so re-check the heart here, even if this listen was already handled.
            if (isStop && ArtistPlaylistService.Config.MarkAsFavorite)
            {
                try
                {
                    _service.MarkFavorite(user, song);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Auto Artist Playlists: failed to mark {Song} as favorite for {User}", song.Name, user.Username);
                }
            }

            var key = $"{user.Id:N}:{song.Id:N}:{e.PlaySessionId}";
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

        var threshold = Math.Clamp(ArtistPlaylistService.Config.ThresholdPercent, 1, 100) / 100d;
        return positionTicks.Value >= runtime * threshold;
    }

    private async Task ProcessAsync(User user, Audio song)
    {
        try
        {
            if (ArtistPlaylistService.Config.MarkAsFavorite)
            {
                _service.MarkFavorite(user, song);
            }

            var artist = ArtistPlaylistService.GetMainArtist(song);
            if (artist is null)
            {
                _logger.LogDebug("Auto Artist Playlists: {Song} has no artist, skipping playlist", song.Name);
                return;
            }

            await _service.AddSongsAsync(user, artist, [song]).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto Artist Playlists: failed to handle {Song} for {User}", song.Name, user.Username);
        }
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
