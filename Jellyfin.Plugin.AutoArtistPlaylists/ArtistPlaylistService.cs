using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoArtistPlaylists;

/// <summary>
/// Shared logic for hearts and artist playlists, used by playback, import and unfavorite handling.
/// </summary>
public sealed class ArtistPlaylistService : IDisposable
{
    private static readonly TimeSpan UnfavoritedMemory = TimeSpan.FromHours(24);

    private readonly IPlaylistManager _playlistManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ArtistPlaylistService> _logger;

    // Serializes playlist work so two quick events can't create the same playlist twice.
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Songs a user removed the heart from recently, so a playback that is still running
    // doesn't put the heart and the playlist entry straight back.
    private readonly ConcurrentDictionary<string, DateTime> _unfavorited = new();

    public ArtistPlaylistService(
        IPlaylistManager playlistManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        ILogger<ArtistPlaylistService> logger)
    {
        _playlistManager = playlistManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public ILogger Logger => _logger;

    public void Dispose()
    {
        _lock.Dispose();
    }

    public static bool IsEnabledFor(User user)
    {
        var ids = Config.EnabledUserIds;
        return ids is null || ids.Length == 0 || ids.Contains(user.Id);
    }

    /// <summary>
    /// Main artist: the first track artist (so "Katy Perry feat. X" lands on Katy Perry),
    /// falling back to the album artist.
    /// </summary>
    public static string? GetMainArtist(Audio song)
    {
        return song.Artists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
            ?? song.AlbumArtists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
    }

    public static string GetPlaylistName(string artist)
    {
        return (Config.PlaylistPrefix ?? string.Empty) + artist.Trim();
    }

    public void NoteUnfavorited(User user, BaseItem item)
    {
        _unfavorited[Key(user, item)] = DateTime.UtcNow;

        var cutoff = DateTime.UtcNow - UnfavoritedMemory;
        foreach (var entry in _unfavorited.Where(e => e.Value < cutoff))
        {
            _unfavorited.TryRemove(entry.Key, out _);
        }
    }

    public void ClearUnfavorited(User user, BaseItem item)
    {
        _unfavorited.TryRemove(Key(user, item), out _);
    }

    public bool WasRecentlyUnfavorited(User user, BaseItem item)
    {
        return _unfavorited.TryGetValue(Key(user, item), out var when)
            && DateTime.UtcNow - when < UnfavoritedMemory;
    }

    public void MarkFavorite(User user, BaseItem item)
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

    /// <summary>
    /// Adds the songs to the user's playlist for the artist, creating it if needed, and hearts the playlist.
    /// </summary>
    /// <returns>The number of songs that were added.</returns>
    public async Task<int> AddSongsAsync(User user, string artist, IReadOnlyCollection<Audio> songs)
    {
        if (songs.Count == 0)
        {
            return 0;
        }

        var playlistName = GetPlaylistName(artist);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            BaseItem? playlistItem;
            int added;
            var playlist = FindPlaylist(user, playlistName);

            if (playlist is null)
            {
                var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
                {
                    Name = playlistName,
                    ItemIdList = songs.Select(s => s.Id).ToArray(),
                    MediaType = MediaType.Audio,
                    UserId = user.Id,
                    Public = false
                }).ConfigureAwait(false);

                added = songs.Count;
                _logger.LogInformation(
                    "Auto Artist Playlists: created playlist {Playlist} ({PlaylistId}) for {User} with {Count} song(s): {Songs}",
                    playlistName,
                    result.Id,
                    user.Username,
                    added,
                    SongNames(songs));
                playlistItem = Guid.TryParse(result.Id, out var newId) ? _libraryManager.GetItemById(newId) : null;
            }
            else
            {
                var existing = playlist.LinkedChildren
                    .Where(c => c.ItemId.HasValue)
                    .Select(c => c.ItemId!.Value)
                    .ToHashSet();
                var missing = songs.Where(s => !existing.Contains(s.Id)).ToList();
                added = missing.Count;

                if (added > 0)
                {
                    await _playlistManager.AddItemToPlaylistAsync(playlist.Id, missing.Select(s => s.Id).ToArray(), null, user.Id).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Auto Artist Playlists: added {Songs} to {Playlist} ({PlaylistId}, {Path}) for {User}",
                        SongNames(missing),
                        playlistName,
                        playlist.Id.ToString("N"),
                        playlist.Path,
                        user.Username);
                }

                playlistItem = playlist;
            }

            if (playlistItem is not null && Config.MarkPlaylistAsFavorite)
            {
                MarkFavorite(user, playlistItem);
            }

            return added;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes the song from the user's playlist for its main artist, if it is there.
    /// </summary>
    public async Task RemoveSongAsync(User user, Audio song)
    {
        var artist = GetMainArtist(song);
        if (artist is null)
        {
            return;
        }

        var playlistName = GetPlaylistName(artist);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var playlist = FindPlaylist(user, playlistName);
            if (playlist is null || !playlist.LinkedChildren.Any(c => c.ItemId.HasValue && c.ItemId.Value.Equals(song.Id)))
            {
                return;
            }

            await _playlistManager.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), [song.Id.ToString("N")]).ConfigureAwait(false);
            _logger.LogInformation("Auto Artist Playlists: removed {Song} from {Playlist} for {User} (heart removed)", song.Name, playlistName, user.Username);
        }
        finally
        {
            _lock.Release();
        }
    }

    // A playlist deleted outside Jellyfin can linger in the database until the next scan; only use
    // playlists whose folder still exists on disk.
    private Playlist? FindPlaylist(User user, string playlistName)
    {
        return _playlistManager.GetPlaylists(user.Id)
            .FirstOrDefault(p => p.OwnerUserId.Equals(user.Id)
                && string.Equals(p.Name, playlistName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(p.Path)
                && Directory.Exists(p.Path));
    }

    private static string Key(User user, BaseItem item) => $"{user.Id:N}:{item.Id:N}";

    private static string SongNames(IEnumerable<Audio> songs)
    {
        const int Max = 5;
        var list = songs.ToList();
        var names = string.Join(", ", list.Take(Max).Select(s => s.Name));
        return list.Count > Max ? $"{names} (+{list.Count - Max} more)" : names;
    }
}
