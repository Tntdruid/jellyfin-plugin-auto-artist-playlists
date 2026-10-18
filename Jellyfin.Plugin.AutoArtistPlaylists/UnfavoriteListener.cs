using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoArtistPlaylists;

/// <summary>
/// Removes a song from its artist playlist when the user removes the heart.
/// </summary>
public sealed class UnfavoriteListener : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ArtistPlaylistService _service;
    private readonly ILogger<UnfavoriteListener> _logger;

    public UnfavoriteListener(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ArtistPlaylistService service,
        ILogger<UnfavoriteListener> logger)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _service = service;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        // Only the heart button saves with this reason. Playback saves use other reasons, and can carry
        // a stale "not favorite" that must not be taken as the user removing the heart.
        if (e.SaveReason != UserDataSaveReason.UpdateUserRating || e.Item is not Audio song || e.UserData is null)
        {
            return;
        }

        var user = _userManager.GetUserById(e.UserId);
        if (user is null || !ArtistPlaylistService.IsEnabledFor(user))
        {
            return;
        }

        if (e.UserData.IsFavorite)
        {
            _service.ClearUnfavorited(user, song);
            return;
        }

        _service.NoteUnfavorited(user, song);

        if (!ArtistPlaylistService.Config.RemoveWhenUnfavorited)
        {
            return;
        }

        _ = RemoveAsync(user, song);
    }

    private async Task RemoveAsync(Jellyfin.Database.Implementations.Entities.User user, Audio song)
    {
        try
        {
            await _service.RemoveSongAsync(user, song).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto Artist Playlists: failed to remove {Song} from playlist for {User}", song.Name, user.Username);
        }
    }
}
