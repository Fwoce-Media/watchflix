using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace WatchFlix.Core.Providers;

/// <summary>The provider could not be reached or replied with a server error.</summary>
public class ProviderError : Exception
{
    public ProviderError(string message) : base(message) { }
}

/// <summary>The provider needs an API key that has not been set, or rejected it.</summary>
public sealed class NotConfigured : ProviderError
{
    public NotConfigured(string message) : base(message) { }
}

public sealed class PersonInfo
{
    public string Name { get; set; } = "";
    public string Character { get; set; } = "";
    public int Order { get; set; }
    public string Bio { get; set; } = "";
    public string Birthday { get; set; } = "";
    public string Deathday { get; set; } = "";
    public string Birthplace { get; set; } = "";
    public string Photo { get; set; } = "";
    public long? TmdbId { get; set; }
    public string ImdbId { get; set; } = "";
}

public sealed class EpisodeInfo
{
    public int Season { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Overview { get; set; } = "";
    public string AirDate { get; set; } = "";
    public double? Rating { get; set; }
    public int? Runtime { get; set; }
    public string Still { get; set; } = "";
}

public sealed class SeasonInfo
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Overview { get; set; } = "";
    public string AirDate { get; set; } = "";
    public string Poster { get; set; } = "";
}

/// <summary>A search hit, before the full record is fetched.</summary>
public sealed class Candidate
{
    public object? Id { get; set; }                // long for TMDB/TVmaze, "tt…" for OMDb
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public double Popularity { get; set; }
    public string Poster { get; set; } = "";
    public List<string> AltTitles { get; set; } = new();
    public string Provider { get; set; } = "";
    public double Score { get; set; }

    public Dictionary<string, object?> ToJson() => new()
    {
        ["id"] = Id, ["title"] = Title, ["year"] = Year, ["popularity"] = Popularity,
        ["poster"] = Poster, ["provider"] = Provider, ["score"] = Score,
    };
}

/// <summary>One provider's answer, before merging.</summary>
public sealed class MetaResult
{
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "movie";
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string Overview { get; set; } = "";
    public string Tagline { get; set; } = "";
    public string Certificate { get; set; } = "";
    public double? Rating { get; set; }
    public long? Votes { get; set; }
    public double Popularity { get; set; }
    public int? Runtime { get; set; }
    public string ReleaseDate { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> Genres { get; set; } = new();
    public List<PersonInfo> Cast { get; set; } = new();
    public List<PersonInfo> Crew { get; set; } = new();
    public List<SeasonInfo> Seasons { get; set; } = new();
    public List<EpisodeInfo> Episodes { get; set; } = new();
    public string Poster { get; set; } = "";
    public string Backdrop { get; set; } = "";
    public string Trailer { get; set; } = "";
    public long? TmdbId { get; set; }
    public string ImdbId { get; set; } = "";
    public long? TvmazeId { get; set; }
    public long? CollectionId { get; set; }
    public string CollectionName { get; set; } = "";
    public List<Dictionary<string, string>> Artwork { get; set; } = new();
    public List<string> Countries { get; set; } = new();
    public List<string> Languages { get; set; } = new();
    public List<string> Keywords { get; set; } = new();

    /// <summary>How much this result actually carries, out of seven.</summary>
    public int Score() => new[]
    {
        Overview.Length > 0, Certificate.Length > 0, Genres.Count > 0, Cast.Count > 0,
        Poster.Length > 0, ReleaseDate.Length > 0, Rating is > 0,
    }.Count(b => b);
}

public abstract class Provider
{
    public abstract string Name { get; }
    public virtual string[] Supports => new[] { "movie", "show" };
    public virtual bool NeedsKey => false;
    public virtual bool Configured() => true;

    public abstract List<Candidate> Search(string title, int? year, string kind);
    public abstract MetaResult? Fetch(object? id, string kind);
    public virtual PersonInfo? Person(string name, long? tmdbId) => null;

    /// <summary>Episode detail for a season, looked up by this provider's own id for the show.</summary>
    public virtual List<EpisodeInfo>? Episodes(MetaIds ids, int season) => null;
}

/// <summary>The ids a show is known by, so any provider can find its episodes.</summary>
public sealed record MetaIds(long? TmdbId, long? TvmazeId, string ImdbId, string Title);

public static class Http
{
    public const string UserAgent = "WatchFlix/1.0 (personal media library)";

    static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Config.ProviderTimeout,
    };

    /// <summary>Python's urlencode: spaces become +, empty values are left out.</summary>
    public static string Query(IEnumerable<(string Key, object? Value)> pairs)
    {
        var parts = new List<string>();
        foreach (var (key, value) in pairs)
        {
            if (value == null) continue;
            var text = value switch
            {
                bool b => b ? "True" : "False",
                double d => d.ToString(CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            };
            if (text.Length == 0) continue;
            parts.Add($"{WebUtility.UrlEncode(key)}={WebUtility.UrlEncode(text)}");
        }
        return string.Join("&", parts);
    }

    public static JsonNode? GetJson(string url, IEnumerable<(string, object?)>? query = null)
    {
        if (query != null)
        {
            var qs = Query(query);
            if (qs.Length > 0) url = $"{url}?{qs}";
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        HttpResponseMessage response;
        try
        {
            response = Client.Send(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ProviderError(ex.Message);
        }
        using (response)
        {
            var code = (int)response.StatusCode;
            if (code == 404) return null;
            if (code is 401 or 403) throw new NotConfigured($"HTTP {code} — check the API key");
            if (code >= 400) throw new ProviderError($"HTTP {code}");
            string text;
            try
            {
                using var stream = response.Content.ReadAsStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                text = reader.ReadToEnd();
            }
            catch (Exception ex) { throw new ProviderError(ex.Message); }
            try { return JsonNode.Parse(text); }
            catch (Exception ex) { throw new ProviderError($"bad JSON: {ex.Message}"); }
        }
    }

    public static byte[]? GetBytes(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "WatchFlix/1.0");
            using var response = Client.Send(request);
            if (!response.IsSuccessStatusCode) return null;
            using var stream = response.Content.ReadAsStream();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch { return null; }
    }
}

/// <summary>Reading loosely-typed JSON the way the Python build did: missing or null means empty.</summary>
public static class J
{
    public static string S(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out _)) return "";
            return v.ToJsonString().Trim('"');
        }
        return "";
    }

    public static long? L(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var p)) return p;
        }
        return null;
    }

    public static int? I(JsonNode? node) => L(node) is long l ? (int)l : null;

    public static double? D(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<string>(out var s) &&
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
        }
        return null;
    }

    public static IEnumerable<JsonNode> A(JsonNode? node) =>
        node is JsonArray arr ? arr.Where(n => n != null).Select(n => n!) : Enumerable.Empty<JsonNode>();

    public static int? YearOf(string date) =>
        date.Length >= 4 && date[..4].All(char.IsDigit) ? int.Parse(date[..4], CultureInfo.InvariantCulture) : null;

    /// <summary>Every provider's rating on the same 0-10 scale.</summary>
    public static double? NormRating(object? value, double scale = 10.0)
    {
        var text = value switch
        {
            null => "",
            JsonNode n => S(n),
            double d => d.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
        text = text.Split('/')[0].Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return null;
        if (num <= 0) return null;
        if (scale != 10.0) num = num * 10.0 / scale;
        return Math.Round(Math.Min(num, 10.0), 1, MidpointRounding.ToEven);
    }
}

/// <summary>
/// Python's difflib.SequenceMatcher(None, a, b).ratio(), which the Python build
/// used to rank search hits. Ported so the same filename picks the same match.
/// </summary>
public static class Similarity
{
    static string Norm(string s) =>
        new string(s.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();

    public static double TitleSimilarity(string a, string b) => Ratio(Norm(a), Norm(b));

    public static double Ratio(string a, string b)
    {
        var total = a.Length + b.Length;
        if (total == 0) return 1.0;
        var matches = MatchingBlocks(a, b).Sum(m => m.Size);
        return 2.0 * matches / total;
    }

    record struct Match(int A, int B, int Size);

    static List<Match> MatchingBlocks(string a, string b)
    {
        // b2j with difflib's autojunk rule for long sequences.
        var b2j = new Dictionary<char, List<int>>();
        for (var i = 0; i < b.Length; i++)
        {
            if (!b2j.TryGetValue(b[i], out var list)) b2j[b[i]] = list = new List<int>();
            list.Add(i);
        }
        var n = b.Length;
        if (n >= 200)
        {
            var ntest = n / 100 + 1;
            foreach (var key in b2j.Where(kv => kv.Value.Count > ntest).Select(kv => kv.Key).ToList())
                b2j.Remove(key);
        }

        var queue = new Stack<(int, int, int, int)>();
        queue.Push((0, a.Length, 0, b.Length));
        var blocks = new List<Match>();
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var m = LongestMatch(a, b, b2j, alo, ahi, blo, bhi);
            if (m.Size == 0) continue;
            blocks.Add(m);
            if (alo < m.A && blo < m.B) queue.Push((alo, m.A, blo, m.B));
            if (m.A + m.Size < ahi && m.B + m.Size < bhi) queue.Push((m.A + m.Size, ahi, m.B + m.Size, bhi));
        }
        return blocks;
    }

    static Match LongestMatch(string a, string b, Dictionary<char, List<int>> b2j,
                              int alo, int ahi, int blo, int bhi)
    {
        int besti = alo, bestj = blo, bestsize = 0;
        var j2len = new Dictionary<int, int>();
        for (var i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var indices))
            {
                foreach (var j in indices)
                {
                    if (j < blo) continue;
                    if (j >= bhi) break;
                    var k = (j2len.TryGetValue(j - 1, out var prev) ? prev : 0) + 1;
                    newj2len[j] = k;
                    if (k > bestsize)
                    {
                        besti = i - k + 1;
                        bestj = j - k + 1;
                        bestsize = k;
                    }
                }
            }
            j2len = newj2len;
        }
        // No junk function is given, so difflib extends the match over any equal
        // neighbours — which only matters for characters autojunk dropped.
        while (besti > alo && bestj > blo && a[besti - 1] == b[bestj - 1])
        {
            besti--; bestj--; bestsize++;
        }
        while (besti + bestsize < ahi && bestj + bestsize < bhi && a[besti + bestsize] == b[bestj + bestsize])
            bestsize++;
        return new Match(besti, bestj, bestsize);
    }
}
