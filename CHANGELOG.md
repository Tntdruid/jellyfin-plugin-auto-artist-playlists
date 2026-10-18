# Changelog

The text under each version is shown in Jellyfin's plugin catalog and on the GitHub release.
Add a new `## x.y.z` section before pushing the `vx.y.z` tag.

## 1.2.0

- New scheduled task "Import existing songs": builds artist playlists from songs you have already hearted (and, optionally, played)
- Removing the heart from a song now takes it off the artist playlist (can be turned off)
- A song you remove the heart from is not added back for 24 hours, even if it keeps playing
- Translation guide in the README, so anyone can add a language to the settings page

## 1.1.0

- Renamed the plugin from "Smart Play List" to "Auto Artist Playlists"
- Settings from 1.0 are moved over automatically
- The artist playlist is also marked with a heart (can be turned off)
- Settings page in English and Danish
- If you installed 1.0 manually, delete the old "Smart Play List_1.0.0.0" folder

## 1.0.0

- First release
- Songs played at least 90% get a heart and are added to a private playlist per artist
- Settings: threshold, heart on/off, playlist name prefix and users
