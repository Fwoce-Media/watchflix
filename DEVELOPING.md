# WatchFlix — developer notes

For the public description, see [README.md](README.md).

A library for the films and series you own. It reads the folders you point it at,
fetches details for what it finds, and plays the files — without moving, renaming
or deleting any of them.

A native WPF window with VLC doing the playing. It opens the same library as
WatchFlix 3.0 (`%USERPROFILE%\.watchflix`): titles, artwork, scraped details and
places in each file carry over.

---

## Build and run

1. Install **Visual Studio 2022** with the **.NET desktop development** workload.
2. Unzip this folder somewhere, e.g. `C:\Users\<you>\source\WatchFlix`.
3. Open **WatchFlix.sln**.
4. Press **F5**.

The first build downloads the video player (about 90 MB). After that,
`src\WatchFlix.Desktop\bin\Debug\net8.0-windows\WatchFlix.exe` runs on its own.

**ffmpeg** makes poster frames, hover previews and the frames under the player.
Keep it on PATH, or put `ffmpeg.exe` and `ffprobe.exe` beside `WatchFlix.exe`.

---

## The player

| Key | |
|---|---|
| `Space` / `K` | play / pause |
| `←` `→` / `J` `L` | back / forward 10 s |
| `↑` `↓` | volume |
| `M` | mute |
| `C` / `D` | subtitles / audio track |
| `N` / `P` | next / previous |
| `A` | auto next |
| `F` / `F11` | full screen |
| `Esc` | leave full screen, then close |

The pop-out button (beside CC) moves the video into a small window that stays
above everything else.

---

## Releases

GitHub Actions builds both downloads. Push a version tag and it publishes a
draft release with `WatchFlix-<version>-Setup.exe` and
`WatchFlix-<version>-portable.zip`:

```
git tag v4.1.0
git push origin v4.1.0
```

The installer script is `installer\WatchFlix.iss` (Inno Setup 6). A
`portable.txt` beside `WatchFlix.exe` keeps the library in a `Data` folder next
to the program instead of `%USERPROFILE%\.watchflix`.

---

## Where things live

```
WatchFlix.sln
src\WatchFlix.Desktop\   the window, pages and player (WPF + LibVLCSharp)
src\WatchFlix.Core\      library, scanning, scraping, ffmpeg (no packages)
```

Library data: `%USERPROFILE%\.watchflix\` — `library.db`, `settings.json`,
`cache\`, `images\`. Errors go to `watchflix.log` there.
