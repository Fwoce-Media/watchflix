using WatchFlix.Core.Data;
using WatchFlix.Core.Media;

namespace WatchFlix.Core.Library;

/// <summary>Changes made by hand: details, artwork, people, and tidying the library.</summary>
public static class Editing
{
    public static readonly HashSet<string> EditableMedia = new()
    {
        "title", "year", "overview", "tagline", "certificate", "rating", "release_date", "status", "runtime",
        "trailer", "poster", "backdrop", "kind", "locked", "imdb_id",
    };

    public static bool UpdateMedia(long mediaId, IDictionary<string, object?> fields,
                                   IEnumerable<string>? genres = null,
                                   IEnumerable<(string Name, string Character)>? cast = null)
    {
        if (Db.QueryOne("SELECT id FROM media WHERE id = ?", mediaId) == null) return false;
        var sets = new List<string>();
        var args = new List<object?>();
        foreach (var (key, value) in fields)
        {
            if (!EditableMedia.Contains(key)) continue;
            sets.Add($"{key} = ?");
            args.Add(value);
            if (key == "title")
            {
                sets.Add("sort_title = ?");
                args.Add(Names.SortTitle(Convert.ToString(value) ?? ""));
            }
        }
        if (sets.Count > 0)
        {
            sets.Add("updated_at = ?");
            args.Add(Db.Now());
            args.Add(mediaId);
            Db.Execute($"UPDATE media SET {string.Join(", ", sets)} WHERE id = ?", args.ToArray());
        }
        if (genres != null) Db.SetGenres(mediaId, genres);
        if (cast != null)
        {
            Db.Execute("DELETE FROM credits WHERE media_id = ? AND role = 'cast'", mediaId);
            var i = 0;
            foreach (var (name, character) in cast)
            {
                if (string.IsNullOrWhiteSpace(name)) { i++; continue; }
                Db.Execute("INSERT OR REPLACE INTO credits (media_id, person_id, role, character, ord) VALUES (?,?, 'cast', ?, ?)",
                           mediaId, Db.PersonId(name), character, i);
                i++;
            }
        }
        Db.BumpLibrary();
        return true;
    }

    /// <summary>Adopt one provider's poster or backdrop.</summary>
    public static bool ChooseArtwork(long mediaId, string? poster, string? backdrop, string title = "")
    {
        if (Db.QueryOne("SELECT id FROM media WHERE id = ?", mediaId) == null) return false;
        var sets = new List<string>();
        var args = new List<object?>();
        if (!string.IsNullOrEmpty(poster))
        {
            sets.Add("poster = ?");
            args.Add(Metadata.CacheImage(poster, Config.PosterDir, title));
        }
        if (!string.IsNullOrEmpty(backdrop))
        {
            sets.Add("backdrop = ?");
            args.Add(Metadata.CacheImage(backdrop, Config.BackdropDir, title));
        }
        if (sets.Count == 0) return false;
        args.Add(Db.Now());
        args.Add(mediaId);
        Db.Execute($"UPDATE media SET {string.Join(", ", sets)}, updated_at = ? WHERE id = ?", args.ToArray());
        Db.BumpLibrary();
        return true;
    }

    /// <summary>Only the database entry goes. WatchFlix never deletes a video file.</summary>
    public static void RemoveMedia(long mediaId)
    {
        Db.Execute("DELETE FROM media WHERE id = ?", mediaId);
        Db.Execute("DELETE FROM files WHERE media_id IS NULL");
        Db.BumpLibrary();
    }

    static readonly HashSet<string> PersonFields = new()
        { "name", "bio", "birthday", "deathday", "birthplace", "photo", "known_for", "imdb_id", "locked" };

    public static void UpdatePerson(long personId, IDictionary<string, object?> fields)
    {
        var sets = new List<string>();
        var args = new List<object?>();
        foreach (var (key, value) in fields)
            if (PersonFields.Contains(key))
            {
                sets.Add($"{key} = ?");
                args.Add(value);
            }
        if (sets.Count == 0) return;
        args.Add(personId);
        Db.Execute($"UPDATE people SET {string.Join(", ", sets)} WHERE id = ?", args.ToArray());
        Db.BumpLibrary();
    }

    /// <summary>Use your own picture as a poster, backdrop or portrait. Returns its /media URL.</summary>
    public static string? UploadImage(string target, long targetId, string sourceFile)
    {
        var folder = target switch
        {
            "poster" => Config.PosterDir,
            "backdrop" => Config.BackdropDir,
            "person" => Config.PersonDir,
            _ => null,
        };
        if (folder == null) return null;
        var ext = Path.GetExtension(sourceFile).ToLowerInvariant();
        if (ext.Length == 0) ext = ".jpg";
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp")) return null;
        // A fresh name each time, so a cached copy of the old picture is never shown.
        foreach (var stale in Directory.EnumerateFiles(folder, $"{target}-{targetId}*"))
            try { File.Delete(stale); } catch { }
        var name = $"{target}-{targetId}-{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
        File.Copy(sourceFile, Path.Combine(folder, name), true);
        var url = $"/media/image/{Path.GetFileName(folder)}/{name}";
        if (target == "person") Db.Execute("UPDATE people SET photo = ? WHERE id = ?", url, targetId);
        else Db.Execute($"UPDATE media SET {target} = ?, updated_at = ? WHERE id = ?", url, Db.Now(), targetId);
        Db.BumpLibrary();
        return url;
    }

    /// <summary>Make the poster frame from a chosen moment, and rebuild the hover clip.</summary>
    public static (bool Ok, string Message, string Thumb) RebuildArtwork(long fileId, double? at = null)
    {
        var row = Db.QueryOne("SELECT path, duration FROM files WHERE id = ?", fileId);
        if (row == null) return (false, "No such file", "");
        var path = row.Str("path");
        if (!File.Exists(path)) return (false, "The file has moved. Rescan to relink it.", "");
        if (!Ffmpeg.HasFfmpeg) return (false, "ffmpeg is not available, so frames can't be grabbed.", "");
        MediaUtil.ClearArtwork(path);
        var thumb = MediaUtil.MakeThumb(path, row.Double("duration"), at, force: true);
        var preview = MediaUtil.MakePreview(path, row.Double("duration"), force: true);
        Db.Execute("UPDATE files SET thumb = ?, preview = ? WHERE id = ?", thumb, preview, fileId);
        Db.BumpLibrary();
        return (true, "Poster frame set.", thumb);
    }

    /// <summary>
    /// A frame taken as the title's poster. The Python build only rebuilt the
    /// file's own frame; the title's scraped poster then still won. Setting it
    /// as the poster directly is what the button promised.
    /// </summary>
    public static (bool Ok, string Message) FrameAsPoster(long fileId, double at)
    {
        var (ok, message, thumb) = RebuildArtwork(fileId, at);
        if (!ok || thumb.Length == 0) return (ok, message);
        var owner = Db.QueryOne("SELECT m.id, m.kind FROM files f JOIN media m ON m.id = f.media_id WHERE f.id = ?", fileId);
        // For a series the frame belongs to the episode, not the whole show.
        if (owner != null && owner.Str("kind") == "movie" && MediaUtil.LocalFileFor(thumb) is { } source)
        {
            UploadImage("poster", owner.Long("id")!.Value, source);
            return (true, "Poster set from this moment.");
        }
        return (true, "Episode frame set from this moment.");
    }

    // ---------------------------------------------------------------- tidying
    public sealed record MissingPreview(List<Row> Files, List<Row> Titles, int Recovered, long Bytes);

    /// <summary>What a cleanup would remove, re-checking the disk first.</summary>
    public static MissingPreview PreviewMissing()
    {
        var rows = Db.Query("""
            SELECT f.id, f.path, f.filename, f.size, m.id AS media_id, m.title, m.kind, m.year
            FROM files f LEFT JOIN media m ON m.id = f.media_id
            WHERE f.missing = 1 ORDER BY m.sort_title, f.filename
            """);
        var items = new List<Row>();
        var recovered = 0;
        foreach (var row in rows)
        {
            if (File.Exists(row.Str("path")))
            {
                Db.Execute("UPDATE files SET missing = 0 WHERE id = ?", row.Long("id"));
                recovered++;
                continue;
            }
            items.Add(row);
        }
        var orphans = new List<Row>();
        foreach (var mediaId in items.Select(i => i.Long("media_id")).Where(id => id != null).Distinct())
        {
            var remaining = Db.Scalar("SELECT COUNT(*) AS n FROM files WHERE media_id = ? AND missing = 0", mediaId);
            if (remaining > 0) continue;
            var m = Db.QueryOne("SELECT title, kind, year FROM media WHERE id = ?", mediaId);
            if (m != null)
            {
                var entry = new Row { ["id"] = mediaId };
                foreach (var (k, v) in m) entry[k] = v;
                orphans.Add(entry);
            }
        }
        return new MissingPreview(items, orphans, recovered, items.Sum(i => i.Long("size") ?? 0));
    }

    public sealed record CleanupResult(int FilesRemoved, int TitlesRemoved, int Recovered, long Bytes, int Relinked);

    /// <summary>Forget files no longer on disk, and titles left with none. Nothing on disk is touched.</summary>
    public static CleanupResult CleanupMissing()
    {
        var preview = PreviewMissing();
        foreach (var item in preview.Files)
        {
            try { MediaUtil.ClearArtwork(item.Str("path")); } catch { }
            Db.Execute("DELETE FROM files WHERE id = ?", item.Long("id"));
        }
        Scanner.PruneEmptyMedia();
        var relinked = Scanner.LinkEpisodes();
        Db.BumpLibrary();
        return new CleanupResult(preview.Files.Count, preview.Titles.Count, preview.Recovered, preview.Bytes, relinked);
    }

    public sealed record RepairResult(int Relinked, int Merged, int Seasons, int Episodes, List<string> Log);

    /// <summary>Reattach episode files to their details, then fetch whatever is still missing.</summary>
    public static RepairResult RepairEpisodes(bool fetch = true, long? mediaId = null)
    {
        var merged = Scanner.MergeDuplicateMedia();
        Scanner.TidySeasonNames();
        var relinked = Scanner.LinkEpisodes();
        var gaps = (Seasons: 0, Episodes: 0, Log: new List<string>());
        if (fetch)
        {
            gaps = Metadata.FillEpisodeGaps(mediaId);
            relinked += Scanner.LinkEpisodes();
        }
        Db.BumpLibrary();
        return new RepairResult(relinked, merged, gaps.Seasons, gaps.Episodes, gaps.Log);
    }
}
