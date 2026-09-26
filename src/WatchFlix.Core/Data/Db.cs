namespace WatchFlix.Core.Data;

/// <summary>
/// The library database. Same file and same schema as the Python build, so an
/// existing ~/.watchflix/library.db opens as it is — titles, artwork, progress.
/// </summary>
public static class Db
{
    static SqliteDb? _conn;
    static readonly object Gate = new();

    public static SqliteDb Conn => _conn ?? throw new InvalidOperationException("Call Db.Init first.");

    // The single viewer. The Python build kept progress per account; this one is
    // a personal app, so everything lives under the host's id, 0.
    public const long Me = 0;

    const string Schema = """
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;

CREATE TABLE IF NOT EXISTS media (
    id          INTEGER PRIMARY KEY,
    kind        TEXT NOT NULL CHECK (kind IN ('movie','show')),
    title       TEXT NOT NULL,
    sort_title  TEXT NOT NULL DEFAULT '',
    year        INTEGER,
    overview    TEXT NOT NULL DEFAULT '',
    tagline     TEXT NOT NULL DEFAULT '',
    certificate TEXT NOT NULL DEFAULT '',
    rating      REAL,
    votes       INTEGER,
    popularity  REAL NOT NULL DEFAULT 0,
    runtime     INTEGER,
    release_date TEXT NOT NULL DEFAULT '',
    status      TEXT NOT NULL DEFAULT '',
    poster      TEXT NOT NULL DEFAULT '',
    backdrop    TEXT NOT NULL DEFAULT '',
    trailer     TEXT NOT NULL DEFAULT '',
    tmdb_id     INTEGER,
    imdb_id     TEXT NOT NULL DEFAULT '',
    tvmaze_id   INTEGER,
    collection_id   INTEGER,
    collection_name TEXT NOT NULL DEFAULT '',
    series_name TEXT NOT NULL DEFAULT '',
    artwork     TEXT NOT NULL DEFAULT '',
    scraped_at  INTEGER,
    scrape_source TEXT NOT NULL DEFAULT '',
    locked      INTEGER NOT NULL DEFAULT 0,
    added_at    INTEGER NOT NULL,
    updated_at  INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_media_kind ON media(kind);
CREATE INDEX IF NOT EXISTS idx_media_sort ON media(sort_title);

CREATE TABLE IF NOT EXISTS genres (
    id   INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE
);

CREATE TABLE IF NOT EXISTS media_genres (
    media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE,
    genre_id INTEGER NOT NULL REFERENCES genres(id) ON DELETE CASCADE,
    PRIMARY KEY (media_id, genre_id)
);
CREATE INDEX IF NOT EXISTS idx_mg_genre ON media_genres(genre_id);

CREATE TABLE IF NOT EXISTS people (
    id         INTEGER PRIMARY KEY,
    name       TEXT NOT NULL UNIQUE COLLATE NOCASE,
    bio        TEXT NOT NULL DEFAULT '',
    birthday   TEXT NOT NULL DEFAULT '',
    deathday   TEXT NOT NULL DEFAULT '',
    birthplace TEXT NOT NULL DEFAULT '',
    photo      TEXT NOT NULL DEFAULT '',
    known_for  TEXT NOT NULL DEFAULT '',
    tmdb_id    INTEGER,
    imdb_id    TEXT NOT NULL DEFAULT '',
    scraped_at INTEGER,
    locked     INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS credits (
    media_id  INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE,
    person_id INTEGER NOT NULL REFERENCES people(id) ON DELETE CASCADE,
    role      TEXT NOT NULL DEFAULT 'cast',
    character TEXT NOT NULL DEFAULT '',
    ord       INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (media_id, person_id, role, character)
);
CREATE INDEX IF NOT EXISTS idx_credits_person ON credits(person_id);

CREATE TABLE IF NOT EXISTS seasons (
    id        INTEGER PRIMARY KEY,
    show_id   INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE,
    number    INTEGER NOT NULL,
    name      TEXT NOT NULL DEFAULT '',
    overview  TEXT NOT NULL DEFAULT '',
    poster    TEXT NOT NULL DEFAULT '',
    air_date  TEXT NOT NULL DEFAULT '',
    UNIQUE (show_id, number)
);

CREATE TABLE IF NOT EXISTS episodes (
    id         INTEGER PRIMARY KEY,
    show_id    INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE,
    season     INTEGER NOT NULL,
    number     INTEGER NOT NULL,
    title      TEXT NOT NULL DEFAULT '',
    overview   TEXT NOT NULL DEFAULT '',
    air_date   TEXT NOT NULL DEFAULT '',
    rating     REAL,
    runtime    INTEGER,
    still      TEXT NOT NULL DEFAULT '',
    UNIQUE (show_id, season, number)
);
CREATE INDEX IF NOT EXISTS idx_ep_show ON episodes(show_id, season, number);

CREATE TABLE IF NOT EXISTS files (
    id         INTEGER PRIMARY KEY,
    path       TEXT NOT NULL UNIQUE,
    filename   TEXT NOT NULL,
    size       INTEGER NOT NULL DEFAULT 0,
    mtime      REAL NOT NULL DEFAULT 0,
    duration   REAL,
    width      INTEGER,
    height     INTEGER,
    vcodec     TEXT NOT NULL DEFAULT '',
    acodec     TEXT NOT NULL DEFAULT '',
    media_id   INTEGER REFERENCES media(id) ON DELETE SET NULL,
    episode_id INTEGER REFERENCES episodes(id) ON DELETE SET NULL,
    season     INTEGER,
    episode    INTEGER,
    thumb      TEXT NOT NULL DEFAULT '',
    preview    TEXT NOT NULL DEFAULT '',
    missing    INTEGER NOT NULL DEFAULT 0,
    parsed_v   INTEGER NOT NULL DEFAULT 0,
    added_at   INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_files_media ON files(media_id);
CREATE INDEX IF NOT EXISTS idx_files_ep ON files(episode_id);

CREATE TABLE IF NOT EXISTS progress (
    user_id    INTEGER NOT NULL DEFAULT 0,
    file_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    position   REAL NOT NULL DEFAULT 0,
    duration   REAL NOT NULL DEFAULT 0,
    completed  INTEGER NOT NULL DEFAULT 0,
    plays      INTEGER NOT NULL DEFAULT 0,
    updated_at INTEGER NOT NULL,
    PRIMARY KEY (user_id, file_id)
);
CREATE INDEX IF NOT EXISTS idx_progress_updated ON progress(updated_at DESC);

CREATE TABLE IF NOT EXISTS media_stats (
    media_id   INTEGER PRIMARY KEY REFERENCES media(id) ON DELETE CASCADE,
    plays      INTEGER NOT NULL DEFAULT 0,
    last_played INTEGER
);

CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS history (
    id         INTEGER PRIMARY KEY,
    user_id    INTEGER NOT NULL,
    file_id    INTEGER REFERENCES files(id) ON DELETE SET NULL,
    media_id   INTEGER REFERENCES media(id) ON DELETE SET NULL,
    season     INTEGER,
    episode    INTEGER,
    title      TEXT NOT NULL DEFAULT '',
    watched_at INTEGER NOT NULL,
    position   REAL NOT NULL DEFAULT 0,
    duration   REAL NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_history_user ON history(user_id, watched_at DESC);

CREATE TABLE IF NOT EXISTS device_blocks (
    address    TEXT PRIMARY KEY,
    blocked    INTEGER NOT NULL DEFAULT 0,
    name       TEXT NOT NULL DEFAULT '',
    updated_at INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS provider_health (
    name        TEXT PRIMARY KEY,
    failures    INTEGER NOT NULL DEFAULT 0,
    down_until  INTEGER NOT NULL DEFAULT 0,
    last_error  TEXT NOT NULL DEFAULT '',
    last_ok     INTEGER
);
""";

    static readonly (string Table, string Column, string Decl)[] Migrations =
    {
        ("media", "collection_id", "INTEGER"),
        ("media", "collection_name", "TEXT NOT NULL DEFAULT ''"),
        ("media", "series_name", "TEXT NOT NULL DEFAULT ''"),
        ("files", "parsed_v", "INTEGER NOT NULL DEFAULT 0"),
        ("media", "artwork", "TEXT NOT NULL DEFAULT ''"),
    };

    public static void Init(string? path = null)
    {
        lock (Gate)
        {
            if (_conn != null) return;
            Config.EnsureFolders();
            var version = SqliteDb.LibraryVersion;
            // UPSERT arrived in 3.24. Windows 10's own copy has had it for years,
            // but a very old install would otherwise fail on the first write.
            if (version < 3024000)
                throw new SqliteException(
                    $"This computer's SQLite is too old ({version}). Update Windows, or put " +
                    "e_sqlite3.dll beside WatchFlix.exe.");
            _conn = new SqliteDb(path ?? Config.DbPath);
            _conn.Exec(Schema);
            foreach (var (table, column, decl) in Migrations)
            {
                if (!TableExists(table)) continue;
                var existing = Query($"PRAGMA table_info({table})").Select(r => r.Str("name")).ToHashSet();
                if (!existing.Contains(column))
                    Execute($"ALTER TABLE {table} ADD COLUMN {column} {decl}");
            }
            Execute("CREATE INDEX IF NOT EXISTS idx_media_collection ON media(collection_id)");

            // A database from before accounts keyed progress by file alone.
            var progressColumns = Query("PRAGMA table_info(progress)").Select(r => r.Str("name")).ToHashSet();
            if (!progressColumns.Contains("user_id"))
            {
                Execute("""
                    CREATE TABLE progress_new (
                        user_id    INTEGER NOT NULL DEFAULT 0,
                        file_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        position   REAL NOT NULL DEFAULT 0,
                        duration   REAL NOT NULL DEFAULT 0,
                        completed  INTEGER NOT NULL DEFAULT 0,
                        plays      INTEGER NOT NULL DEFAULT 0,
                        updated_at INTEGER NOT NULL,
                        PRIMARY KEY (user_id, file_id))
                    """);
                Execute("""
                    INSERT OR IGNORE INTO progress_new
                        (user_id, file_id, position, duration, completed, plays, updated_at)
                    SELECT 0, file_id, position, duration, completed, plays, updated_at FROM progress
                    """);
                Execute("DROP TABLE progress");
                Execute("ALTER TABLE progress_new RENAME TO progress");
            }
            Execute("CREATE INDEX IF NOT EXISTS idx_progress_user ON progress(user_id, updated_at DESC)");
            AdoptAccountsProgress();
        }
    }

    static bool TableExists(string name) =>
        QueryOne("SELECT 1 AS x FROM sqlite_master WHERE type = 'table' AND name = ?", name) != null;

    /// <summary>
    /// The Python build kept a place in each file per account, and the host's own
    /// viewing sat under the admin account once one existed. This build has one
    /// viewer, so once, on first start, the most recent place in each file —
    /// whoever it belonged to — becomes that viewer's. Nothing is deleted; the old
    /// rows stay where they were.
    /// </summary>
    static void AdoptAccountsProgress()
    {
        if (QueryOne("SELECT value FROM meta WHERE key = 'single_user'") != null) return;
        Execute("""
            INSERT INTO progress (user_id, file_id, position, duration, completed, plays, updated_at)
            SELECT 0, p.file_id, p.position, p.duration, p.completed, p.plays, p.updated_at
            FROM progress p
            WHERE p.user_id <> 0
              AND p.updated_at = (SELECT MAX(q.updated_at) FROM progress q WHERE q.file_id = p.file_id)
            ON CONFLICT(user_id, file_id) DO UPDATE SET
              position = excluded.position, duration = excluded.duration,
              completed = excluded.completed, plays = MAX(progress.plays, excluded.plays),
              updated_at = excluded.updated_at
            WHERE excluded.updated_at > progress.updated_at
            """);
        Execute("INSERT OR REPLACE INTO meta (key, value) VALUES ('single_user', '1')");
    }

    // ------------------------------------------------------------------ access
    public static List<Row> Query(string sql, params object?[] args) => Conn.Query(sql, args);
    public static Row? QueryOne(string sql, params object?[] args) => Conn.QueryOne(sql, args);
    public static int Execute(string sql, params object?[] args) => Conn.Execute(sql, args);
    public static long Insert(string sql, params object?[] args) => Conn.Insert(sql, args);

    public static long Scalar(string sql, params object?[] args) =>
        QueryOne(sql, args) is { } row && row.Count > 0 && row.Values.First() is { } v
            ? v switch { long l => l, double d => (long)d, _ => 0 }
            : 0;

    public static long Now() => Config.Now();

    public static long GenreId(string name)
    {
        name = name.Trim();
        var row = QueryOne("SELECT id FROM genres WHERE name = ?", name);
        if (row != null) return row.Long("id") ?? 0;
        return Insert("INSERT INTO genres (name) VALUES (?)", name);
    }

    public static void SetGenres(long mediaId, IEnumerable<string> names)
    {
        Execute("DELETE FROM media_genres WHERE media_id = ?", mediaId);
        var seen = new HashSet<string>();
        foreach (var name in names)
        {
            var key = name.Trim().ToLowerInvariant();
            if (key.Length == 0 || !seen.Add(key)) continue;
            Execute("INSERT OR IGNORE INTO media_genres (media_id, genre_id) VALUES (?, ?)",
                    mediaId, GenreId(name));
        }
    }

    public static long PersonId(string name)
    {
        name = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var row = QueryOne("SELECT id FROM people WHERE name = ?", name);
        if (row != null) return row.Long("id") ?? 0;
        return Insert("INSERT INTO people (name) VALUES (?)", name);
    }

    public static void BumpPlay(long fileId)
    {
        var row = QueryOne("SELECT media_id FROM files WHERE id = ?", fileId);
        if (row?.Long("media_id") is not long mediaId) return;
        Execute("""
            INSERT INTO media_stats (media_id, plays, last_played) VALUES (?, 1, ?)
            ON CONFLICT(media_id) DO UPDATE SET plays = plays + 1, last_played = ?
            """, mediaId, Now(), Now());
    }

    // --------------------------------------------------------------- versions
    // Two counters: one moves when the library changes, one when this
    // viewer's progress does.
    public static event Action? LibraryChanged;
    public static event Action? ProgressChanged;

    public static long LibraryVersion() => ReadCounter("library_v");
    public static long UserVersion() => ReadCounter("host_v");

    static long ReadCounter(string key)
    {
        var row = QueryOne("SELECT value FROM meta WHERE key = ?", key);
        return row != null && long.TryParse(row.Str("value"), out var v) ? v : 1;
    }

    public static void BumpLibrary()
    {
        Execute("""
            INSERT INTO meta (key, value) VALUES ('library_v', '2')
            ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
            """);
        LibraryChanged?.Invoke();
    }

    public static void BumpUser()
    {
        Execute("""
            INSERT INTO meta (key, value) VALUES ('host_v', '2')
            ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
            """);
        ProgressChanged?.Invoke();
    }
}
