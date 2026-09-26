using System.Text.Json;
using System.Text.RegularExpressions;
using WatchFlix.Core.Data;
using WatchFlix.Core.Media;

namespace WatchFlix.Core.Library;

/// <summary>
/// Every question the screens ask of the library: rows for the home page,
/// detail pages, what plays next, where you left off.
/// Rows come back as dictionaries keyed exactly as the Python build's JSON was.
/// </summary>
public static class Catalog
{
    public static readonly Dictionary<string, string> Sorts = new()
    {
        ["recent"] = "m.added_at DESC",
        ["title"] = "m.sort_title ASC",
        ["year"] = "COALESCE(m.year, 0) DESC, m.sort_title ASC",
        ["rating"] = "COALESCE(m.rating, -1) DESC, m.sort_title ASC",
        ["popularity"] = "m.popularity DESC, COALESCE(m.rating, 0) DESC",
        ["watched"] = "COALESCE(st.plays, 0) DESC, m.sort_title ASC",
        ["recently_watched"] = "COALESCE(st.last_played, 0) DESC, m.sort_title ASC",
        ["genre"] = "first_genre ASC, m.sort_title ASC",
        ["runtime"] = "COALESCE(m.runtime, 0) DESC",
    };

    public static List<string> GenresFor(long mediaId) => Db.Query("""
        SELECT g.name FROM genres g JOIN media_genres mg ON mg.genre_id = g.id
        WHERE mg.media_id = ? ORDER BY g.name
        """, mediaId).Select(r => r.Str("name")).ToList();

    public static Row Card(Row source)
    {
        var data = new Row(source);
        var mediaId = data.Long("id") ?? 0;
        data["genres"] = GenresFor(mediaId);
        var counts = Db.QueryOne("SELECT SUM(missing = 0) AS present, SUM(missing = 1) AS gone FROM files WHERE media_id = ?", mediaId);
        data["missing_files"] = counts?.Long("gone") ?? 0;
        data["unplayable"] = counts != null && (counts.Long("present") ?? 0) == 0;
        if (data.Str("kind") == "show")
        {
            var c = Db.QueryOne("SELECT COUNT(*) AS episodes, COUNT(DISTINCT season) AS seasons FROM files WHERE media_id = ? AND missing = 0", mediaId);
            data["episode_count"] = c?.Long("episodes") ?? 0;
            data["season_count"] = c?.Long("seasons") ?? 0;
        }
        else
        {
            var f = Db.QueryOne("""
                SELECT id, duration, width, height, thumb, preview FROM files
                WHERE media_id = ? AND missing = 0 ORDER BY size DESC LIMIT 1
                """, mediaId);
            if (f != null)
            {
                data["file_id"] = f.Long("id");
                data["duration"] = f.Get("duration");
                data["quality"] = MediaUtil.QualityBadge(f.Long("width"), f.Long("height"));
                data["thumb"] = f.Get("thumb");
                data["preview"] = f.Get("preview");
            }
        }
        if (!data.Truthy("poster") && data.Truthy("thumb")) data["poster"] = data["thumb"];
        data["plays"] = data.Long("plays") ?? 0;
        return data;
    }

    static (string Sql, List<object?> Args) BaseQuery(string? kind, string? genre, string? q, string sort, bool unwatched = false)
    {
        var order = Sorts.TryGetValue(sort, out var o) ? o : Sorts["recent"];
        var args = new List<object?>();
        var where = new List<string> { "EXISTS (SELECT 1 FROM files f WHERE f.media_id = m.id)" };
        if (kind is "movie" or "show")
        {
            where.Add("m.kind = ?");
            args.Add(kind);
        }
        if (!string.IsNullOrEmpty(genre))
        {
            where.Add("EXISTS (SELECT 1 FROM media_genres mg JOIN genres g ON g.id = mg.genre_id WHERE mg.media_id = m.id AND g.name = ? COLLATE NOCASE)");
            args.Add(genre);
        }
        if (!string.IsNullOrEmpty(q))
        {
            where.Add("""
                (m.title LIKE ? OR m.overview LIKE ?
                 OR EXISTS (SELECT 1 FROM credits c JOIN people p ON p.id = c.person_id WHERE c.media_id = m.id AND p.name LIKE ?)
                 OR EXISTS (SELECT 1 FROM files f2 WHERE f2.media_id = m.id AND f2.path LIKE ?)
                 OR EXISTS (SELECT 1 FROM episodes e WHERE e.show_id = m.id AND e.title LIKE ?))
                """);
            var like = $"%{q}%";
            args.AddRange(new object?[] { like, like, like, like, like });
        }
        if (unwatched)
        {
            where.Add("NOT EXISTS (SELECT 1 FROM files f3 JOIN progress pr ON pr.file_id = f3.id AND pr.user_id = ? WHERE f3.media_id = m.id AND pr.completed = 1)");
            args.Add(Db.Me);
        }
        var sql = $"""
            SELECT m.*, COALESCE(st.plays, 0) AS plays, st.last_played,
                   (SELECT g.name FROM media_genres mg JOIN genres g ON g.id = mg.genre_id
                    WHERE mg.media_id = m.id ORDER BY g.name LIMIT 1) AS first_genre
            FROM media m
            LEFT JOIN media_stats st ON st.media_id = m.id
            WHERE {string.Join(" AND ", where)}
            ORDER BY {order}
            """;
        return (sql, args);
    }

    static List<Row> Cards(string sql, List<object?> args, int limit, int offset = 0)
    {
        var all = new List<object?>(args) { limit, offset };
        return Db.Query(sql + " LIMIT ? OFFSET ?", all.ToArray()).Select(Card).ToList();
    }

    // ------------------------------------------------------------------ lists
    public sealed record Page(List<Row> Items, long Total, int Limit, int Offset, string Sort);

    public static Page LibraryPage(string? kind, string? genre, string? q, string sort = "recent",
                                   bool unwatched = false, int limit = 120, int offset = 0)
    {
        limit = Math.Min(limit, 500);
        var (sql, args) = BaseQuery(kind, genre, q, sort, unwatched);
        var items = Cards(sql, args, limit, offset);
        var total = Db.Scalar($"SELECT COUNT(*) AS n FROM ({sql})", args.ToArray());
        return new Page(items, total, limit, offset, sort);
    }

    public static List<Row> Genres(string? kind)
    {
        var args = new List<object?>();
        var clause = "";
        if (kind is "movie" or "show")
        {
            clause = "AND m.kind = ?";
            args.Add(kind);
        }
        return Db.Query($"""
            SELECT g.name, COUNT(DISTINCT m.id) AS count
            FROM genres g
            JOIN media_genres mg ON mg.genre_id = g.id
            JOIN media m ON m.id = mg.media_id
            WHERE EXISTS (SELECT 1 FROM files f WHERE f.media_id = m.id) {clause}
            GROUP BY g.name ORDER BY count DESC, g.name
            """, args.ToArray());
    }

    public sealed class Shelf
    {
        public string Title { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Genre { get; set; } = "";
        public List<Row> Items { get; set; } = new();

        public Dictionary<string, object?> ToJson()
        {
            var d = new Dictionary<string, object?> { ["title"] = Title, ["items"] = Items };
            if (Kind.Length > 0) d["kind"] = Kind;
            if (Genre.Length > 0) d["genre"] = Genre;
            return d;
        }
    }

    /// <summary>One page over the whole library: a billboard, then rows to browse sideways.</summary>
    public static (Row? Hero, List<Shelf> Rows) FrontPage()
    {
        List<Row> Query(string sort, string? kind = null, string? genre = null, int limit = 20)
        {
            var (sql, args) = BaseQuery(kind, genre, null, sort);
            return Cards(sql, args, limit);
        }

        var rows = new List<Shelf>();
        var resume = ContinueWatching(null, 20);
        if (resume.Count > 0) rows.Add(new Shelf { Title = "Continue watching", Items = resume, Kind = "resume" });
        rows.Add(new Shelf { Title = "Recently added", Items = Query("recent") });
        rows.Add(new Shelf { Title = "Films", Items = Query("recent", "movie") });
        rows.Add(new Shelf { Title = "TV shows", Items = Query("recent", "show") });
        rows.Add(new Shelf { Title = "Highest rated", Items = Query("rating") });
        rows.Add(new Shelf { Title = "Most watched", Items = Query("watched") });

        var seen = rows.Select(r => r.Title).ToHashSet();
        foreach (var genre in Db.Query("""
                     SELECT g.name, COUNT(DISTINCT m.id) AS n FROM genres g
                     JOIN media_genres mg ON mg.genre_id = g.id
                     JOIN media m ON m.id = mg.media_id
                     WHERE EXISTS (SELECT 1 FROM files f WHERE f.media_id = m.id)
                     GROUP BY g.name HAVING n >= 4 ORDER BY n DESC LIMIT 6
                     """))
        {
            var name = genre.Str("name");
            if (seen.Contains(name)) continue;
            rows.Add(new Shelf { Title = name, Items = Query("popularity", genre: name) });
        }
        rows = rows.Where(r => r.Items.Count >= 2).ToList();

        var candidates = rows.SelectMany(r => r.Items)
            .Where(c => c.Truthy("backdrop") && c.Truthy("overview")).ToList();
        Row? hero = null;
        if (candidates.Count > 0)
        {
            var ranked = candidates.OrderBy(c => -(c.Double("rating") ?? 0)).Take(12).ToList();
            hero = new Row(ranked[(int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86400 % ranked.Count)]);
            var detail = Db.QueryOne("SELECT overview, tagline, certificate, runtime FROM media WHERE id = ?", hero.Long("id"));
            if (detail != null)
                foreach (var (k, v) in detail) hero[k] = v;
            hero["genres"] = GenresFor(hero.Long("id") ?? 0).Take(3).ToList();
            hero["resume"] = ResumeTarget(hero.Long("id") ?? 0);
        }
        return (hero, rows);
    }

    /// <summary>
    /// The home page slideshow: the two series and two films watched most
    /// recently — anything part-way through first — alternating series, film.
    /// A library with little history is topped up from its best-rated titles.
    /// Only titles with a backdrop qualify, since the picture is the point.
    /// </summary>
    public static List<Row> Spotlight(int perKind = 2)
    {
        var picked = new Dictionary<string, List<long>>();
        foreach (var kind in new[] { "show", "movie" })
        {
            var ids = Db.Query("""
                SELECT m.id,
                       MAX(pr.updated_at) AS last,
                       MAX(CASE WHEN pr.completed = 0 AND pr.duration > 0
                                 AND pr.position / pr.duration > ? THEN 1 ELSE 0 END) AS going
                FROM progress pr
                JOIN files f ON f.id = pr.file_id
                JOIN media m ON m.id = f.media_id
                WHERE pr.user_id = ? AND m.kind = ? AND f.missing = 0
                  AND m.backdrop IS NOT NULL AND m.backdrop <> ''
                GROUP BY m.id
                ORDER BY going DESC, last DESC
                LIMIT ?
                """, Config.ResumeMin, Db.Me, kind, perKind)
                .Select(r => r.Long("id") ?? 0).ToList();
            if (ids.Count < perKind)
                foreach (var r in Db.Query("""
                             SELECT m.id FROM media m
                             WHERE m.kind = ? AND m.backdrop IS NOT NULL AND m.backdrop <> ''
                               AND EXISTS (SELECT 1 FROM files f WHERE f.media_id = m.id AND f.missing = 0)
                             ORDER BY COALESCE(m.rating, 0) DESC, m.id DESC
                             LIMIT ?
                             """, kind, perKind * 3))
                {
                    var id = r.Long("id") ?? 0;
                    if (ids.Count >= perKind) break;
                    if (!ids.Contains(id)) ids.Add(id);
                }
            picked[kind] = ids;
        }

        var order = new List<long>();
        for (var i = 0; i < perKind; i++)
            foreach (var kind in new[] { "show", "movie" })
                if (i < picked[kind].Count) order.Add(picked[kind][i]);

        var slides = new List<Row>();
        foreach (var id in order)
        {
            var row = Db.QueryOne("""
                SELECT id, kind, title, year, poster, backdrop, overview, tagline, certificate, rating, runtime
                FROM media WHERE id = ?
                """, id);
            if (row == null) continue;
            row["genres"] = GenresFor(id).Take(3).ToList();
            row["resume"] = ResumeTarget(id);
            slides.Add(row);
        }
        return slides;
    }

    /// <summary>The rows each tab opens on.</summary>
    public static List<Shelf> TabHome(string kind)
    {
        (string, List<object?>) For(string sort, string? genre = null) => BaseQuery(kind, genre, null, sort);
        var rows = new List<Shelf>();
        var cont = ContinueWatching(kind, 20);
        if (cont.Count > 0) rows.Add(new Shelf { Title = "Continue watching", Kind = "continue", Items = cont });

        var (sql, args) = For("recent");
        var recent = Cards(sql, args, 20);
        if (recent.Count > 0) rows.Add(new Shelf { Title = "Recently added", Kind = "grid", Items = recent });

        (sql, args) = For("watched");
        var played = Cards(sql, args, 20).Where(p => p.Truthy("plays")).ToList();
        if (played.Count > 0) rows.Add(new Shelf { Title = "Most watched", Kind = "grid", Items = played });

        (sql, args) = For("rating");
        var top = Cards(sql, args, 20).Where(t => t.Truthy("rating")).ToList();
        if (top.Count > 0) rows.Add(new Shelf { Title = "Highest rated", Kind = "grid", Items = top });

        foreach (var g in Db.Query("""
                     SELECT g.name, COUNT(DISTINCT m.id) AS n FROM genres g
                     JOIN media_genres mg ON mg.genre_id = g.id
                     JOIN media m ON m.id = mg.media_id AND m.kind = ?
                     WHERE EXISTS (SELECT 1 FROM files f WHERE f.media_id = m.id)
                     GROUP BY g.name HAVING n >= 2 ORDER BY n DESC LIMIT 4
                     """, kind))
        {
            var name = g.Str("name");
            (sql, args) = For("popularity", name);
            var items = Cards(sql, args, 20);
            if (items.Count > 0) rows.Add(new Shelf { Title = name, Kind = "grid", Genre = name, Items = items });
        }
        return rows;
    }

    // ----------------------------------------------------------------- detail
    public static Row? MediaDetail(long mediaId, bool includePaths = true)
    {
        var row = Db.QueryOne("""
            SELECT m.*, COALESCE(st.plays, 0) AS plays, st.last_played
            FROM media m LEFT JOIN media_stats st ON st.media_id = m.id WHERE m.id = ?
            """, mediaId);
        if (row == null) return null;
        var data = new Row(row);
        data["genres"] = GenresFor(mediaId);
        data["cast"] = Db.Query("""
            SELECT p.id, p.name, p.photo, p.birthday, c.character, c.ord
            FROM credits c JOIN people p ON p.id = c.person_id
            WHERE c.media_id = ? AND c.role = 'cast' ORDER BY c.ord LIMIT 40
            """, mediaId);
        data["crew"] = Db.Query("""
            SELECT p.id, p.name, p.photo, c.character AS job
            FROM credits c JOIN people p ON p.id = c.person_id
            WHERE c.media_id = ? AND c.role = 'crew' ORDER BY c.character
            """, mediaId);

        if (row.Str("kind") == "movie")
        {
            var files = Db.Query("""
                SELECT f.*, pr.position, pr.completed, pr.plays AS file_plays
                FROM files f LEFT JOIN progress pr ON pr.file_id = f.id AND pr.user_id = ?
                WHERE f.media_id = ? ORDER BY f.size DESC
                """, Db.Me, mediaId);
            foreach (var f in files)
            {
                if (!includePaths) f.Remove("path");
                f["quality"] = MediaUtil.QualityBadge(f.Long("width"), f.Long("height"));
            }
            data["files"] = files;
            data["seasons"] = new List<Row>();
        }
        else
        {
            var seasons = new List<Row>();
            foreach (var s in Db.Query("SELECT DISTINCT f.season AS number FROM files f WHERE f.media_id = ? AND f.season IS NOT NULL ORDER BY f.season", mediaId))
            {
                var number = s.Long("number") ?? 0;
                var meta = Db.QueryOne("SELECT * FROM seasons WHERE show_id = ? AND number = ?", mediaId, number);
                var episodes = Db.Query("""
                    SELECT f.id AS file_id, f.filename, f.path, f.duration, f.width, f.height,
                           f.thumb, f.preview, f.missing, f.season, f.episode,
                           e.id AS episode_id, e.title, e.overview, e.air_date, e.rating, e.still,
                           pr.position, pr.completed
                    FROM files f
                    LEFT JOIN episodes e ON e.show_id = f.media_id AND e.season = f.season AND e.number = f.episode
                    LEFT JOIN progress pr ON pr.file_id = f.id AND pr.user_id = ?
                    WHERE f.media_id = ? AND f.season = ?
                    ORDER BY COALESCE(f.episode, 9999), f.filename
                    """, Db.Me, mediaId, number);
                foreach (var ep in episodes)
                {
                    ep["quality"] = MediaUtil.QualityBadge(ep.Long("width"), ep.Long("height"));
                    if (!ep.Truthy("title"))
                        ep["title"] = ep.Truthy("episode") ? $"Episode {ep.Long("episode")}"
                            : Path.GetFileNameWithoutExtension(ep.Str("filename"));
                    if (!includePaths) ep.Remove("path");
                }
                seasons.Add(new Row
                {
                    ["number"] = number,
                    ["name"] = meta != null && meta.Truthy("name") ? meta.Str("name") : number == 0 ? "Specials" : $"Season {number}",
                    ["overview"] = meta?.Str("overview") ?? "",
                    ["poster"] = meta?.Str("poster") ?? "",
                    ["air_date"] = meta?.Str("air_date") ?? "",
                    ["episodes"] = episodes,
                });
            }
            data["seasons"] = seasons;
            data["files"] = new List<Row>();
            data["episode_count"] = seasons.Sum(s => ((List<Row>)s["episodes"]!).Count);
        }
        try
        {
            data["artwork"] = row.Truthy("artwork")
                ? JsonSerializer.Deserialize<List<Dictionary<string, string>>>(row.Str("artwork")) ?? new()
                : new List<Dictionary<string, string>>();
        }
        catch { data["artwork"] = new List<Dictionary<string, string>>(); }
        data["similar"] = Similar(mediaId);
        data["resume"] = ResumeTarget(mediaId);
        return data;
    }

    /// <summary>Where Play should drop you: mid-file, or the next episode.</summary>
    public static Row? ResumeTarget(long mediaId)
    {
        var row = Db.QueryOne("""
            SELECT f.id AS file_id, f.season, f.episode, pr.position, pr.duration, pr.completed
            FROM files f JOIN progress pr ON pr.file_id = f.id AND pr.user_id = ?
            WHERE f.media_id = ? AND f.missing = 0 AND pr.completed = 0 AND pr.position > 0
            ORDER BY pr.updated_at DESC LIMIT 1
            """, Db.Me, mediaId);
        if (row != null && row.Double("duration") is > 0 && row.Double("position")!.Value / row.Double("duration")!.Value > Config.ResumeMin)
            return new Row
            {
                ["file_id"] = row.Long("file_id"), ["position"] = row.Double("position"),
                ["season"] = row.Get("season"), ["episode"] = row.Get("episode"), ["reason"] = "resume",
            };

        var watchedAny = Db.Scalar("""
            SELECT COUNT(*) AS n FROM files f JOIN progress pr ON pr.file_id = f.id AND pr.user_id = ?
            WHERE f.media_id = ? AND pr.completed = 1
            """, Db.Me, mediaId);
        var next = Db.QueryOne("""
            SELECT f.id AS file_id, f.season, f.episode FROM files f
            LEFT JOIN progress pr ON pr.file_id = f.id AND pr.user_id = ?
            WHERE f.media_id = ? AND f.missing = 0 AND COALESCE(pr.completed, 0) = 0
            ORDER BY COALESCE(f.season, 0), COALESCE(f.episode, 0), f.size DESC LIMIT 1
            """, Db.Me, mediaId);
        if (next != null)
            return new Row
            {
                ["file_id"] = next.Long("file_id"), ["position"] = 0L, ["season"] = next.Get("season"),
                ["episode"] = next.Get("episode"), ["reason"] = watchedAny > 0 ? "next" : "start",
            };
        var first = Db.QueryOne("""
            SELECT id AS file_id, season, episode FROM files WHERE media_id = ? AND missing = 0
            ORDER BY COALESCE(season, 0), COALESCE(episode, 0), size DESC LIMIT 1
            """, mediaId);
        if (first != null)
            return new Row
            {
                ["file_id"] = first.Long("file_id"), ["position"] = 0L, ["season"] = first.Get("season"),
                ["episode"] = first.Get("episode"), ["reason"] = "rewatch",
            };
        return null;
    }

    /// <summary>Shared genres first, then the same certificate and era as tiebreakers.</summary>
    public static List<Row> Similar(long mediaId, int limit = 18)
    {
        var row = Db.QueryOne("SELECT kind, year, certificate FROM media WHERE id = ?", mediaId);
        if (row == null) return new List<Row>();
        var genres = GenresFor(mediaId);
        if (genres.Count == 0)
            return Db.Query("""
                SELECT m.*, COALESCE(st.plays,0) AS plays FROM media m
                LEFT JOIN media_stats st ON st.media_id = m.id
                WHERE m.kind = ? AND m.id <> ? AND EXISTS (SELECT 1 FROM files f WHERE f.media_id = m.id)
                ORDER BY m.popularity DESC LIMIT ?
                """, row.Str("kind"), mediaId, limit).Select(Card).ToList();
        var placeholders = string.Join(",", genres.Select(_ => "?"));
        var args = new List<object?> { row.Str("certificate"), row.Long("year") ?? 0 };
        args.AddRange(genres);
        args.AddRange(new object?[] { row.Str("kind"), mediaId, limit });
        return Db.Query($"""
            SELECT m.*, COALESCE(st.plays,0) AS plays,
                   COUNT(DISTINCT g.id) AS shared,
                   (CASE WHEN m.certificate = ? AND m.certificate <> '' THEN 1 ELSE 0 END) AS same_cert,
                   ABS(COALESCE(m.year, 0) - ?) AS year_gap
            FROM media m
            JOIN media_genres mg ON mg.media_id = m.id
            JOIN genres g ON g.id = mg.genre_id AND g.name IN ({placeholders})
            LEFT JOIN media_stats st ON st.media_id = m.id
            WHERE m.kind = ? AND m.id <> ? AND EXISTS (SELECT 1 FROM files f WHERE f.media_id = m.id)
            GROUP BY m.id
            ORDER BY shared DESC, same_cert DESC,
                     CASE WHEN COALESCE(m.year,0) = 0 THEN 999 ELSE year_gap END ASC,
                     COALESCE(m.rating, 0) DESC
            LIMIT ?
            """, args.ToArray()).Select(Card).ToList();
    }

    // ----------------------------------------------------------------- people
    public static (List<Row> Items, long Total) People(string? q, string sort = "credits", int limit = 200, int offset = 0)
    {
        limit = Math.Min(limit, 1000);
        var order = sort switch
        {
            "name" => "p.name",
            "recent" => "MAX(m.added_at) DESC",
            _ => "credits DESC, p.name",
        };
        var args = new List<object?>();
        var where = "WHERE EXISTS (SELECT 1 FROM files f WHERE f.media_id = m.id)";
        if (!string.IsNullOrEmpty(q))
        {
            where += " AND p.name LIKE ?";
            args.Add($"%{q}%");
        }
        var items = Db.Query($"""
            SELECT p.id, p.name, p.photo, p.birthday, p.birthplace,
                   COUNT(DISTINCT m.id) AS credits,
                   SUM(CASE WHEN m.kind = 'movie' THEN 1 ELSE 0 END) AS movies,
                   SUM(CASE WHEN m.kind = 'show' THEN 1 ELSE 0 END) AS shows
            FROM people p
            JOIN credits c ON c.person_id = p.id AND c.role = 'cast'
            JOIN media m ON m.id = c.media_id
            {where}
            GROUP BY p.id ORDER BY {order} LIMIT ? OFFSET ?
            """, args.Concat(new object?[] { limit, offset }).ToArray());
        var total = Db.Scalar($"""
            SELECT COUNT(*) AS n FROM (
              SELECT p.id FROM people p
              JOIN credits c ON c.person_id = p.id AND c.role = 'cast'
              JOIN media m ON m.id = c.media_id {where} GROUP BY p.id)
            """, args.ToArray());
        return (items, total);
    }

    public static Row? PersonDetail(long personId)
    {
        var row = Db.QueryOne("SELECT * FROM people WHERE id = ?", personId);
        if (row == null) return null;
        var data = new Row(row);
        var credits = Db.Query("""
            SELECT m.id, m.kind, m.title, m.year, m.poster, m.rating, m.certificate, c.character, c.role
            FROM credits c JOIN media m ON m.id = c.media_id
            WHERE c.person_id = ? AND EXISTS (SELECT 1 FROM files f WHERE f.media_id = m.id)
            ORDER BY c.role, COALESCE(m.year, 0) DESC
            """, personId);
        foreach (var credit in credits)
            if (!credit.Truthy("poster"))
            {
                var f = Db.QueryOne("SELECT thumb FROM files WHERE media_id = ? AND thumb <> '' LIMIT 1", credit.Long("id"));
                credit["poster"] = f?.Str("thumb") ?? "";
            }
        data["credits"] = credits;
        data["top_genres"] = Db.Query("""
            SELECT g.name, COUNT(*) AS n FROM credits c
            JOIN media_genres mg ON mg.media_id = c.media_id
            JOIN genres g ON g.id = mg.genre_id
            WHERE c.person_id = ? GROUP BY g.name ORDER BY n DESC LIMIT 6
            """, personId).Select(g => g.Str("name")).ToList();
        return data;
    }

    // --------------------------------------------------------------- progress
    public static List<Row> ContinueWatching(string? kind, int limit = 24)
    {
        var args = new List<object?> { Db.Me, Config.ResumeMin, Config.CompleteAt };
        var clause = "";
        if (kind is "movie" or "show")
        {
            clause = "AND m.kind = ?";
            args.Add(kind);
        }
        args.Add(limit);
        var rows = Db.Query($"""
            SELECT f.id AS file_id, f.filename, f.thumb, f.preview, f.season, f.episode,
                   f.duration AS file_duration, f.width, f.height,
                   pr.position, pr.duration AS progress_duration, pr.updated_at,
                   m.id, m.kind, m.title, m.year, m.poster, m.backdrop, m.certificate, m.rating, m.overview,
                   e.title AS episode_title, e.still AS episode_still, e.overview AS episode_overview
            FROM progress pr
            JOIN files f ON f.id = pr.file_id
            JOIN media m ON m.id = f.media_id
            LEFT JOIN episodes e ON e.show_id = f.media_id AND e.season = f.season AND e.number = f.episode
            WHERE pr.user_id = ? AND pr.completed = 0 AND f.missing = 0
              AND pr.duration > 0
              AND pr.position / pr.duration > ?
              AND pr.position / pr.duration < ?
              {clause}
            ORDER BY pr.updated_at DESC LIMIT ?
            """, args.ToArray());
        foreach (var data in rows)
        {
            var position = data.Double("position") ?? 0;
            var duration = data.Double("progress_duration") ?? 1;
            data["genres"] = GenresFor(data.Long("id") ?? 0);
            data["progress"] = Math.Round(position / duration, 4);
            data["remaining"] = Math.Max(0, duration - position);
            data["quality"] = MediaUtil.QualityBadge(data.Long("width"), data.Long("height"));
            if (data.Str("kind") == "show")
            {
                var label = $"S{data.Long("season") ?? 0}·E{data.Long("episode") ?? 0}";
                data["subtitle"] = $"{label}  {data.Str("episode_title")}".Trim();
            }
            else data["subtitle"] = data.Long("year")?.ToString() ?? "";
            if (!data.Truthy("thumb"))
                data["thumb"] = data.Truthy("episode_still") ? data["episode_still"]
                    : data.Truthy("backdrop") ? data["backdrop"] : data["poster"];
        }
        return rows;
    }

    /// <summary>Record where playback is. Returns whether the file now counts as watched.</summary>
    public static bool SaveProgress(long fileId, double position, double duration)
    {
        if (Db.QueryOne("SELECT id FROM files WHERE id = ?", fileId) == null) return false;
        var completed = duration > 0 && position / duration >= Config.CompleteAt ? 1 : 0;
        Db.Execute("""
            INSERT INTO progress (user_id, file_id, position, duration, completed, plays, updated_at)
            VALUES (?, ?, ?, ?, ?, 0, ?)
            ON CONFLICT(user_id, file_id) DO UPDATE SET
              position = excluded.position, duration = excluded.duration,
              completed = excluded.completed, updated_at = excluded.updated_at
            """, Db.Me, fileId, position, duration, completed, Db.Now());
        RecordHistory(fileId, position, duration);
        Db.BumpUser();
        return completed == 1;
    }

    static void RecordHistory(long fileId, double position, double duration)
    {
        var row = Db.QueryOne("""
            SELECT f.media_id, f.season, f.episode, e.title AS episode_title
            FROM files f LEFT JOIN episodes e ON e.show_id = f.media_id AND e.season = f.season AND e.number = f.episode
            WHERE f.id = ?
            """, fileId);
        if (row == null) return;
        var recent = Db.QueryOne("""
            SELECT id FROM history WHERE user_id = ? AND file_id = ? AND watched_at > ? ORDER BY watched_at DESC LIMIT 1
            """, Db.Me, fileId, Db.Now() - 12 * 3600);
        if (recent != null)
        {
            Db.Execute("UPDATE history SET position = ?, duration = ?, watched_at = ? WHERE id = ?",
                       position, duration, Db.Now(), recent.Long("id"));
            return;
        }
        Db.Execute("""
            INSERT INTO history (user_id, file_id, media_id, season, episode, title, watched_at, position, duration)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, Db.Me, fileId, row.Get("media_id"), row.Get("season"), row.Get("episode"),
            row.Str("episode_title"), Db.Now(), position, duration);
    }

    public static void MarkStarted(long fileId)
    {
        Db.Execute("""
            INSERT INTO progress (user_id, file_id, position, duration, plays, updated_at) VALUES (?, ?, 0, 0, 1, ?)
            ON CONFLICT(user_id, file_id) DO UPDATE SET plays = plays + 1, updated_at = excluded.updated_at
            """, Db.Me, fileId, Db.Now());
        Db.BumpPlay(fileId);
        Db.BumpUser();
    }

    public static bool MarkWatched(long fileId, bool watched = true)
    {
        var row = Db.QueryOne("SELECT duration FROM files WHERE id = ?", fileId);
        if (row == null) return false;
        var duration = row.Double("duration") ?? 0;
        Db.Execute("""
            INSERT INTO progress (user_id, file_id, position, duration, completed, updated_at) VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(user_id, file_id) DO UPDATE SET position = excluded.position,
              duration = excluded.duration, completed = excluded.completed, updated_at = excluded.updated_at
            """, Db.Me, fileId, watched ? duration : 0, duration, watched ? 1 : 0, Db.Now());
        Db.BumpUser();
        return true;
    }

    /// <summary>Clears a file's progress entirely, so it counts as unwatched again.</summary>
    public static void DropFromContinue(long fileId)
    {
        Db.Execute("DELETE FROM progress WHERE user_id = ? AND file_id = ?", Db.Me, fileId);
        Db.BumpUser();
    }

    public static List<Row> History(int limit = 200) => Db.Query("""
        SELECT h.*, m.title AS media_title, m.kind, m.poster, m.backdrop, f.thumb
        FROM history h
        LEFT JOIN media m ON m.id = h.media_id
        LEFT JOIN files f ON f.id = h.file_id
        WHERE h.user_id = ? ORDER BY h.watched_at DESC LIMIT ?
        """, Db.Me, limit);

    // ------------------------------------------------------------------ files
    public static Row? FileDetail(long fileId, bool includePaths = true)
    {
        var row = Db.QueryOne("""
            SELECT f.*, pr.position, pr.completed, m.title, m.kind, m.id AS media_id
            FROM files f LEFT JOIN progress pr ON pr.file_id = f.id AND pr.user_id = ?
            LEFT JOIN media m ON m.id = f.media_id WHERE f.id = ?
            """, Db.Me, fileId);
        if (row == null) return null;
        var data = new Row(row);
        data["quality"] = MediaUtil.QualityBadge(row.Long("width"), row.Long("height"));
        data["exists"] = File.Exists(row.Str("path"));
        if (!includePaths) data.Remove("path");
        if (row.Get("season") != null && row.Get("episode") != null)
        {
            var ep = Db.QueryOne("SELECT * FROM episodes WHERE show_id = ? AND season = ? AND number = ?",
                                 row.Get("media_id"), row.Get("season"), row.Get("episode"));
            data["episode"] = ep;
        }
        return data;
    }

    public static string? FilePath(long fileId) =>
        Db.QueryOne("SELECT path FROM files WHERE id = ?", fileId)?.Str("path");

    // ------------------------------------------------------ what plays next
    static readonly HashSet<string> Roman = new() { "i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix", "x" };
    static readonly HashSet<string> SequelWords = new() { "part", "chapter", "volume", "vol", "episode" };
    static readonly HashSet<string> StopWords = new() { "the", "a", "an", "of", "and", "in", "on", "at", "to" };
    static readonly Regex NotWord = new("[^a-z0-9 ]+", RegexOptions.CultureInvariant);

    /// <summary>A title reduced to its identifying words, sequel markers removed.</summary>
    public static List<string> TitleWords(string title)
    {
        var text = NotWord.Replace(title.ToLowerInvariant(), " ");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !StopWords.Contains(w)).ToList();
        while (words.Count > 1 && (words[^1].All(char.IsDigit) || Roman.Contains(words[^1]) || SequelWords.Contains(words[^1])))
            words.RemoveAt(words.Count - 1);
        if (words.Count > 0) return words;
        var t = text.Trim();
        return new List<string> { t.Length > 0 ? t : "?" };
    }

    static double Relatedness(List<string> baseWords, HashSet<string> baseGenres, string otherTitle,
                              HashSet<string> otherGenres, double? rating, double popularity)
    {
        var words = TitleWords(otherTitle);
        var lead = 0;
        for (var i = 0; i < Math.Min(baseWords.Count, words.Count); i++)
        {
            if (baseWords[i] == words[i]) lead++;
            else break;
        }
        var sharedWords = baseWords.ToHashSet().Intersect(words).Count();
        var sharedGenres = baseGenres.Intersect(otherGenres).Count();
        return lead * 40 + sharedWords * 10 + sharedGenres * 8 + (rating ?? 0) * 1.5 + Math.Min(popularity, 60) * 0.05;
    }

    static Row? BestFile(long mediaId) => Db.QueryOne("""
        SELECT f.id AS file_id, f.filename, f.duration, f.width, f.height, f.thumb, pr.position, pr.completed
        FROM files f LEFT JOIN progress pr ON pr.file_id = f.id AND pr.user_id = ?
        WHERE f.media_id = ? AND f.missing = 0 ORDER BY f.size DESC LIMIT 1
        """, Db.Me, mediaId);

    /// <summary>One title must be a complete prefix of the other; two shared words are not enough.</summary>
    public static bool SameSeries(List<string> baseWords, string otherTitle)
    {
        var words = TitleWords(otherTitle);
        if (baseWords.Count == 0 || words.Count == 0) return false;
        var shorter = Math.Min(baseWords.Count, words.Count);
        for (var i = 0; i < shorter; i++)
            if (baseWords[i] != words[i]) return false;
        if (shorter == 1 && (baseWords[0].Length < 4 || baseWords[0].All(char.IsDigit))) return false;
        return true;
    }

    static List<Row> MovieQueue(long mediaId, int limit = 20)
    {
        var baseRow = Db.QueryOne("SELECT title, year, collection_id FROM media WHERE id = ?", mediaId);
        if (baseRow == null) return new List<Row>();
        var baseWords = TitleWords(baseRow.Str("title"));
        var baseGenres = GenresFor(mediaId).Select(g => g.ToLowerInvariant()).ToHashSet();
        var collection = baseRow.Long("collection_id");

        var rows = Db.Query("""
            SELECT m.id, m.title, m.year, m.overview, m.certificate, m.rating, m.popularity, m.poster, m.backdrop,
                   m.runtime, m.release_date, m.collection_id, m.collection_name
            FROM media m
            WHERE m.kind = 'movie' AND m.id <> ?
              AND EXISTS (SELECT 1 FROM files f WHERE f.media_id = m.id AND f.missing = 0)
            """, mediaId);
        var franchise = new List<(int Rank, string Key, Row Row)>();
        var others = new List<(double Score, Row Row)>();
        foreach (var row in rows)
        {
            var genres = GenresFor(row.Long("id") ?? 0).Select(g => g.ToLowerInvariant()).ToHashSet();
            var score = Relatedness(baseWords, baseGenres, row.Str("title"), genres, row.Double("rating"),
                                    row.Double("popularity") ?? 0);
            string reason;
            if (collection is > 0 && row.Long("collection_id") == collection) reason = "collection";
            else if (collection is > 0 && row.Long("collection_id") is > 0) reason = "";
            else if (SameSeries(baseWords, row.Str("title"))) reason = "title";
            else reason = "";
            if (reason.Length > 0)
            {
                var key = row.Truthy("release_date") ? row.Str("release_date")
                    : row.Long("year") is long y ? $"{y}-01-01" : "9999";
                franchise.Add((reason == "collection" ? 0 : 1, key, row));
            }
            else if (score > 8) others.Add((score, row));
        }
        franchise = franchise.OrderBy(x => x.Rank).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        others = others.OrderByDescending(x => x.Score).ToList();
        var franchiseIds = franchise.Select(x => x.Row.Long("id")).ToHashSet();
        var ordered = franchise.Select(x => x.Row).Concat(others.Select(x => x.Row)).ToList();

        var items = new List<Row>();
        foreach (var row in ordered.Take(limit))
        {
            var best = BestFile(row.Long("id") ?? 0);
            if (best == null) continue;
            var duration = best.Double("duration");
            var position = best.Double("position");
            var inFranchise = franchiseIds.Contains(row.Long("id"));
            items.Add(new Row
            {
                ["file_id"] = best.Long("file_id"), ["media_id"] = row.Long("id"), ["filename"] = best.Str("filename"),
                ["season"] = null, ["episode"] = null, ["label"] = row.Long("year")?.ToString() ?? "",
                ["title"] = row.Str("title"), ["overview"] = row.Str("overview"), ["air_date"] = "",
                ["rating"] = row.Get("rating"), ["duration"] = duration,
                ["quality"] = MediaUtil.QualityBadge(best.Long("width"), best.Long("height")),
                ["still"] = best.Truthy("thumb") ? best.Str("thumb") : row.Truthy("backdrop") ? row.Str("backdrop") : row.Str("poster"),
                ["thumb"] = best.Str("thumb"), ["position"] = position, ["completed"] = best.Get("completed"),
                ["progress"] = position is > 0 && duration is > 0 ? position.Value / duration.Value : 0.0,
                ["missing"] = 0L, ["reason"] = inFranchise ? "franchise" : "genre",
                ["series"] = inFranchise ? row.Str("collection_name") : "",
            });
        }
        return items;
    }

    public sealed class Queue
    {
        public Row Media { get; set; } = new();
        public string Kind { get; set; } = "movie";
        public List<Row> Items { get; set; } = new();
        public int Index { get; set; }
        public long? Prev { get; set; }
        public long? Next { get; set; }

        public Dictionary<string, object?> ToJson() => new()
        {
            ["media"] = Media, ["kind"] = Kind, ["items"] = Items, ["index"] = Index, ["prev"] = Prev, ["next"] = Next,
        };
    }

    /// <summary>What plays next: the episode list for a series, recommendations for a film.</summary>
    public static Queue? QueueFor(long fileId)
    {
        var row = Db.QueryOne("SELECT f.id, f.media_id, m.kind FROM files f JOIN media m ON m.id = f.media_id WHERE f.id = ?", fileId);
        if (row == null) return null;
        var mediaId = row.Long("media_id") ?? 0;
        var m = Db.QueryOne("SELECT * FROM media WHERE id = ?", mediaId)!;
        var media = new Row
        {
            ["id"] = m.Long("id"), ["kind"] = m.Str("kind"), ["title"] = m.Str("title"), ["year"] = m.Get("year"),
            ["overview"] = m.Str("overview"), ["tagline"] = m.Str("tagline"), ["certificate"] = m.Str("certificate"),
            ["rating"] = m.Get("rating"), ["runtime"] = m.Get("runtime"), ["release_date"] = m.Str("release_date"),
            ["poster"] = m.Str("poster"), ["backdrop"] = m.Str("backdrop"), ["genres"] = GenresFor(mediaId),
        };
        var items = new List<Row>();
        if (m.Str("kind") == "show")
        {
            foreach (var r in Db.Query("""
                         SELECT f.id AS file_id, f.filename, f.season, f.episode, f.duration,
                                f.width, f.height, f.thumb, f.missing,
                                e.title, e.overview, e.air_date, e.rating, e.still,
                                pr.position, pr.completed
                         FROM files f
                         LEFT JOIN episodes e ON e.show_id = f.media_id AND e.season = f.season AND e.number = f.episode
                         LEFT JOIN progress pr ON pr.file_id = f.id AND pr.user_id = ?
                         WHERE f.media_id = ? AND f.missing = 0
                         ORDER BY COALESCE(f.season, 0), COALESCE(f.episode, 0), f.filename
                         """, Db.Me, mediaId))
            {
                r["media_id"] = mediaId;
                r["quality"] = MediaUtil.QualityBadge(r.Long("width"), r.Long("height"));
                r["label"] = r.Get("season") != null ? $"S{r.Long("season") ?? 0:00}E{r.Long("episode") ?? 0:00}" : "";
                if (!r.Truthy("title"))
                    r["title"] = r.Truthy("episode") ? $"Episode {r.Long("episode")}" : Path.GetFileNameWithoutExtension(r.Str("filename"));
                var pos = r.Double("position");
                var dur = r.Double("duration");
                r["progress"] = pos is > 0 && dur is > 0 ? pos.Value / dur.Value : 0.0;
                r["reason"] = "episode";
                r["series"] = "";
                items.Add(r);
            }
        }
        else
        {
            var current = BestFile(mediaId);
            if (current != null)
            {
                var duration = current.Double("duration");
                var position = current.Double("position");
                items.Add(new Row
                {
                    ["file_id"] = current.Long("file_id"), ["media_id"] = mediaId, ["filename"] = current.Str("filename"),
                    ["season"] = null, ["episode"] = null, ["label"] = m.Long("year")?.ToString() ?? "",
                    ["title"] = m.Str("title"), ["overview"] = m.Str("overview"), ["air_date"] = m.Str("release_date"),
                    ["rating"] = m.Get("rating"), ["duration"] = duration,
                    ["quality"] = MediaUtil.QualityBadge(current.Long("width"), current.Long("height")),
                    ["still"] = current.Truthy("thumb") ? current.Str("thumb") : m.Str("backdrop"),
                    ["thumb"] = current.Str("thumb"), ["position"] = position, ["completed"] = current.Get("completed"),
                    ["progress"] = position is > 0 && duration is > 0 ? position.Value / duration.Value : 0.0,
                    ["missing"] = 0L, ["reason"] = "current", ["series"] = m.Str("collection_name"),
                });
            }
            items.AddRange(MovieQueue(mediaId));
        }
        var index = items.FindIndex(i => i.Long("file_id") == fileId);
        if (index < 0) index = 0;
        return new Queue
        {
            Media = media, Kind = m.Str("kind"), Items = items, Index = index,
            Prev = index > 0 ? items[index - 1].Long("file_id") : null,
            Next = index + 1 < items.Count ? items[index + 1].Long("file_id") : null,
        };
    }

    // ------------------------------------------------------------------ stats
    public static Row Stats() => Db.QueryOne("""
        SELECT
          (SELECT COUNT(*) FROM media WHERE kind = 'movie') AS movies,
          (SELECT COUNT(*) FROM media WHERE kind = 'show') AS shows,
          (SELECT COUNT(*) FROM files WHERE missing = 0) AS files,
          (SELECT COUNT(*) FROM files WHERE missing = 1) AS missing,
          (SELECT COUNT(*) FROM people) AS people,
          (SELECT COALESCE(SUM(size), 0) FROM files WHERE missing = 0) AS bytes,
          (SELECT COALESCE(SUM(duration), 0) FROM files WHERE missing = 0) AS seconds,
          (SELECT COUNT(*) FROM media WHERE scraped_at IS NULL) AS unscraped
        """)!;

    public static List<Row> Collections() => Db.Query("""
        SELECT m.collection_name AS name, m.collection_id AS id, COUNT(*) AS films
        FROM media m
        WHERE m.collection_id IS NOT NULL AND m.collection_name <> ''
          AND EXISTS (SELECT 1 FROM files f WHERE f.media_id = m.id)
        GROUP BY m.collection_id ORDER BY films DESC, name
        """);
}
