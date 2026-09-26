using System.Text.RegularExpressions;

namespace WatchFlix.Core.Library;

/// <summary>What a release filename says: title, year, season and episode.</summary>
public sealed class Parsed
{
    public string Kind { get; set; } = "movie";          // movie | show
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public int? EpisodeEnd { get; set; }
    public string EpisodeTitle { get; set; } = "";
    public string Edition { get; set; } = "";
    public double Confidence { get; set; } = 0.5;
    public List<string> Notes { get; set; } = new();
}

/// <summary>
/// Turns a release filename into a searchable title, year, season and episode.
/// A line-for-line port of the Python build's names.py, so a library scanned by
/// either build files every title the same way.
/// </summary>
public static class Names
{
    const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const RegexOptions N = RegexOptions.CultureInvariant;

    static readonly HashSet<string> Junk = new(StringComparer.Ordinal)
    {
        "480p", "540p", "576p", "720p", "1080p", "1440p", "2160p", "4320p",
        "4k", "8k", "uhd", "hd", "sd", "hdr", "hdr10", "hdr10plus", "dv",
        "dolbyvision", "sdr", "remux", "bluray", "blu-ray", "brrip", "bdrip",
        "bdremux", "bd", "dvdrip", "dvdscr", "dvd", "hdtv", "pdtv", "sdtv", "webrip",
        "web-dl", "webdl", "web", "hdrip", "cam", "camrip", "ts", "tc", "r5",
        "hdcam", "telesync", "screener", "workprint", "vodrip", "amzn", "nf",
        "dsnp", "hmax", "atvp", "hulu", "pcok", "stan", "itunes", "ithd",
        "x264", "x265", "h264", "h265", "h", "264", "265", "avc", "hevc", "xvid",
        "divx", "mpeg2", "vp9", "av1", "10bit", "8bit", "12bit", "hi10p",
        "aac", "aac2", "ac3", "eac3", "ddp", "dd5", "ddp5", "dd51", "dts", "dtshd",
        "dts-hd", "truehd", "atmos", "flac", "opus", "dual", "audio", "dubbed",
        "subbed", "multi", "vostfr",
        "proper", "repack", "internal", "limited", "unrated", "uncut", "extended",
        "remastered", "restored", "theatrical", "directors", "director", "cut",
        "criterion", "imax", "open", "matte", "complete", "readnfo",
        "nfo", "rerip", "hybrid", "sample",
    };

    // Junk only in context: real words too, so a title may open with one.
    static readonly HashSet<string> WeakJunk = new(StringComparer.Ordinal)
        { "bd", "ts", "web", "dd", "open", "matte", "cam", "tc", "dv", "hd", "sd" };

    static readonly Regex GroupTail = new(@"-[A-Za-z0-9_.]{2,20}$", N);
    public static readonly Regex Year = new(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)", N);
    static readonly Regex YearFull = new(@"^(?:19\d{2}|20\d{2})\z", N);
    static readonly Regex ResolutionTag = new(@"^\d{3,4}p\z", I);

    static readonly Regex[] EpisodePatterns =
    {
        new(@"\bs(?<s>\d{1,2})[\s._-]*e(?<e>\d{1,3})(?:[\s._-]*e\d{1,3})?\b", I),
        new(@"\bseason[\s._-]*(?<s>\d{1,2})[\s._-]*episode[\s._-]*(?<e>\d{1,3})\b", I),
        new(@"(?<![a-z0-9])(?<s>\d{1,2})x(?<e>\d{1,3})(?![a-z0-9])", I),
        new(@"\bs(?<s>\d{1,2})[\s._-]*ep(?<e>\d{1,3})\b", I),
        new(@"\[(?<s>\d{1,2})[\s._-]*(?<e>\d{2})\]", I),
    };

    static readonly Regex SeasonOnlyFull = new(@"^(?:\b(?:s|season|series)[\s._-]*(\d{1,2})\b)\z", I);
    static readonly Regex SeasonLooseFull = new(@"^\s*(?:season|series|s)[\s._-]*(\d{1,2})\s*\z", I);

    static readonly Regex[] BareEpisode =
    {
        new(@"\bep?(?<e>\d{1,3})\b", I),
        new(@"(?<![\d])(?<e>\d{1,3})(?![\d])", N),
    };

    static readonly Regex MultiEp = new(@"s\d{1,2}[\s._-]*e\d{1,3}[\s._-]*e(\d{1,3})", I);

    static readonly Regex SeasonDir = new(@"^\s*(?:season|series|s)[\s._-]*(\d{1,3})\s*\z", I);
    static readonly Regex SpecialsDir = new(@"^\s*specials?\s*\z", I);
    static readonly Regex NamedSeasonDir =
        new(@"^(?<title>.+?)[\s._-]+(?:season|series|s)[\s._-]*(?<n>\d{1,3})\s*\z", I);

    static readonly Regex Brackets = new(@"[\[\](){}]", N);
    static readonly Regex DotsUnderscores = new(@"[._]+", N);
    static readonly Regex SpacedDash = new(@"\s+-\s+", N);
    static readonly Regex Spaces = new(@"\s+", N);
    static readonly Regex TrailingPunct = new(@"[\s\-–—_.]+$", N);
    static readonly Regex LeadingPunct = new(@"^[\s\-–—_.]+", N);
    static readonly Regex DoubleSpace = new(@"\s{2,}", N);
    static readonly Regex ArticleLast = new(@"^(.*), (The|A|An)$", I);
    static readonly Regex NonAlnum = new(@"[^a-z0-9]+", N);
    static readonly Regex FolderEpisodePattern = new(@"^(?:e|ep|episode|part)?\s*(\d{1,4})(?:v\d)?\b", N);
    static readonly Regex LeadingArticle = new(@"^(the|a|an)\s+", N);

    static List<string> SplitTokens(string text)
    {
        text = Brackets.Replace(text, " ");
        text = DotsUnderscores.Replace(text, " ");
        text = SpacedDash.Replace(text, " - ");
        return Spaces.Split(text).Where(t => t.Length > 0).ToList();
    }

    /// <summary>Drop everything from the first junk token onwards, then tidy.</summary>
    public static string CleanTitle(string raw)
    {
        raw = GroupTail.Replace(raw.Trim(), "", 1);
        var kept = new List<string>();
        foreach (var tok in SplitTokens(raw))
        {
            var bare = tok.Trim('-').ToLowerInvariant();
            if (Junk.Contains(bare) || ResolutionTag.IsMatch(bare))
            {
                if (kept.Count == 0)
                {
                    if (WeakJunk.Contains(bare)) kept.Add(tok);
                    continue;
                }
                break;
            }
            kept.Add(tok);
        }
        var title = string.Join(" ", kept);
        title = TrailingPunct.Replace(title, "");
        title = LeadingPunct.Replace(title, "");
        title = DoubleSpace.Replace(title, " ");
        var m = ArticleLast.Match(title);
        if (m.Success) title = $"{m.Groups[2].Value} {m.Groups[1].Value}";
        return title.Trim();
    }

    static string DetectEdition(string text)
    {
        var low = text.ToLowerInvariant();
        (string, string[])[] editions =
        {
            ("Extended", new[] { "extended" }),
            ("Director's Cut", new[] { "directors cut", "director's cut", "dircut" }),
            ("Theatrical", new[] { "theatrical" }),
            ("Unrated", new[] { "unrated" }),
            ("Remastered", new[] { "remaster" }),
            ("IMAX", new[] { "imax" }),
        };
        foreach (var (label, needles) in editions)
            if (needles.Any(low.Contains)) return label;
        return "";
    }

    /// <summary>The folders above a file, nearest first — Python's path.parents.</summary>
    static List<string> ParentNames(string path, int count)
    {
        var names = new List<string>();
        var dir = Path.GetDirectoryName(path);
        while (dir != null && names.Count < count)
        {
            names.Add(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "");
            var up = Path.GetDirectoryName(dir);
            if (up == dir) break;
            dir = up;
        }
        return names;
    }

    public static string ParentName(string path)
    {
        var dir = Path.GetDirectoryName(path);
        return dir == null ? "" : Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    static (int? Season, string Show) SeasonFromParents(string path)
    {
        int? season = null;
        var showName = "";
        foreach (var name in ParentNames(path, 3))
        {
            if (string.IsNullOrEmpty(name)) continue;
            var m = SeasonOnlyFull.Match(name.Trim());
            if (!m.Success) m = SeasonLooseFull.Match(name);
            if (m.Success && season == null)
            {
                season = int.Parse(m.Groups[1].Value);
                continue;
            }
            if (season != null && showName.Length == 0)
            {
                showName = name;
                break;
            }
            if (showName.Length == 0 && !m.Success) showName = name;
        }
        return (season, showName);
    }

    public static Parsed Parse(string path, string? forceKind = null)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var edition = DetectEdition(stem);

        int? season = null, episode = null, epEnd = null;
        string head = stem, tail = "";
        foreach (var pattern in EpisodePatterns)
        {
            var m = pattern.Match(stem);
            if (!m.Success) continue;
            season = int.Parse(m.Groups["s"].Value);
            episode = int.Parse(m.Groups["e"].Value);
            head = stem[..m.Index];
            tail = stem[(m.Index + m.Length)..];
            var dbl = MultiEp.Match(stem);
            if (dbl.Success) epEnd = int.Parse(dbl.Groups[1].Value);
            break;
        }

        var (folderSeason, folderShow) = SeasonFromParents(path);
        var (namedTitle, namedSeason) = SeasonFolder(ParentName(path));
        if (namedSeason != null && namedTitle.Length > 0)
        {
            folderSeason ??= namedSeason;
            if (folderShow.Length == 0) folderShow = namedTitle;
        }

        if (episode == null && folderSeason != null)
        {
            foreach (var pattern in BareEpisode)
            {
                var m = pattern.Match(stem);
                if (!m.Success) continue;
                var text = m.Groups["e"].Value;
                var candidate = int.Parse(text);
                if (candidate > 0 && candidate < 400 && !YearFull.IsMatch(text))
                {
                    season = folderSeason;
                    episode = candidate;
                    head = stem[..m.Index];
                    tail = stem[(m.Index + m.Length)..];
                    break;
                }
            }
        }

        var isShow = episode != null || forceKind == "show";
        if (forceKind == "movie")
        {
            isShow = false;
            season = episode = epEnd = null;
        }

        int? year = null;
        var haystack = isShow ? head : stem;
        var cutoff = DateTime.Today.Year + 2;
        var hits = Year.Matches(haystack).Where(m => int.Parse(m.Groups[1].Value) <= cutoff).ToList();
        if (hits.Count > 0)
        {
            var ym = hits[^1];
            year = int.Parse(ym.Groups[1].Value);
            var trimmed = haystack[..ym.Index];
            if (CleanTitle(trimmed).Length > 0) head = trimmed;
        }

        var title = CleanTitle(head);
        var notes = new List<string>();

        if (isShow && title.Length < 2)
        {
            title = CleanTitle(folderShow);
            if (title.Length == 0) title = CleanTitle(ParentName(path));
            notes.Add("title taken from folder");
        }
        if (!isShow && title.Length == 0)
        {
            title = CleanTitle(ParentName(path));
            if (title.Length == 0) title = stem;
            notes.Add("title taken from folder");
        }

        var episodeTitle = isShow ? CleanTitle(tail) : "";
        if (episodeTitle.ToLowerInvariant() is "" or "-" || episodeTitle.Length < 2) episodeTitle = "";

        var confidence = 0.5;
        if (isShow && episode != null) confidence = season != null ? 0.9 : 0.6;
        if (!isShow && year != null) confidence = 0.85;
        if (title.Length > 0 && title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 2)
            confidence = Math.Min(1.0, confidence + 0.05);
        if (title.Length == 0) confidence = 0.1;

        return new Parsed
        {
            Kind = isShow ? "show" : "movie",
            Title = title,
            Year = year,
            Season = season,
            Episode = episode,
            EpisodeEnd = epEnd,
            EpisodeTitle = episodeTitle,
            Edition = edition,
            Confidence = Math.Round(confidence, 2),
            Notes = notes,
        };
    }

    static string Normalise(string text) =>
        Spaces.Replace(NonAlnum.Replace(text.ToLowerInvariant(), " "), " ").Trim();

    /// <summary>Split a folder name into the show it names and the season it marks.</summary>
    public static (string Title, int? Season) SeasonFolder(string name)
    {
        name = name.Trim();
        var m = SeasonDir.Match(name);
        if (m.Success) return ("", int.Parse(m.Groups[1].Value));
        if (SpecialsDir.IsMatch(name)) return ("", 0);
        m = NamedSeasonDir.Match(name);
        if (m.Success) return (m.Groups["title"].Value.Trim(), int.Parse(m.Groups["n"].Value));
        return (name, null);
    }

    /// <summary>The show a folder represents, and the season it pins down if any.</summary>
    public static (string Title, int? Season) SeriesFolderTitle(string directory)
    {
        var dirName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var (title, season) = SeasonFolder(dirName);
        if (title.Length == 0)
        {
            var parent = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
            var parentName = Path.GetFileName(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var (parentTitle, _) = SeasonFolder(parentName);
            title = parentTitle.Length > 0 ? parentTitle : parentName;
        }
        return (title, season);
    }

    /// <summary>Read an episode number from a filename that opens with the show's name.</summary>
    public static (int Number, string Title)? FolderEpisode(string stem, string folderTitle)
    {
        var show = Normalise(folderTitle);
        var name = Normalise(stem);
        if (show.Length < 3 || !name.StartsWith(show, StringComparison.Ordinal)) return null;
        var rest = name[show.Length..].Trim();
        if (rest.Length == 0) return null;
        var m = FolderEpisodePattern.Match(rest);
        if (!m.Success) return null;
        var number = int.Parse(m.Groups[1].Value);
        if (number <= 0 || number >= 2000) return null;
        var tail = rest[(m.Index + m.Length)..].Trim();
        if (number >= 1900 && number <= 2100 && tail.Length == 0) return null;
        var title = tail.Length > 0 ? CleanTitle(stem[Math.Max(0, stem.Length - tail.Length)..]) : "";
        return (number, title);
    }


    public static string SortTitle(string title)
    {
        var t = title.Trim().ToLowerInvariant();
        return LeadingArticle.Replace(t, "", 1);
    }
}
