using System.Text.Json;
using System.Text.RegularExpressions;
using WatchFlix.Core.Data;
using WatchFlix.Core.Providers;

namespace WatchFlix.Core.Library;

public sealed record ScrapeOutcome(bool Ok, List<string> Log, string Source = "", string Title = "");

/// <summary>
/// Asks every provider in turn and merges what comes back. The first usable
/// answer sets the record's identity; later ones only fill what it left empty.
/// A provider that keeps failing is rested for five minutes.
/// </summary>
public static class Metadata
{
    static readonly object ImageGate = new();

    // ----------------------------------------------------------------- health
    static Row Health(string name)
    {
        var row = Db.QueryOne("SELECT * FROM provider_health WHERE name = ?", name);
        if (row != null) return row;
        Db.Execute("INSERT OR IGNORE INTO provider_health (name) VALUES (?)", name);
        return new Row { ["name"] = name, ["failures"] = 0L, ["down_until"] = 0L, ["last_error"] = "", ["last_ok"] = null };
    }

    static void MarkOk(string name) => Db.Execute("""
        INSERT INTO provider_health (name, failures, down_until, last_error, last_ok) VALUES (?, 0, 0, '', ?)
        ON CONFLICT(name) DO UPDATE SET failures = 0, down_until = 0, last_error = '', last_ok = ?
        """, name, Db.Now(), Db.Now());

    static void MarkFail(string name, string error)
    {
        var failures = (Health(name).Long("failures") ?? 0) + 1;
        long downUntil = failures >= Config.ProviderFailuresBeforeCooldown ? Db.Now() + Config.ProviderCooldownSeconds : 0;
        var text = error.Length > 300 ? error[..300] : error;
        Db.Execute("""
            INSERT INTO provider_health (name, failures, down_until, last_error) VALUES (?, ?, ?, ?)
            ON CONFLICT(name) DO UPDATE SET failures = ?, down_until = ?, last_error = ?
            """, name, failures, downUntil, text, failures, downUntil, text);
    }

    static List<string> Order()
    {
        var order = Settings.List("provider_order");
        return order.Count > 0 ? order : Registry.All.Keys.ToList();
    }

    /// <summary>Configured providers that support this kind and are not resting.</summary>
    public static List<Provider> ActiveProviders(string kind)
    {
        var list = new List<Provider>();
        foreach (var name in Order())
        {
            if (!Registry.All.TryGetValue(name, out var provider)) continue;
            if (!provider.Supports.Contains(kind)) continue;
            if (provider.NeedsKey && !provider.Configured()) continue;
            if ((Health(name).Long("down_until") ?? 0) > Db.Now()) continue;
            list.Add(provider);
        }
        return list;
    }

    public sealed record ProviderState(string Name, string[] Supports, bool NeedsKey, bool Configured,
                                       string State, long Failures, long DownFor, string LastError);

    public static List<ProviderState> ProviderStatus()
    {
        var list = new List<ProviderState>();
        foreach (var name in Order())
        {
            if (!Registry.All.TryGetValue(name, out var provider)) continue;
            var health = Health(name);
            var cooling = (health.Long("down_until") ?? 0) > Db.Now();
            var configured = provider.Configured();
            list.Add(new ProviderState(name, provider.Supports, provider.NeedsKey, configured,
                !configured ? "needs key" : cooling ? "cooling down" : "ready",
                health.Long("failures") ?? 0, Math.Max(0, (health.Long("down_until") ?? 0) - Db.Now()),
                health.Str("last_error")));
        }
        return list;
    }

    public static void ResetProviderHealth() =>
        Db.Execute("UPDATE provider_health SET failures = 0, down_until = 0, last_error = ''");

    // --------------------------------------------------------- search and merge
    public static Candidate? BestMatch(List<Candidate> candidates, string title, int? year)
    {
        if (candidates.Count == 0) return null;
        Candidate? best = null;
        var bestScore = -1.0;
        foreach (var cand in candidates)
        {
            var names = new List<string> { cand.Title };
            names.AddRange(cand.AltTitles);
            var usable = names.Where(n => !string.IsNullOrEmpty(n)).ToList();
            var score = usable.Count > 0 ? usable.Max(n => Similarity.TitleSimilarity(title, n)) * 100 : 0;
            if (year != null && cand.Year != null)
            {
                var gap = Math.Abs(year.Value - cand.Year.Value);
                score += gap == 0 ? 25 : gap == 1 ? 10 : -12 * Math.Min(gap, 5);
            }
            else if (year != null && cand.Year == null) score -= 3;
            score += Math.Min(cand.Popularity, 40) * 0.25;
            if (score > bestScore)
            {
                best = cand;
                bestScore = score;
            }
        }
        return bestScore > 35 ? best : candidates[0];
    }

    /// <summary>First result wins on identity, later ones fill the gaps.</summary>
    public static MetaResult? Merge(List<MetaResult?> input)
    {
        var results = input.Where(r => r != null).Select(r => r!).ToList();
        if (results.Count == 0) return null;
        var b = results[0];
        b.Artwork = results.Where(r => r.Poster.Length > 0 || r.Backdrop.Length > 0)
            .Select(r => new Dictionary<string, string> { ["source"] = r.Source, ["poster"] = r.Poster, ["backdrop"] = r.Backdrop })
            .ToList();
        var sources = new List<string> { b.Source };
        foreach (var o in results.Skip(1))
        {
            sources.Add(o.Source);
            if (b.Overview.Length == 0) b.Overview = o.Overview;
            if (b.Tagline.Length == 0) b.Tagline = o.Tagline;
            if (b.Certificate.Length == 0) b.Certificate = o.Certificate;
            if (b.ReleaseDate.Length == 0) b.ReleaseDate = o.ReleaseDate;
            if (b.Status.Length == 0) b.Status = o.Status;
            if (b.Poster.Length == 0) b.Poster = o.Poster;
            if (b.Backdrop.Length == 0) b.Backdrop = o.Backdrop;
            if (b.Trailer.Length == 0) b.Trailer = o.Trailer;
            if (b.ImdbId.Length == 0) b.ImdbId = o.ImdbId;
            if (b.CollectionName.Length == 0) b.CollectionName = o.CollectionName;
            if (b.Rating is null or 0 && o.Rating is > 0) b.Rating = o.Rating;
            if (b.Votes is null or 0 && o.Votes is > 0) b.Votes = o.Votes;
            if (b.Runtime is null or 0 && o.Runtime is > 0) b.Runtime = o.Runtime;
            if (b.Year is null or 0 && o.Year is > 0) b.Year = o.Year;
            if (b.TmdbId is null or 0 && o.TmdbId is > 0) b.TmdbId = o.TmdbId;
            if (b.TvmazeId is null or 0 && o.TvmazeId is > 0) b.TvmazeId = o.TvmazeId;
            if (b.CollectionId is null or 0 && o.CollectionId is > 0) b.CollectionId = o.CollectionId;
            if (b.Popularity == 0 && o.Popularity != 0) b.Popularity = o.Popularity;
            if (b.Genres.Count == 0) b.Genres = o.Genres;
            if (b.Countries.Count == 0) b.Countries = o.Countries;
            if (b.Languages.Count == 0) b.Languages = o.Languages;
            if (b.Keywords.Count == 0) b.Keywords = o.Keywords;
            if (b.Seasons.Count == 0 && o.Seasons.Count > 0) b.Seasons = o.Seasons;
            if (b.Episodes.Count == 0 && o.Episodes.Count > 0) b.Episodes = o.Episodes;
            // Cast: keep the richer list, then fold in bios and photos by name.
            if (o.Cast.Count > b.Cast.Count) (b.Cast, o.Cast) = (o.Cast, b.Cast);
            var byName = new Dictionary<string, PersonInfo>();
            foreach (var p in b.Cast) byName[p.Name.ToLowerInvariant()] = p;
            foreach (var person in o.Cast)
            {
                if (!byName.TryGetValue(person.Name.ToLowerInvariant(), out var t)) continue;
                if (t.Bio.Length == 0) t.Bio = person.Bio;
                if (t.Birthday.Length == 0) t.Birthday = person.Birthday;
                if (t.Deathday.Length == 0) t.Deathday = person.Deathday;
                if (t.Birthplace.Length == 0) t.Birthplace = person.Birthplace;
                if (t.Photo.Length == 0) t.Photo = person.Photo;
                if (t.Character.Length == 0) t.Character = person.Character;
            }
            if (b.Crew.Count == 0) b.Crew = o.Crew;
        }
        b.Source = string.Join("+", sources.Distinct());
        return b;
    }

    public static (MetaResult? Result, List<string> Log) Lookup(string title, int? year, string kind,
                                                                 IEnumerable<Provider>? providers = null)
    {
        var log = new List<string>();
        var results = new List<MetaResult?>();
        var chain = providers?.ToList() ?? ActiveProviders(kind);
        if (chain.Count == 0)
        {
            log.Add("No provider available — add an API key in Settings, or check the provider status panel.");
            return (null, log);
        }
        foreach (var provider in chain)
        {
            try
            {
                var match = BestMatch(provider.Search(title, year, kind), title, year);
                if (match == null)
                {
                    log.Add($"{provider.Name}: no match for '{title}'");
                    MarkOk(provider.Name);
                    continue;
                }
                var result = provider.Fetch(match.Id, kind);
                if (result == null)
                {
                    log.Add($"{provider.Name}: match found but details missing");
                    MarkOk(provider.Name);
                    continue;
                }
                MarkOk(provider.Name);
                results.Add(result);
                log.Add($"{provider.Name}: matched '{result.Title}' ({(result.Year?.ToString() ?? "—")}), {result.Score()}/7 fields");
            }
            catch (NotConfigured ex) { log.Add($"{provider.Name}: {ex.Message}"); }
            catch (ProviderError ex)
            {
                MarkFail(provider.Name, ex.Message);
                log.Add($"{provider.Name}: unreachable ({ex.Message}) — trying the next one");
            }
            catch (Exception ex)
            {
                MarkFail(provider.Name, ex.ToString());
                log.Add($"{provider.Name}: failed ({ex.Message})");
            }
        }
        if (results.Count == 0)
            log.Add("Every provider came back empty. Try editing the title and scraping again.");
        return (Merge(results), log);
    }

    public static List<EpisodeInfo> FetchEpisodes(MetaIds ids, int season)
    {
        foreach (var provider in ActiveProviders("show"))
        {
            try
            {
                var eps = provider.Episodes(ids, season);
                if (eps is { Count: > 0 })
                {
                    MarkOk(provider.Name);
                    return eps;
                }
            }
            catch (NotConfigured) { }
            catch (ProviderError ex) { MarkFail(provider.Name, ex.Message); }
            catch (Exception) { }
        }
        return new List<EpisodeInfo>();
    }

    // ------------------------------------------------------------------ images
    static readonly Regex SlugRun = new("[^a-z0-9]+", RegexOptions.CultureInvariant);

    /// <summary>Download once into the local image folder; return a /media/... path.</summary>
    public static string CacheImage(string url, string folder, string tag = "")
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http")) return url ?? "";
        if (!Settings.Bool("download_images", true)) return url;
        var ext = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp")) ext = ".jpg";
        var digest = Config.Sha1Hex(url, 16);
        string name;
        if (tag.Length > 0)
        {
            var slug = SlugRun.Replace(tag.ToLowerInvariant(), "-").Trim('-');
            if (slug.Length > 40) slug = slug[..40];
            name = $"{slug}-{digest}{ext}";
        }
        else name = $"{digest}{ext}";
        var dest = Path.Combine(folder, name);
        if (!File.Exists(dest))
        {
            lock (ImageGate)
            {
                if (!File.Exists(dest))
                {
                    var data = Http.GetBytes(url);
                    if (data == null || data.Length < 512) return url;
                    try { File.WriteAllBytes(dest, data); }
                    catch { return url; }
                }
            }
        }
        return $"/media/image/{Path.GetFileName(folder)}/{name}";
    }

    // ------------------------------------------------------ writing a result in
    public static void ApplyResult(long mediaId, MetaResult result, bool withEpisodes = true)
    {
        var row = Db.QueryOne("SELECT kind, locked FROM media WHERE id = ?", mediaId);
        if (row == null) return;
        var kind = row.Str("kind");
        var poster = CacheImage(result.Poster, Config.PosterDir, result.Title);
        var backdrop = CacheImage(result.Backdrop, Config.BackdropDir, result.Title);
        var artwork = result.Artwork.Count > 0 ? JsonSerializer.Serialize(result.Artwork) : "";
        Db.Execute("""
            UPDATE media SET title = ?, sort_title = ?, year = ?, overview = ?,
               tagline = ?, certificate = ?, rating = ?, votes = ?, popularity = ?,
               runtime = ?, release_date = ?, status = ?, poster = ?, backdrop = ?,
               trailer = ?, tmdb_id = ?, imdb_id = ?, tvmaze_id = ?,
               collection_id = ?, collection_name = ?,
               artwork = CASE WHEN ? <> '' THEN ? ELSE artwork END,
               scraped_at = ?, scrape_source = ?, updated_at = ?
            WHERE id = ?
            """,
            string.IsNullOrEmpty(result.Title) ? "Untitled" : result.Title, Names.SortTitle(result.Title), result.Year,
            result.Overview, result.Tagline, result.Certificate, result.Rating, result.Votes, result.Popularity,
            result.Runtime, result.ReleaseDate, result.Status, poster, backdrop, result.Trailer, result.TmdbId,
            result.ImdbId, result.TvmazeId, result.CollectionId, result.CollectionName, artwork, artwork,
            Db.Now(), result.Source, Db.Now(), mediaId);

        Db.SetGenres(mediaId, result.Genres);
        Db.Execute("DELETE FROM credits WHERE media_id = ?", mediaId);
        foreach (var person in result.Cast)
        {
            UpsertPerson(person);
            Db.Execute("INSERT OR REPLACE INTO credits (media_id, person_id, role, character, ord) VALUES (?, ?, 'cast', ?, ?)",
                       mediaId, Db.PersonId(person.Name), person.Character, person.Order);
        }
        foreach (var person in result.Crew)
        {
            UpsertPerson(person);
            Db.Execute("INSERT OR REPLACE INTO credits (media_id, person_id, role, character, ord) VALUES (?, ?, 'crew', ?, 0)",
                       mediaId, Db.PersonId(person.Name), person.Character);
        }
        if (kind == "show") ApplySeasons(mediaId, result, withEpisodes);
        RelinkFiles(mediaId);
        Db.BumpLibrary();
    }

    static long UpsertPerson(PersonInfo person)
    {
        var pid = Db.PersonId(person.Name);
        var row = Db.QueryOne("SELECT * FROM people WHERE id = ?", pid);
        if (row != null && row.Truthy("locked")) return pid;
        var photo = person.Photo;
        if (photo.StartsWith("http")) photo = CacheImage(photo, Config.PersonDir, person.Name);
        var sets = new List<string>();
        var args = new List<object?>();
        foreach (var (column, value) in new[]
                 {
                     ("bio", person.Bio), ("birthday", person.Birthday), ("deathday", person.Deathday),
                     ("birthplace", person.Birthplace), ("photo", photo), ("imdb_id", person.ImdbId),
                 })
        {
            if (value.Length > 0 && !(row != null && row.Truthy(column)))
            {
                sets.Add($"{column} = ?");
                args.Add(value);
            }
        }
        if (person.TmdbId is > 0 && !(row != null && row.Truthy("tmdb_id")))
        {
            sets.Add("tmdb_id = ?");
            args.Add(person.TmdbId);
        }
        if (sets.Count > 0)
        {
            args.Add(pid);
            Db.Execute($"UPDATE people SET {string.Join(", ", sets)} WHERE id = ?", args.ToArray());
        }
        return pid;
    }

    const string EpisodeUpsert = """
        INSERT INTO episodes (show_id, season, number, title, overview, air_date, rating, runtime, still)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT(show_id, season, number) DO UPDATE SET
          title = COALESCE(NULLIF(excluded.title, ''), episodes.title),
          overview = COALESCE(NULLIF(excluded.overview, ''), episodes.overview),
          air_date = COALESCE(NULLIF(excluded.air_date, ''), episodes.air_date),
          rating = COALESCE(excluded.rating, episodes.rating),
          runtime = COALESCE(excluded.runtime, episodes.runtime),
          still = COALESCE(NULLIF(excluded.still, ''), episodes.still)
        """;

    static void ApplySeasons(long showId, MetaResult result, bool withEpisodes)
    {
        var haveFiles = Db.Query("SELECT DISTINCT season FROM files WHERE media_id = ? AND season IS NOT NULL", showId)
            .Select(r => (int)(r.Long("season") ?? 0)).ToHashSet();
        var seasons = result.Seasons.ToList();
        var known = seasons.Select(s => s.Number).ToHashSet();
        foreach (var number in haveFiles.Except(known).OrderBy(n => n))
            seasons.Add(new SeasonInfo { Number = number });

        foreach (var season in seasons)
        {
            var poster = CacheImage(season.Poster, Config.PosterDir, $"{result.Title} S{season.Number:00}");
            Db.Execute("""
                INSERT INTO seasons (show_id, number, name, overview, poster, air_date) VALUES (?, ?, ?, ?, ?, ?)
                ON CONFLICT(show_id, number) DO UPDATE SET
                  name = COALESCE(NULLIF(excluded.name, ''), seasons.name),
                  overview = COALESCE(NULLIF(excluded.overview, ''), seasons.overview),
                  poster = COALESCE(NULLIF(excluded.poster, ''), seasons.poster),
                  air_date = COALESCE(NULLIF(excluded.air_date, ''), seasons.air_date)
                """, showId, season.Number, season.Name, season.Overview, poster, season.AirDate);
        }
        if (!withEpisodes) return;

        var ids = new MetaIds(result.TmdbId, result.TvmazeId, result.ImdbId, result.Title);
        foreach (var number in haveFiles.OrderBy(n => n))
        {
            var episodes = result.Episodes.Where(e => e.Season == number).ToList();
            if (episodes.Count == 0) episodes = FetchEpisodes(ids, number);
            foreach (var ep in episodes)
            {
                var still = CacheImage(ep.Still, Config.StillDir, $"{result.Title} S{ep.Season:00}E{ep.Number:00}");
                Db.Execute(EpisodeUpsert, showId, ep.Season, ep.Number, ep.Title, ep.Overview, ep.AirDate,
                           ep.Rating, ep.Runtime, still);
            }
        }
    }

    static void RelinkFiles(long showId)
    {
        foreach (var row in Db.Query(
                     "SELECT id, season, episode FROM files WHERE media_id = ? AND season IS NOT NULL AND episode IS NOT NULL",
                     showId))
        {
            var ep = Db.QueryOne("SELECT id FROM episodes WHERE show_id = ? AND season = ? AND number = ?",
                                 showId, row.Long("season"), row.Long("episode"));
            if (ep != null)
                Db.Execute("UPDATE files SET episode_id = ? WHERE id = ?", ep.Long("id"), row.Long("id"));
        }
    }

    public static ScrapeOutcome ScrapeMedia(long mediaId, string? title = null, int? year = null,
                                            string? kind = null, string? providerName = null, bool yearGiven = false)
    {
        var row = Db.QueryOne("SELECT * FROM media WHERE id = ?", mediaId);
        if (row == null) return new ScrapeOutcome(false, new() { "No such title in the library." });
        if (row.Truthy("locked")) return new ScrapeOutcome(false, new() { "This title is locked. Unlock it to scrape." });
        kind ??= row.Str("kind");
        title = (string.IsNullOrWhiteSpace(title) ? row.Str("title") : title).Trim();
        if (!yearGiven && year == null) year = row.Int("year");

        List<Provider>? providers = null;
        if (!string.IsNullOrEmpty(providerName))
            providers = Registry.All.TryGetValue(providerName, out var p) && p.Supports.Contains(kind)
                ? new List<Provider> { p } : new List<Provider>();

        var (result, log) = Lookup(title, year, kind, providers);
        if (result == null) return new ScrapeOutcome(false, log);
        ApplyResult(mediaId, result);
        return new ScrapeOutcome(true, log, result.Source, result.Title);
    }

    /// <summary>Every provider's top hits, so a wrong automatic match can be put right.</summary>
    public static (List<Candidate> Items, List<string> Log) Candidates(string title, int? year, string kind)
    {
        var items = new List<Candidate>();
        var log = new List<string>();
        foreach (var provider in ActiveProviders(kind))
        {
            try
            {
                foreach (var hit in provider.Search(title, year, kind))
                {
                    hit.Provider = provider.Name;
                    hit.Score = Math.Round(Similarity.TitleSimilarity(title, hit.Title), 3);
                    items.Add(hit);
                }
            }
            catch (Exception ex) { log.Add($"{provider.Name}: {ex.Message}"); }
        }
        items = items.OrderByDescending(h => h.Score).ThenByDescending(h => h.Popularity).Take(24).ToList();
        return (items, log);
    }

    /// <summary>Apply the hit that was picked, then top it up from the other providers.</summary>
    public static ScrapeOutcome ApplyMatch(long mediaId, string providerName, object? id, string? kind = null)
    {
        if (!Registry.All.TryGetValue(providerName, out var provider))
            return new ScrapeOutcome(false, new() { "Unknown provider" });
        var row = Db.QueryOne("SELECT kind FROM media WHERE id = ?", mediaId);
        if (row == null) return new ScrapeOutcome(false, new() { "Not in the library" });
        kind ??= row.Str("kind");
        MetaResult? result;
        try { result = provider.Fetch(id, kind); }
        catch (Exception ex) { return new ScrapeOutcome(false, new() { $"{provider.Name} failed: {ex.Message}" }); }
        if (result == null) return new ScrapeOutcome(false, new() { "That entry had no details" });
        var extras = new List<MetaResult?> { result };
        foreach (var other in ActiveProviders(kind))
        {
            if (other.Name == provider.Name) continue;
            try
            {
                var best = BestMatch(other.Search(result.Title, result.Year, kind), result.Title, result.Year);
                if (best != null && other.Fetch(best.Id, kind) is { } extra) extras.Add(extra);
            }
            catch { }
        }
        var merged = Merge(extras) ?? result;
        ApplyResult(mediaId, merged);
        return new ScrapeOutcome(true, new(), merged.Source, merged.Title);
    }

    /// <summary>Franchise data for films already scraped, touching nothing else.</summary>
    public static (bool Ok, int Checked, int Found, List<string> Log) RefreshCollections()
    {
        var provider = Registry.All["tmdb"];
        if (!provider.Configured())
            return (false, 0, 0, new() { "Franchise data comes from TMDB. Add a TMDB API key in Settings, then try again." });
        var rows = Db.Query("SELECT id, title, tmdb_id FROM media WHERE kind = 'movie' AND tmdb_id IS NOT NULL ORDER BY sort_title");
        int checkedCount = 0, found = 0;
        var log = new List<string>();
        foreach (var row in rows)
        {
            checkedCount++;
            MetaResult? result;
            try { result = provider.Fetch(row.Long("tmdb_id"), "movie"); }
            catch (NotConfigured ex) { log.Add($"{row.Str("title")}: {ex.Message}"); continue; }
            catch (ProviderError ex)
            {
                MarkFail(provider.Name, ex.Message);
                log.Add($"TMDB unreachable ({ex.Message}) — stopped after {checkedCount}.");
                break;
            }
            catch (Exception ex) { log.Add($"{row.Str("title")}: {ex.Message}"); continue; }
            if (result == null) continue;
            MarkOk(provider.Name);
            if (result.CollectionId is > 0)
            {
                found++;
                Db.Execute("UPDATE media SET collection_id = ?, collection_name = ? WHERE id = ?",
                           result.CollectionId, result.CollectionName, row.Long("id"));
                log.Add($"{row.Str("title")} -> {result.CollectionName}");
            }
        }
        var unscraped = Db.Scalar("""
            SELECT COUNT(*) AS n FROM media WHERE kind = 'movie' AND tmdb_id IS NULL
            AND EXISTS (SELECT 1 FROM files f WHERE f.media_id = media.id)
            """);
        if (unscraped > 0)
            log.Add($"{unscraped} films have no TMDB match yet — scrape them to include them in franchises.");
        if (found > 0) Db.BumpLibrary();
        return (true, checkedCount, found, log);
    }

    /// <summary>Episode details for seasons that have files but were never scraped.</summary>
    public static (int Seasons, int Episodes, List<string> Log) FillEpisodeGaps(long? mediaId = null)
    {
        var sql = "SELECT * FROM media WHERE kind = 'show' AND locked = 0";
        var args = new List<object?>();
        if (mediaId is > 0)
        {
            sql += " AND id = ?";
            args.Add(mediaId);
        }
        int filledSeasons = 0, filledEpisodes = 0;
        var log = new List<string>();
        foreach (var show in Db.Query(sql + " ORDER BY sort_title", args.ToArray()))
        {
            if (!(show.Truthy("tmdb_id") || show.Truthy("tvmaze_id") || show.Truthy("imdb_id"))) continue;
            var ids = new MetaIds(show.Long("tmdb_id"), show.Long("tvmaze_id"), show.Str("imdb_id"), show.Str("title"));
            var showId = show.Long("id")!.Value;
            foreach (var row in Db.Query("""
                         SELECT DISTINCT season FROM files
                         WHERE media_id = ? AND missing = 0 AND season IS NOT NULL ORDER BY season
                         """, showId))
            {
                var season = (int)(row.Long("season") ?? 0);
                var have = Db.Scalar("SELECT COUNT(*) AS n FROM episodes WHERE show_id = ? AND season = ?", showId, season);
                var want = Db.Scalar("""
                    SELECT COUNT(DISTINCT episode) AS n FROM files WHERE media_id = ?
                    AND season = ? AND episode IS NOT NULL
                    """, showId, season);
                if (have >= want && have > 0) continue;
                var episodes = FetchEpisodes(ids, season);
                if (episodes.Count == 0)
                {
                    log.Add($"{show.Str("title")} season {season}: no episode details found");
                    continue;
                }
                Db.Execute("INSERT OR IGNORE INTO seasons (show_id, number, name) VALUES (?, ?, ?)",
                           showId, season, season == 0 ? "Specials" : $"Season {season}");
                foreach (var ep in episodes)
                {
                    var still = CacheImage(ep.Still, Config.StillDir, $"{show.Str("title")} S{ep.Season:00}E{ep.Number:00}");
                    Db.Execute("""
                        INSERT INTO episodes (show_id, season, number, title, overview, air_date, rating, runtime, still)
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                        ON CONFLICT(show_id, season, number) DO UPDATE SET
                          title = COALESCE(NULLIF(excluded.title, ''), episodes.title),
                          overview = COALESCE(NULLIF(excluded.overview, ''), episodes.overview),
                          air_date = COALESCE(NULLIF(excluded.air_date, ''), episodes.air_date),
                          rating = COALESCE(excluded.rating, episodes.rating),
                          still = COALESCE(NULLIF(excluded.still, ''), episodes.still)
                        """, showId, ep.Season, ep.Number, ep.Title, ep.Overview, ep.AirDate, ep.Rating, ep.Runtime, still);
                    filledEpisodes++;
                }
                filledSeasons++;
                log.Add($"{show.Str("title")} season {season}: {episodes.Count} episodes");
                RelinkFiles(showId);
            }
        }
        if (filledSeasons > 0) Db.BumpLibrary();
        return (filledSeasons, filledEpisodes, log);
    }

    public static ScrapeOutcome RefreshPerson(long personId)
    {
        var row = Db.QueryOne("SELECT * FROM people WHERE id = ?", personId);
        if (row == null) return new ScrapeOutcome(false, new() { "No such person." });
        if (row.Truthy("locked")) return new ScrapeOutcome(false, new() { "This profile is locked." });
        var log = new List<string>();
        var tried = new HashSet<string>();
        foreach (var provider in ActiveProviders("movie").Concat(ActiveProviders("show")))
        {
            if (!tried.Add(provider.Name)) continue;
            try
            {
                var info = provider.Person(row.Str("name"), row.Long("tmdb_id"));
                if (info == null)
                {
                    log.Add($"{provider.Name}: no profile for '{row.Str("name")}'");
                    continue;
                }
                MarkOk(provider.Name);
                var photo = info.Photo.Length > 0 ? CacheImage(info.Photo, Config.PersonDir, info.Name) : "";
                Db.Execute("""
                    UPDATE people SET
                      bio = CASE WHEN ? <> '' THEN ? ELSE bio END,
                      birthday = CASE WHEN ? <> '' THEN ? ELSE birthday END,
                      deathday = CASE WHEN ? <> '' THEN ? ELSE deathday END,
                      birthplace = CASE WHEN ? <> '' THEN ? ELSE birthplace END,
                      photo = CASE WHEN ? <> '' THEN ? ELSE photo END,
                      tmdb_id = COALESCE(?, tmdb_id),
                      scraped_at = ?
                    WHERE id = ?
                    """, info.Bio, info.Bio, info.Birthday, info.Birthday, info.Deathday, info.Deathday,
                    info.Birthplace, info.Birthplace, photo, photo, info.TmdbId, Db.Now(), personId);
                log.Add($"{provider.Name}: updated {info.Name}");
                if (info.Bio.Length > 0) return new ScrapeOutcome(true, log);
            }
            catch (NotConfigured ex) { log.Add($"{provider.Name}: {ex.Message}"); }
            catch (ProviderError ex)
            {
                MarkFail(provider.Name, ex.Message);
                log.Add($"{provider.Name}: unreachable ({ex.Message})");
            }
            catch (Exception ex) { log.Add($"{provider.Name}: failed ({ex.Message})"); }
        }
        return new ScrapeOutcome(log.Count > 0, log);
    }
}
