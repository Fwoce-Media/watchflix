# Third-party components

WatchFlix uses the following. Their licences apply to those parts.

| Component | Licence | Used for |
|---|---|---|
| [.NET 8](https://github.com/dotnet/runtime) | MIT | the runtime |
| [LibVLC](https://www.videolan.org/vlc/libvlc.html) | LGPL 2.1 or later | playback |
| [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) | LGPL 2.1 or later | connecting the app to LibVLC |
| [SQLite](https://www.sqlite.org/) | Public domain | the library database (the copy built into Windows) |

LibVLC and LibVLCSharp are included unmodified, as separate libraries in the
`libvlc` folder and `LibVLCSharp*.dll`. Their source is available from
VideoLAN at the links above.

## Metadata

Details and artwork come from these services, using your own API keys where
they need one:

- [TMDB](https://www.themoviedb.org/) — this product uses the TMDB API but is
  not endorsed or certified by TMDB.
- [TVmaze](https://www.tvmaze.com/) — data licensed under
  [CC BY-SA](https://creativecommons.org/licenses/by-sa/4.0/).
- [OMDb](https://www.omdbapi.com/)

## ffmpeg

ffmpeg is not included. WatchFlix uses it if it is on your machine, for poster
frames and hover previews.
