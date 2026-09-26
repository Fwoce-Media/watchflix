using System.Text.Json.Nodes;

namespace WatchFlix.Core.Providers;

/// <summary>The Movie Database. Broadest coverage, needs a free API key.</summary>
public sealed class TmdbProvider : Provider
{
    const string Api = "https://api.themoviedb.org/3";
    const string Img = "https://image.tmdb.org/t/p";
    const string PosterBase = Img + "/w500";
    const string BackdropBase = Img + "/w1280";
    const string ProfileBase = Img + "/h632";
    const string StillBase = Img + "/w300";

    public override string Name => "tmdb";
    public override bool NeedsKey => true;

    static string Image(string b, JsonNode? path) => J.S(path) is { Length: > 0 } p ? b + p : "";

    string Key()
    {
        var key = Settings.Str("tmdb_api_key").Trim();
        if (key.Length == 0) throw new NotConfigured("TMDB API key not set");
        return key;
    }

    public override bool Configured() => Settings.Str("tmdb_api_key").Trim().Length > 0;

    static string Lang() => Settings.Str("language", "en-US");

    static List<Candidate> Hits(JsonNode? data)
    {
        var list = new List<Candidate>();
        foreach (var item in J.A(data?["results"]).Take(8))
        {
            var date = J.S(item["release_date"]);
            if (date.Length == 0) date = J.S(item["first_air_date"]);
            var title = J.S(item["title"]);
            if (title.Length == 0) title = J.S(item["name"]);
            list.Add(new Candidate
            {
                Id = J.L(item["id"]), Title = title, Year = J.YearOf(date),
                Popularity = J.D(item["popularity"]) ?? 0.0, Poster = Image(PosterBase, item["poster_path"]),
            });
        }
        return list;
    }

    public override List<Candidate> Search(string title, int? year, string kind)
    {
        var path = kind == "movie" ? "movie" : "tv";
        var query = new List<(string, object?)>
        {
            ("api_key", Key()), ("query", title), ("language", Lang()), ("include_adult", "false"),
        };
        if (year != null) query.Add((kind == "movie" ? "year" : "first_air_date_year", year));
        var hits = Hits(Http.GetJson($"{Api}/search/{path}", query));
        if (hits.Count == 0 && year != null)
        {
            query.RemoveAll(q => q.Item1 is "year" or "first_air_date_year");
            hits = Hits(Http.GetJson($"{Api}/search/{path}", query));
        }
        return hits;
    }

    public override MetaResult? Fetch(object? id, string kind)
    {
        if (id == null || Convert.ToString(id) is not { Length: > 0 } idText) return null;
        var path = kind == "movie" ? "movie" : "tv";
        var extra = kind == "movie"
            ? "credits,release_dates,videos,external_ids,keywords"
            : "aggregate_credits,content_ratings,videos,external_ids,keywords";
        var data = Http.GetJson($"{Api}/{path}/{idText}", new (string, object?)[]
        {
            ("api_key", Key()), ("language", Lang()), ("append_to_response", extra),
        });
        if (data == null) return null;

        var date = J.S(data["release_date"]);
        if (date.Length == 0) date = J.S(data["first_air_date"]);
        var title = J.S(data["title"]);
        if (title.Length == 0) title = J.S(data["name"]);
        var imdb = J.S(data["imdb_id"]);
        if (imdb.Length == 0) imdb = J.S(data["external_ids"]?["imdb_id"]);

        var res = new MetaResult
        {
            Source = Name, Kind = kind, Title = title, Year = J.YearOf(date),
            Overview = J.S(data["overview"]), Tagline = J.S(data["tagline"]),
            Rating = J.NormRating(data["vote_average"]), Votes = J.L(data["vote_count"]),
            Popularity = J.D(data["popularity"]) ?? 0.0, ReleaseDate = date, Status = J.S(data["status"]),
            Genres = J.A(data["genres"]).Select(g => J.S(g["name"])).Where(s => s.Length > 0).ToList(),
            Poster = Image(PosterBase, data["poster_path"]), Backdrop = Image(BackdropBase, data["backdrop_path"]),
            TmdbId = long.TryParse(idText, out var tid) ? tid : null, ImdbId = imdb,
        };
        res.Countries = J.A(data["origin_country"]).Select(J.S).Where(s => s.Length > 0).ToList();
        res.Countries.AddRange(J.A(data["production_countries"]).Select(c => J.S(c["iso_3166_1"])).Where(s => s.Length > 0));
        var lang = J.S(data["original_language"]);
        if (lang.Length > 0) res.Languages.Add(lang);
        var keywords = data["keywords"];
        var kwList = keywords?["keywords"] ?? keywords?["results"];
        res.Keywords = J.A(kwList).Select(k => J.S(k["name"])).ToList();

        if (kind == "movie")
        {
            var collection = data["belongs_to_collection"];
            res.CollectionId = J.L(collection?["id"]);
            res.CollectionName = J.S(collection?["name"]);
            res.Runtime = J.I(data["runtime"]);
            res.Certificate = MovieCert(data);
            res.Cast = CastOf(data["credits"], false);
            res.Crew = CrewOf(data["credits"]);
        }
        else
        {
            var runtimes = J.A(data["episode_run_time"]).ToList();
            res.Runtime = runtimes.Count > 0 ? J.I(runtimes[0]) : null;
            res.Certificate = TvCert(data);
            res.Cast = CastOf(data["aggregate_credits"], true);
            res.Crew = CrewOf(data["aggregate_credits"]);
            res.Seasons = J.A(data["seasons"])
                .Where(s => J.I(s["season_number"]) != null)
                .Select(s => new SeasonInfo
                {
                    Number = J.I(s["season_number"]) ?? 0, Name = J.S(s["name"]), Overview = J.S(s["overview"]),
                    AirDate = J.S(s["air_date"]), Poster = Image(PosterBase, s["poster_path"]),
                }).ToList();
        }
        res.Trailer = TrailerOf(data["videos"]);
        return res;
    }

    public override List<EpisodeInfo>? Episodes(MetaIds ids, int season)
    {
        if (ids.TmdbId is not long id) return null;
        var data = Http.GetJson($"{Api}/tv/{id}/season/{season}", new (string, object?)[]
        {
            ("api_key", Key()), ("language", Lang()),
        });
        if (data == null) return new List<EpisodeInfo>();
        return J.A(data["episodes"]).Select(e => new EpisodeInfo
        {
            Season = season, Number = J.I(e["episode_number"]) ?? 0, Title = J.S(e["name"]),
            Overview = J.S(e["overview"]), AirDate = J.S(e["air_date"]),
            Rating = J.NormRating(e["vote_average"]), Runtime = J.I(e["runtime"]),
            Still = Image(StillBase, e["still_path"]),
        }).ToList();
    }

    public override PersonInfo? Person(string name, long? tmdbId)
    {
        var pid = tmdbId;
        if (pid == null)
        {
            var found = Http.GetJson($"{Api}/search/person", new (string, object?)[]
            {
                ("api_key", Key()), ("query", name), ("include_adult", "false"),
            });
            var first = J.A(found?["results"]).FirstOrDefault();
            if (first == null) return null;
            pid = J.L(first["id"]);
        }
        var data = Http.GetJson($"{Api}/person/{pid}", new (string, object?)[]
        {
            ("api_key", Key()), ("language", Lang()),
        });
        if (data == null) return null;
        var n = J.S(data["name"]);
        return new PersonInfo
        {
            Name = n.Length > 0 ? n : name, Bio = J.S(data["biography"]), Birthday = J.S(data["birthday"]),
            Deathday = J.S(data["deathday"]), Birthplace = J.S(data["place_of_birth"]),
            Photo = Image(ProfileBase, data["profile_path"]), TmdbId = pid, ImdbId = J.S(data["imdb_id"]),
        };
    }

    static readonly string[] Prefer = { "US", "GB", "CA", "AU" };

    static string MovieCert(JsonNode data)
    {
        var results = J.A(data["release_dates"]?["results"]).ToList();
        var byCountry = new Dictionary<string, JsonNode>();
        foreach (var r in results) byCountry[J.S(r["iso_3166_1"])] = r;
        foreach (var code in Prefer)
            if (byCountry.TryGetValue(code, out var entry))
                foreach (var rel in J.A(entry["release_dates"]))
                    if (J.S(rel["certification"]) is { Length: > 0 } c) return c;
        foreach (var entry in results)
            foreach (var rel in J.A(entry["release_dates"]))
                if (J.S(rel["certification"]) is { Length: > 0 } c) return c;
        return "";
    }

    static string TvCert(JsonNode data)
    {
        var byCountry = new Dictionary<string, string>();
        foreach (var r in J.A(data["content_ratings"]?["results"])) byCountry[J.S(r["iso_3166_1"])] = J.S(r["rating"]);
        foreach (var code in Prefer)
            if (byCountry.TryGetValue(code, out var v) && v.Length > 0) return v;
        return byCountry.Values.FirstOrDefault(v => v.Length > 0) ?? "";
    }

    static List<PersonInfo> CastOf(JsonNode? credits, bool aggregate)
    {
        var list = new List<PersonInfo>();
        var i = 0;
        foreach (var c in J.A(credits?["cast"]).Take(30))
        {
            string character;
            if (aggregate)
            {
                var role = J.A(c["roles"]).FirstOrDefault();
                character = role != null ? J.S(role["character"]) : "";
            }
            else character = J.S(c["character"]);
            list.Add(new PersonInfo
            {
                Name = J.S(c["name"]), Character = character, Order = J.I(c["order"]) ?? i,
                Photo = Image(ProfileBase, c["profile_path"]), TmdbId = J.L(c["id"]),
            });
            i++;
        }
        return list.Where(p => p.Name.Length > 0).ToList();
    }

    static readonly HashSet<string> WantedJobs = new() { "Director", "Creator", "Writer", "Screenplay", "Executive Producer" };

    static List<PersonInfo> CrewOf(JsonNode? credits)
    {
        var list = new List<PersonInfo>();
        foreach (var c in J.A(credits?["crew"]))
        {
            var job = J.S(c["job"]);
            if (job.Length == 0 && J.A(c["jobs"]).FirstOrDefault() is { } first) job = J.S(first["job"]);
            if (WantedJobs.Contains(job))
                list.Add(new PersonInfo
                {
                    Name = J.S(c["name"]), Character = job, Photo = Image(ProfileBase, c["profile_path"]),
                    TmdbId = J.L(c["id"]),
                });
        }
        return list.Where(p => p.Name.Length > 0).Take(12).ToList();
    }

    static string TrailerOf(JsonNode? videos)
    {
        foreach (var v in J.A(videos?["results"]))
            if (J.S(v["site"]) == "YouTube" && J.S(v["type"]) == "Trailer")
                return $"https://www.youtube.com/watch?v={J.S(v["key"])}";
        return "";
    }
}
