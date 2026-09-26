# WatchFlix

A library for the films and series you own, on your own Windows PC. It looks
like a streaming service, and nothing ever leaves your computer.

Point it at your folders and it reads the files, fetches posters, synopses,
cast and ratings, and plays everything in place.

---

## Download

| | |
|---|---|
| **Setup.exe** | Installs normally, with a Start menu entry and an uninstaller. |
| **Portable zip** | Unzip and run. Everything stays in its own folder. |

Windows 10 or 11, 64-bit. Both are on the [Releases](../../releases) page.

### ffmpeg

Used for poster frames and hover previews. Playback and browsing work without
it.

Either install it so it is on your PATH, or put `ffmpeg.exe` and `ffprobe.exe`
beside `WatchFlix.exe`.

---

## What it does

- **Reads your existing folders.** Nothing is moved, renamed or deleted.
- **Films and TV shows** on their own tabs, with a home page of what you were
  watching.
- **Plays every file as it is** — MKV, HEVC, 10-bit, DTS — with switchable audio
  and subtitle tracks.
- **Picks up where you left off**, per episode, with Continue watching.
- **Series pages** with a season picker and every episode's details.
- **Fetches details** from TMDB, TVmaze and OMDb, falling back from one to the
  next when a service is down.
- **Actors** with photos, biographies and every title they appear in.
- **Pop-out player** that stays above your other windows.
- **Hover previews** on posters.

## Where your things are kept

| | Installed | Portable |
|---|---|---|
| Library, artwork, previews | `%USERPROFILE%\.watchflix` | `Data` beside the program |

Removing the program never touches your library, and never touches your videos.

## Privacy

Nothing is uploaded and there is no telemetry of any kind. The only time it
reaches the internet is to look up details and artwork for your titles.

---

## Supporting it

WatchFlix is free, and stays free.

It is built by [Fwoce Media](https://fwoce-media.github.io/) — free, private,
offline-first apps with no accounts, no tracking and no subscriptions. If the
work is useful to you, you can support it on
[Ko-fi](https://ko-fi.com/fwocemedia) or
[Buy Me a Coffee](https://www.buymeacoffee.com/Fwoce_Media).

Bug reports and ideas are welcome in [Issues](../../issues).

## Licence

MIT — see [LICENSE](LICENSE). Third-party components are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

This product uses the TMDB API but is not endorsed or certified by TMDB.

WatchFlix is a library manager. It contains no media, indexes no websites and
downloads no video. It plays the files you already have.
