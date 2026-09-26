using WatchFlix.Core.Data;
using WatchFlix.Core.Media;

namespace WatchFlix.Core.Library;

public sealed class ScanSnapshot
{
    public bool Running { get; init; }
    public string Phase { get; init; } = "idle";
    public string Current { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Missing { get; init; }
    public int Scraped { get; init; }
    public double Elapsed { get; init; }
    public List<string> Log { get; init; } = new();

    public Dictionary<string, object?> ToJson() => new()
    {
        ["running"] = Running, ["phase"] = Phase, ["current"] = Current, ["done"] = Done, ["total"] = Total,
        ["added"] = Added, ["updated"] = Updated, ["missing"] = Missing, ["scraped"] = Scraped,
        ["elapsed"] = Elapsed, ["log"] = Log,
    };
}

/// <summary>Walks the library folders and turns video files into titles, on a background thread.</summary>
public static class Scanner
{
    static readonly object Gate = new();
    static bool _running;
    static string _phase = "idle", _current = "";
    static int _done, _total, _added, _updated, _missing, _scraped;
    static DateTime? _started, _finished;
    static readonly List<string> _log = new();

    /// <summary>Raised on the scan thread whenever progress moves.</summary>
    public static event Action? Progress;
    /// <summary>Raised when a scan or rebuild finishes.</summary>
    public static event Action<ScanSnapshot>? Finished;

    public static bool Running { get { lock (Gate) return _running; } }

    public static ScanSnapshot Snapshot()
    {
        lock (Gate)
        {
            double elapsed = 0;
            if (_started is { } s) elapsed = Math.Round(((_finished ?? DateTime.UtcNow) - s).TotalSeconds, 1);
            return new ScanSnapshot
            {
                Running = _running, Phase = _phase, Current = _current, Done = _done, Total = _total,
                Added = _added, Updated = _updated, Missing = _missing, Scraped = _scraped, Elapsed = elapsed,
                Log = _log.Skip(Math.Max(0, _log.Count - 60)).ToList(),
            };
        }
    }

    static void Say(string message)
    {
        lock (Gate) _log.Add(message);
        Progress?.Invoke();
    }

    static void Set(Action change)
    {
        lock (Gate) change();
        Progress?.Invoke();
    }

    static bool Begin(string phase)
    {
        lock (Gate)
        {
            if (_running) return false;
            _running = true;
            _phase = phase;
            _current = "";
            _done = _total = _added = _updated = _missing = _scraped = 0;
            _started = DateTime.UtcNow;
            _finished = null;
            _log.Clear();
        }
        Progress?.Invoke();
        return true;
    }

    static void End()
    {
        lock (Gate)
        {
            _running = false;
            _phase = "idle";
            _current = "";
            _finished = DateTime.UtcNow;
        }
        Finished?.Invoke(Snapshot());
    }

    // ------------------------------------------------------------------ walking
    static bool Skip(string path, FileInfo info)
    {
        foreach (var part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (Config.SkipDirNames.Contains(part.ToLowerInvariant())) return true;
        if (Path.GetFileName(path).StartsWith('.')) return true;
        if (Path.GetFileNameWithoutExtension(path).ToLowerInvariant().Contains("sample")
            && info.Length < 200L * 1024 * 1024) return true;
        return false;
    }

    public static List<(string Path, string? Forced)> WalkRoots()
    {
        var found = new List<(string, string?)>();
        var seen = new HashSet<string>(PathUtil.Comparer);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Hidden folders are scanned, as before; system ones (the recycle bin,
            // System Volume Information) never hold anything worth listing.
            AttributesToSkip = FileAttributes.System,
            ReturnSpecialDirectories = false,
        };
        foreach (var (rootText, forced) in Settings.AllRoots())
        {
            var root = PathUtil.Resolve(rootText);
            if (!Directory.Exists(root))
            {
                Say($"Folder not found: {rootText}");
                continue;
            }
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*", options)
                    .Where(p => Config.VideoExts.Contains(Path.GetExtension(p)))
                    .OrderBy(p => p, PathUtil.Comparer).ToList();
            }
            catch (Exception ex)
            {
                Say($"Could not read {root}: {ex.Message}");
                continue;
            }
            foreach (var path in files)
            {
                try
                {
                    if (Skip(path, new FileInfo(path))) continue;
                }
                catch { continue; }
                if (!seen.Add(path)) continue;
                found.Add((path, forced));
            }
        }
        return found;
    }

    /// <summary>Folders that hold one show's episodes named only by number.</summary>
    public static Dictionary<string, (string Show, int Number, string Title, int? Season)> DetectSeriesFolders(
        List<(string Path, string? Forced)> files)
    {
        var hints = new Dictionary<string, (string, int, string, int?)>(PathUtil.Comparer);
        foreach (var group in files.GroupBy(f => Path.GetDirectoryName(f.Path) ?? "", PathUtil.Comparer))
        {
            var entries = group.ToList();
            var (show, folderSeason) = Names.SeriesFolderTitle(group.Key);
            if (show.Length == 0) continue;
            var matches = new List<(string Path, int Number, string Title)>();
            foreach (var (path, _) in entries)
            {
                if (Names.Parse(path, "show").Episode != null) continue;
                if (Names.FolderEpisode(Path.GetFileNameWithoutExtension(path), show) is { } hit)
                    matches.Add((path, hit.Number, hit.Title));
            }
            var numbers = matches.Select(m => m.Number).ToHashSet();
            if (numbers.Count < matches.Count) continue;
            var allNumbered = matches.Count == entries.Count;
            var enough = (allNumbered && matches.Count >= 2)
                         || (matches.Count >= 3 && ((numbers.Count > 0 ? numbers.Min() : 99) == 1 || matches.Count >= 5));
            if (matches.Count == 0 || !enough) continue;
            foreach (var (path, number, title) in matches)
                hints[path] = (show, number, title, folderSeason);
        }
        return hints;
    }

    static long FindOrCreateMedia(Parsed parsed)
    {
        var title = string.IsNullOrEmpty(parsed.Title) ? "Untitled" : parsed.Title;
        var key = Names.SortTitle(title);
        Row? row;
        if (parsed.Kind == "show")
            row = Db.QueryOne("SELECT id FROM media WHERE kind = 'show' AND sort_title = ?", key);
        else
        {
            row = Db.QueryOne("SELECT id FROM media WHERE kind = 'movie' AND sort_title = ? AND (year IS ? OR year = ?)",
                              key, parsed.Year, parsed.Year);
            if (row == null && parsed.Year != null)
                row = Db.QueryOne("SELECT id FROM media WHERE kind = 'movie' AND sort_title = ? AND year IS NULL", key);
        }
        if (row != null) return row.Long("id")!.Value;
        return Db.Insert("INSERT INTO media (kind, title, sort_title, year, added_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)",
                         parsed.Kind, title, key, parsed.Year, Db.Now(), Db.Now());
    }

    static double MTime(FileInfo info) =>
        (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>Returns (media id, created). Unchanged files are left alone.</summary>
    static (long? MediaId, bool Created) IndexFile(string path, string? forced,
                                                   (string Show, int Number, string Title, int? Season)? hint)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return (null, false);
        }
        catch { return (null, false); }

        var existing = Db.QueryOne("SELECT id, path, mtime, size, media_id, missing, parsed_v FROM files WHERE path = ?", path);
        if (existing == null && OperatingSystem.IsWindows())
        {
            // Same file, spelled with different letter case: adopt the old row.
            existing = Db.QueryOne(
                "SELECT id, path, mtime, size, media_id, missing, parsed_v FROM files WHERE path = ? COLLATE NOCASE", path);
            if (existing != null)
                Db.Execute("UPDATE files SET path = ? WHERE id = ?", path, existing.Long("id"));
        }
        // Artwork is keyed by the stored spelling, so keep using it.
        var keyPath = existing?.Str("path") is { Length: > 0 } stored ? stored : path;

        var mtime = MTime(info);
        var unchanged = existing != null && !existing.Truthy("missing")
                        && Math.Abs((existing.Double("mtime") ?? 0) - mtime) < 1
                        && existing.Long("size") == info.Length;
        if (unchanged && (existing!.Long("parsed_v") ?? 0) >= Config.ParserVersion)
            return (existing.Long("media_id"), false);

        var parsed = Names.Parse(path, forced);
        if (hint is { } h && parsed.Episode == null)
        {
            parsed.Kind = "show";
            parsed.Title = h.Show;
            parsed.Episode = h.Number;
            if (h.Title.Length > 0) parsed.EpisodeTitle = h.Title;
            parsed.Season = h.Season ?? 1;
            parsed.Year = null;
        }
        var mediaId = FindOrCreateMedia(parsed);

        double? duration;
        long? width, height;
        string vcodec, acodec, thumb, preview;
        if (unchanged)
        {
            var old = Db.QueryOne("SELECT * FROM files WHERE id = ?", existing!.Long("id"))!;
            duration = old.Double("duration");
            width = old.Long("width");
            height = old.Long("height");
            vcodec = old.Str("vcodec");
            acodec = old.Str("acodec");
            thumb = old.Str("thumb");
            preview = old.Str("preview");
        }
        else
        {
            var probe = MediaUtil.Probe(path);
            duration = probe.Duration;
            width = probe.Width;
            height = probe.Height;
            vcodec = probe.VCodec;
            acodec = probe.ACodec;
            thumb = MediaUtil.MakeThumb(keyPath, duration);
            preview = MediaUtil.MakePreview(keyPath, duration);
        }

        if (existing != null)
        {
            Db.Execute("""
                UPDATE files SET filename = ?, size = ?, mtime = ?, duration = ?,
                   width = ?, height = ?, vcodec = ?, acodec = ?, media_id = ?,
                   season = ?, episode = ?, thumb = ?, preview = ?, missing = 0, parsed_v = ?
                WHERE id = ?
                """, Path.GetFileName(path), info.Length, mtime, duration, width, height, vcodec, acodec,
                mediaId, parsed.Season, parsed.Episode, thumb, preview, Config.ParserVersion, existing.Long("id"));
            return (mediaId, false);
        }
        Db.Execute("""
            INSERT INTO files (path, filename, size, mtime, duration, width, height, vcodec, acodec, media_id,
                               season, episode, thumb, preview, parsed_v, added_at)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            """, path, Path.GetFileName(path), info.Length, mtime, duration, width, height, vcodec, acodec,
            mediaId, parsed.Season, parsed.Episode, thumb, preview, Config.ParserVersion, Db.Now());
        return (mediaId, true);
    }

    public static int TidySeasonNames() =>
        Db.Execute("UPDATE seasons SET name = '' WHERE name IN ('Episodes', 'Season')");

    /// <summary>Point every episode file at its episode row.</summary>
    public static int LinkEpisodes()
    {
        var linked = 0;
        foreach (var row in Db.Query("""
                     SELECT f.id, f.media_id, f.season, f.episode, f.episode_id, e.id AS target
                     FROM files f
                     JOIN media m ON m.id = f.media_id AND m.kind = 'show'
                     LEFT JOIN episodes e ON e.show_id = f.media_id AND e.season = f.season AND e.number = f.episode
                     WHERE f.season IS NOT NULL AND f.episode IS NOT NULL
                     """))
        {
            if (row.Long("target") is long target && target != row.Long("episode_id"))
            {
                Db.Execute("UPDATE files SET episode_id = ? WHERE id = ?", target, row.Long("id"));
                linked++;
            }
        }
        return linked;
    }

    static int MarkMissing(HashSet<string> present)
    {
        var count = 0;
        foreach (var row in Db.Query("SELECT id, path, missing FROM files"))
        {
            var path = row.Str("path");
            var gone = !present.Contains(path) && !File.Exists(path);
            if (gone && !row.Truthy("missing"))
            {
                Db.Execute("UPDATE files SET missing = 1 WHERE id = ?", row.Long("id"));
                count++;
            }
            else if (!gone && row.Truthy("missing"))
                Db.Execute("UPDATE files SET missing = 0 WHERE id = ?", row.Long("id"));
        }
        return count;
    }

    /// <summary>Fold entries that turned out to be the same title into one.</summary>
    public static int MergeDuplicateMedia()
    {
        var merged = 0;
        var groups = Db.Query("""
            SELECT kind, sort_title, COUNT(*) AS n FROM media WHERE sort_title <> ''
            GROUP BY kind, sort_title HAVING n > 1
            """);
        foreach (var group in groups)
        {
            var rows = Db.Query("""
                SELECT m.id, (SELECT COUNT(*) FROM files f WHERE f.media_id = m.id) AS files, m.scraped_at
                FROM media m WHERE m.kind = ? AND m.sort_title = ?
                ORDER BY (m.scraped_at IS NULL), files DESC, m.id
                """, group.Str("kind"), group.Str("sort_title"));
            var keep = rows[0].Long("id");
            foreach (var row in rows.Skip(1))
            {
                var loser = row.Long("id");
                Db.Execute("UPDATE files SET media_id = ?, episode_id = NULL WHERE media_id = ?", keep, loser);
                Db.Execute("""
                    INSERT OR IGNORE INTO seasons (show_id, number, name, overview, poster, air_date)
                    SELECT ?, number, name, overview, poster, air_date FROM seasons WHERE show_id = ?
                    """, keep, loser);
                Db.Execute("""
                    INSERT OR IGNORE INTO episodes (show_id, season, number, title, overview, air_date, rating, runtime, still)
                    SELECT ?, season, number, title, overview, air_date, rating, runtime, still FROM episodes WHERE show_id = ?
                    """, keep, loser);
                Db.Execute("INSERT OR IGNORE INTO media_genres (media_id, genre_id) SELECT ?, genre_id FROM media_genres WHERE media_id = ?",
                           keep, loser);
                Db.Execute("""
                    INSERT OR IGNORE INTO credits (media_id, person_id, role, character, ord)
                    SELECT ?, person_id, role, character, ord FROM credits WHERE media_id = ?
                    """, keep, loser);
                Db.Execute("DELETE FROM media WHERE id = ?", loser);
                merged++;
            }
        }
        return merged;
    }

    public static void PruneEmptyMedia() => Db.Execute(
        "DELETE FROM media WHERE id NOT IN (SELECT DISTINCT media_id FROM files WHERE media_id IS NOT NULL)");

    // ---------------------------------------------------------------- the scan
    public static bool StartScan(bool rescrape = false)
    {
        if (!Begin("listing files")) return false;
        new Thread(() => RunScan(rescrape)) { IsBackground = true, Name = "WatchFlix scan" }.Start();
        return true;
    }

    static void RunScan(bool rescrape)
    {
        try
        {
            if (Settings.AllRoots().Count == 0)
            {
                Say("No library folders set. Add one in Settings.");
                return;
            }
            var files = WalkRoots();
            Set(() => { _total = files.Count; _phase = "reading files"; });
            Say($"Found {files.Count} video files.");

            var hints = DetectSeriesFolders(files);
            if (hints.Count > 0)
            {
                var shows = hints.Values.Select(v => v.Show).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
                Say($"Recognised {shows.Count} folders as episode collections: {string.Join(", ", shows.Take(6))}"
                    + (shows.Count > 6 ? "…" : ""));
            }

            var touched = new HashSet<long>();
            var present = new HashSet<string>(PathUtil.Comparer);
            foreach (var (path, forced) in files)
            {
                Set(() => _current = Path.GetFileName(path));
                present.Add(path);
                long? mediaId = null;
                var created = false;
                try
                {
                    (mediaId, created) = IndexFile(path, forced, hints.TryGetValue(path, out var h) ? h : null);
                }
                catch (Exception ex) { Say($"Skipped {Path.GetFileName(path)}: {ex.Message}"); }
                if (mediaId is long id) touched.Add(id);
                Set(() => { _done++; if (created) _added++; });
            }

            Set(() => _phase = "checking for moved files");
            var gone = MarkMissing(present);
            Set(() => _missing = gone);
            PruneEmptyMedia();

            var folded = MergeDuplicateMedia();
            if (folded > 0) Say($"Folded {folded} duplicate entries into their originals.");
            TidySeasonNames();
            var relinked = LinkEpisodes();
            if (relinked > 0) Say($"Reconnected {relinked} files to their episode details.");

            var autoScrape = Settings.Bool("auto_scrape_on_scan", true);
            if (rescrape || autoScrape)
            {
                var pending = Db.Query("""
                        SELECT id FROM media WHERE locked = 0 AND (? = 1 OR scraped_at IS NULL) ORDER BY sort_title
                        """, rescrape ? 1 : 0)
                    .Select(r => r.Long("id")!.Value)
                    .Where(id => touched.Contains(id) || rescrape).ToList();
                Set(() => { _phase = "fetching details"; _total = pending.Count; _done = 0; });
                foreach (var mediaId in pending)
                {
                    var row = Db.QueryOne("SELECT title, year FROM media WHERE id = ?", mediaId);
                    Set(() => _current = row?.Str("title") ?? "");
                    var result = Metadata.ScrapeMedia(mediaId);
                    Set(() => { _done++; if (result.Ok) _scraped++; });
                    if (!result.Ok)
                        Say($"{row?.Str("title") ?? mediaId.ToString()}: {(result.Log.Count > 0 ? result.Log[^1] : "no match")}");
                }
            }

            if (autoScrape)
            {
                Set(() => _phase = "filling in episode details");
                var gaps = Metadata.FillEpisodeGaps();
                if (gaps.Seasons > 0) Say($"Fetched details for {gaps.Episodes} episodes across {gaps.Seasons} seasons.");
                foreach (var line in gaps.Log.Take(20)) Say(line);
                LinkEpisodes();
            }

            Db.BumpLibrary();
            Say("Scan complete.");
        }
        catch (Exception ex)
        {
            Say($"The scan stopped: {ex.Message}");
        }
        finally
        {
            End();
        }
    }

    /// <summary>Regenerate every hover preview at the current quality setting.</summary>
    public static bool StartPreviewRebuild()
    {
        var (quality, _) = MediaUtil.PreviewSettings();
        if (!Begin($"rebuilding previews ({quality})")) return false;
        new Thread(RunPreviewRebuild) { IsBackground = true, Name = "WatchFlix previews" }.Start();
        return true;
    }

    static void RunPreviewRebuild()
    {
        try
        {
            var (quality, preset) = MediaUtil.PreviewSettings();
            if (preset == null)
            {
                Db.Execute("UPDATE files SET preview = '' WHERE preview <> ''");
                Say("Hover previews are switched off. Existing clips unlinked.");
                return;
            }
            if (!Ffmpeg.HasFfmpeg)
            {
                Say("ffmpeg is not on PATH, so previews cannot be built.");
                return;
            }
            var rows = Db.Query("SELECT id, path, duration FROM files WHERE missing = 0 ORDER BY id");
            Set(() => _total = rows.Count);
            Say($"Rebuilding {rows.Count} previews at {quality} ({preset.Width}px, crf {preset.Crf}).");
            foreach (var row in rows)
            {
                var path = row.Str("path");
                Set(() => _current = Path.GetFileName(path));
                try
                {
                    var preview = MediaUtil.MakePreview(path, row.Double("duration"), force: true);
                    Db.Execute("UPDATE files SET preview = ? WHERE id = ?", preview, row.Long("id"));
                    if (preview.Length > 0) Set(() => _updated++);
                }
                catch (Exception ex) { Say($"{Path.GetFileName(path)}: {ex.Message}"); }
                Set(() => _done++);
            }
            Db.BumpLibrary();
            Say("Previews rebuilt.");
        }
        finally
        {
            End();
        }
    }
}
