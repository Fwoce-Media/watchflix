using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace WatchFlix.Core.Providers;

/// <summary>TVmaze. No key, TV only — the fallback that keeps working when the others cannot.</summary>
public sealed class TvmazeProvider : Provider
{
    const string Api = "https://api.tvmaze.com";
    static readonly Regex Tags = new("<[^>]+>", RegexOptions.CultureInvariant);

    public override string Name => "tvmaze";
    public override string[] Supports => new[] { "show" };

    static string Plain(JsonNode? html)
    {
        var text = J.S(html);
        if (text.Length == 0) return "";
        text = Tags.Replace(text, "");
        text = text.Replace("&amp;", "&").Replace("&quot;", "\"").Replace("&#39;", "'");
        return string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    static string Image(JsonNode? node, string size = "original")
    {
        if (node is not JsonObject) return "";
        var v = J.S(node[size]);
        return v.Length > 0 ? v : J.S(node["medium"]);
    }

    public override List<Candidate> Search(string title, int? year, string kind)
    {
        if (kind != "show") return new List<Candidate>();
        var data = Http.GetJson($"{Api}/search/shows", new (string, object?)[] { ("q", title) });
        return J.A(data).Take(8).Select(item =>
        {
            var show = item["show"];
            var premiered = J.S(show?["premiered"]);
            return new Candidate
            {
                Id = J.L(show?["id"]), Title = J.S(show?["name"]), Year = J.YearOf(premiered),
                Popularity = (J.D(item["score"]) ?? 0) * 10, Poster = Image(show?["image"]),
            };
        }).ToList();
    }

    public override MetaResult? Fetch(object? id, string kind)
    {
        if (kind != "show" || id == null || Convert.ToString(id) is not { Length: > 0 } showId) return null;
        var data = Http.GetJson($"{Api}/shows/{showId}", new (string, object?)[] { ("embed[]", "cast") });
        if (data == null) return null;
        var seasonsRaw = Http.GetJson($"{Api}/shows/{showId}/seasons");
        var castRaw = J.A(data["_embedded"]?["cast"]).ToList();
        if (castRaw.Count == 0) castRaw = J.A(Http.GetJson($"{Api}/shows/{showId}/cast")).ToList();

        var premiered = J.S(data["premiered"]);
        var net = data["network"] is JsonObject ? data["network"] : data["webChannel"];
        var res = new MetaResult
        {
            Source = Name, Kind = "show", Title = J.S(data["name"]), Year = J.YearOf(premiered),
            Overview = Plain(data["summary"]), Certificate = J.S(data["type"]) == "Adult" ? "TV-MA" : "",
            Rating = J.NormRating(data["rating"]?["average"]), Popularity = J.D(data["weight"]) ?? 0,
            Runtime = J.I(data["averageRuntime"]) ?? J.I(data["runtime"]), ReleaseDate = premiered,
            Status = J.S(data["status"]),
            Genres = J.A(data["genres"]).Select(J.S).Where(s => s.Length > 0).ToList(),
            Poster = Image(data["image"]), TvmazeId = long.TryParse(showId, out var sid) ? sid : null,
            ImdbId = J.S(data["externals"]?["imdb"]),
        };
        var netName = J.S(net?["name"]);
        if (netName.Length > 0) res.Tagline = netName;
        var country = J.S(net?["country"]?["code"]);
        if (country.Length > 0) res.Countries.Add(country);
        var lang = J.S(data["language"]).ToLowerInvariant();
        if (lang.Length > 0) res.Languages.Add(lang == "japanese" ? "ja" : lang);

        var i = 0;
        foreach (var c in castRaw.Take(30))
        {
            var person = c["person"];
            res.Cast.Add(new PersonInfo
            {
                Name = J.S(person?["name"]), Character = J.S(c["character"]?["name"]), Order = i++,
                Photo = Image(person?["image"]), Birthday = J.S(person?["birthday"]),
                Deathday = J.S(person?["deathday"]), Birthplace = J.S(person?["country"]?["name"]),
            });
        }
        res.Cast = res.Cast.Where(p => p.Name.Length > 0).ToList();
        res.Seasons = J.A(seasonsRaw).Where(s => J.I(s["number"]) != null).Select(s => new SeasonInfo
        {
            Number = J.I(s["number"]) ?? 0, Name = J.S(s["name"]), Overview = Plain(s["summary"]),
            AirDate = J.S(s["premiereDate"]), Poster = Image(s["image"]),
        }).ToList();
        return res;
    }

    public override List<EpisodeInfo>? Episodes(MetaIds ids, int season)
    {
        if (ids.TvmazeId is not long id) return null;
        var data = Http.GetJson($"{Api}/shows/{id}/episodes");
        return J.A(data)
            .Where(e => J.I(e["season"]) == season)
            .Select(e => new EpisodeInfo
            {
                Season = J.I(e["season"]) ?? 0, Number = J.I(e["number"]) ?? 0, Title = J.S(e["name"]),
                Overview = Plain(e["summary"]), AirDate = J.S(e["airdate"]),
                Rating = J.NormRating(e["rating"]?["average"]), Runtime = J.I(e["runtime"]),
                Still = Image(e["image"]),
            })
            .Where(e => e.Number != 0).ToList();
    }

    public override PersonInfo? Person(string name, long? tmdbId)
    {
        var data = J.A(Http.GetJson($"{Api}/search/people", new (string, object?)[] { ("q", name) })).ToList();
        if (data.Count == 0) return null;
        var person = data[0]["person"];
        var n = J.S(person?["name"]);
        return new PersonInfo
        {
            Name = n.Length > 0 ? n : name, Birthday = J.S(person?["birthday"]), Deathday = J.S(person?["deathday"]),
            Birthplace = J.S(person?["country"]?["name"]), Photo = Image(person?["image"]),
        };
    }
}

/// <summary>OMDb. IMDb-derived, free key; mostly useful for the IMDb rating and certificate.</summary>
public sealed class OmdbProvider : Provider
{
    const string Api = "https://www.omdbapi.com/";
    public override string Name => "omdb";
    public override bool NeedsKey => true;

    string Key()
    {
        var key = Settings.Str("omdb_api_key").Trim();
        if (key.Length == 0) throw new NotConfigured("OMDb API key not set");
        return key;
    }

    public override bool Configured() => Settings.Str("omdb_api_key").Trim().Length > 0;

    static string Clean(JsonNode? value)
    {
        var s = J.S(value).Trim();
        return s is "N/A" or "NA" ? "" : s;
    }

    static List<string> Split(JsonNode? value) =>
        Clean(value).Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

    static bool Failed(JsonNode? data) => data == null || J.S(data["Response"]) == "False";

    public override List<Candidate> Search(string title, int? year, string kind)
    {
        var type = kind == "movie" ? "movie" : "series";
        var data = Http.GetJson(Api, new (string, object?)[] { ("apikey", Key()), ("s", title), ("y", year), ("type", type) });
        if (Failed(data))
        {
            if (year != null) data = Http.GetJson(Api, new (string, object?)[] { ("apikey", Key()), ("s", title), ("type", type) });
            if (Failed(data)) return new List<Candidate>();
        }
        return J.A(data!["Search"]).Take(8).Select(item =>
        {
            var y = Clean(item["Year"]);
            return new Candidate
            {
                Id = J.S(item["imdbID"]), Title = Clean(item["Title"]),
                Year = y.Length >= 4 ? J.YearOf(y[..4]) : null, Popularity = 0, Poster = Clean(item["Poster"]),
            };
        }).ToList();
    }

    public override MetaResult? Fetch(object? id, string kind)
    {
        var imdbId = Convert.ToString(id) ?? "";
        if (imdbId.Length == 0) return null;
        var data = Http.GetJson(Api, new (string, object?)[] { ("apikey", Key()), ("i", imdbId), ("plot", "full") });
        if (Failed(data)) return null;
        var yearTxt = Clean(data!["Year"]);
        var rating = J.NormRating(data["imdbRating"]);
        if (rating == null)
            foreach (var r in J.A(data["Ratings"]))
                if (J.S(r["Source"]) == "Rotten Tomatoes")
                {
                    rating = J.NormRating(Clean(r["Value"]).TrimEnd('%'), 100);
                    break;
                }
        var votes = Clean(data["imdbVotes"]).Replace(",", "");
        var runtime = Clean(data["Runtime"]).Split(' ')[0];
        var res = new MetaResult
        {
            Source = Name, Kind = kind, Title = Clean(data["Title"]),
            Year = yearTxt.Length >= 4 ? J.YearOf(yearTxt[..4]) : null, Overview = Clean(data["Plot"]),
            Certificate = Clean(data["Rated"]), Rating = rating,
            Votes = votes.Length > 0 && votes.All(char.IsDigit) ? long.Parse(votes) : null,
            Runtime = runtime.Length > 0 && runtime.All(char.IsDigit) ? int.Parse(runtime) : null,
            ReleaseDate = Clean(data["Released"]), Genres = Split(data["Genre"]), Poster = Clean(data["Poster"]),
            ImdbId = imdbId.StartsWith("tt") ? imdbId : "",
        };
        if (Clean(data["Country"]).ToLowerInvariant().Contains("japan")) res.Countries.Add("JP");
        if (Clean(data["Language"]).ToLowerInvariant().Contains("japanese")) res.Languages.Add("ja");
        res.Cast = Split(data["Actors"]).Select((n, i) => new PersonInfo { Name = n, Order = i }).ToList();
        res.Crew = Split(data["Director"]).Select(n => new PersonInfo { Name = n, Character = "Director" })
            .Concat(Split(data["Writer"]).Select(n => new PersonInfo { Name = n, Character = "Writer" })).ToList();
        var total = Clean(data["totalSeasons"]);
        if (kind == "show" && total.Length > 0 && total.All(char.IsDigit))
            res.Seasons = Enumerable.Range(1, int.Parse(total)).Select(n => new SeasonInfo { Number = n }).ToList();
        return res;
    }

    public override List<EpisodeInfo>? Episodes(MetaIds ids, int season)
    {
        if (string.IsNullOrEmpty(ids.ImdbId)) return null;
        var data = Http.GetJson(Api, new (string, object?)[] { ("apikey", Key()), ("i", ids.ImdbId), ("Season", season) });
        if (Failed(data)) return new List<EpisodeInfo>();
        var list = new List<EpisodeInfo>();
        foreach (var e in J.A(data!["Episodes"]))
        {
            var num = Clean(e["Episode"]);
            if (num.Length == 0 || !num.All(char.IsDigit)) continue;
            list.Add(new EpisodeInfo
            {
                Season = season, Number = int.Parse(num), Title = Clean(e["Title"]),
                AirDate = Clean(e["Released"]), Rating = J.NormRating(e["imdbRating"]),
            });
        }
        return list;
    }
}

public static class Registry
{
    public static readonly Dictionary<string, Provider> All = new()
    {
        ["tmdb"] = new TmdbProvider(),
        ["tvmaze"] = new TvmazeProvider(),
        ["omdb"] = new OmdbProvider(),
    };
}
