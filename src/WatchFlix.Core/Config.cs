using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WatchFlix.Core;

/// <summary>Paths and tunables. The data folder is the same one the Python build used.</summary>
public static class Config
{
    public const string AppName = "WatchFlix";
    public const string AppVersion = "4.1.0";

    public static readonly string AppHome = ResolveHome();

    /// <summary>
    /// Portable mode: a portable.txt beside the program keeps the whole library
    /// in a Data folder next to it, so a copy on a USB drive carries everything
    /// and leaves nothing on the machine it runs on.
    /// </summary>
    public static bool IsPortable { get; private set; }

    static string ResolveHome()
    {
        if (Environment.GetEnvironmentVariable("WATCHFLIX_HOME") is { Length: > 0 } custom) return custom;
        var here = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(here, "portable.txt")))
        {
            IsPortable = true;
            return Path.Combine(here, "Data");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".watchflix");
    }

    public static string DbPath => Path.Combine(AppHome, "library.db");
    public static string SettingsPath => Path.Combine(AppHome, "settings.json");
    public static string CacheDir => Path.Combine(AppHome, "cache");
    public static string ThumbDir => Path.Combine(CacheDir, "thumbs");
    public static string PreviewDir => Path.Combine(CacheDir, "previews");
    public static string ShotDir => Path.Combine(CacheDir, "shots");
    public static string ImageDir => Path.Combine(AppHome, "images");
    public static string PosterDir => Path.Combine(ImageDir, "posters");
    public static string BackdropDir => Path.Combine(ImageDir, "backdrops");
    public static string PersonDir => Path.Combine(ImageDir, "people");
    public static string StillDir => Path.Combine(ImageDir, "stills");
    public static string LogPath => Path.Combine(AppHome, "watchflix.log");

    public static void EnsureFolders()
    {
        foreach (var dir in new[] { AppHome, CacheDir, ThumbDir, PreviewDir, ShotDir,
                                    ImageDir, PosterDir, BackdropDir, PersonDir, StillDir })
            Directory.CreateDirectory(dir);
    }

    // Bumped whenever filename parsing changes. Kept in step with the Python
    // build so a library it scanned is not re-read for no reason.
    public const int ParserVersion = 4;

    public static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".webm", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts",
    };

    public static readonly HashSet<string> SkipDirNames = new(StringComparer.Ordinal)
    {
        "sample", "samples", "extras", "featurettes", "trailers", "behind the scenes",
        "deleted scenes", "@eadir", ".git",
    };

    public const int PreviewSegments = 6;
    public const double PreviewSegSeconds = 1.2;
    public const int ThumbWidth = 640;
    public const int SnapshotCount = 4;
    public const int SnapshotWidth = 480;

    public const double CompleteAt = 0.92;
    public const double ResumeMin = 0.01;

    public const int ProviderCooldownSeconds = 300;
    public const int ProviderFailuresBeforeCooldown = 3;
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(12);

    public record PreviewPreset(int Width, int Crf, int Fps, string Preset);

    public static readonly Dictionary<string, PreviewPreset?> PreviewPresets = new()
    {
        ["off"] = null,
        ["low"] = new(320, 32, 12, "veryfast"),
        ["medium"] = new(480, 28, 18, "veryfast"),
        ["high"] = new(720, 23, 24, "fast"),
        ["ultra"] = new(1080, 20, 24, "medium"),
    };
    public const string PreviewQualityDefault = "medium";

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static string Sha1Hex(string text, int length)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant()[..length];
    }

    public static string F(double value, int decimals) =>
        value.ToString("F" + decimals, CultureInfo.InvariantCulture);

    public static string Inv(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}

/// <summary>
/// settings.json, shared with the Python build. Keys this build does not use are
/// kept as they are, so going back to the old build loses nothing.
/// </summary>
public static class Settings
{
    static readonly object Gate = new();
    static JsonObject? _cache;

    public static readonly string[] ProviderNames = { "tmdb", "tvmaze", "omdb" };

    static JsonObject Defaults() => new()
    {
        ["library_roots"] = new JsonArray(),
        ["movie_roots"] = new JsonArray(),
        ["show_roots"] = new JsonArray(),
        ["tmdb_api_key"] = "",
        ["omdb_api_key"] = "",
        ["provider_order"] = new JsonArray("tmdb", "tvmaze", "omdb"),
        ["auto_scrape_on_scan"] = true,
        ["download_images"] = true,
        ["preview_quality"] = Config.PreviewQualityDefault,
        ["language"] = "en-US",
        ["auto_next"] = false,
    };

    public static event Action? Changed;

    static JsonObject Load()
    {
        lock (Gate)
        {
            if (_cache != null) return _cache;
            var data = Defaults();
            try
            {
                if (File.Exists(Config.SettingsPath))
                {
                    var stored = JsonNode.Parse(File.ReadAllText(Config.SettingsPath)) as JsonObject;
                    if (stored != null)
                        foreach (var (key, value) in stored)
                            data[key] = value?.DeepClone();
                }
            }
            catch (Exception) { /* unreadable file: defaults stand */ }

            // A provider added after the file was written would never be tried.
            var order = new List<string>();
            if (data["provider_order"] is JsonArray arr)
                foreach (var n in arr)
                    if (n?.GetValue<string>() is { } name && ProviderNames.Contains(name) && !order.Contains(name))
                        order.Add(name);
            foreach (var name in ProviderNames)
                if (!order.Contains(name)) order.Add(name);
            data["provider_order"] = new JsonArray(order.Select(o => (JsonNode)o).ToArray());
            _cache = data;
            return data;
        }
    }

    public static JsonObject Snapshot()
    {
        lock (Gate) return (JsonObject)Load().DeepClone();
    }

    public static void Save(IDictionary<string, JsonNode?> patch)
    {
        lock (Gate)
        {
            var data = Load();
            foreach (var (key, value) in patch)
                data[key] = value?.DeepClone();
            Directory.CreateDirectory(Config.AppHome);
            var tmp = Config.SettingsPath + ".tmp";
            File.WriteAllText(tmp, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Config.SettingsPath, true);
        }
        Changed?.Invoke();
    }

    public static void Set(string key, JsonNode? value) =>
        Save(new Dictionary<string, JsonNode?> { [key] = value });

    public static string Str(string key, string fallback = "")
    {
        lock (Gate)
        {
            var node = Load()[key];
            if (node is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s)) return s;
                return v.ToJsonString().Trim('"');
            }
            return fallback;
        }
    }

    public static bool Bool(string key, bool fallback = false)
    {
        lock (Gate)
        {
            var node = Load()[key];
            if (node is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<long>(out var l)) return l != 0;
                if (v.TryGetValue<string>(out var s)) return s is "1" or "true" or "True";
            }
            return fallback;
        }
    }

    public static int Int(string key, int fallback = 0)
    {
        lock (Gate)
        {
            var node = Load()[key];
            if (node is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) return i;
                if (v.TryGetValue<long>(out var l)) return (int)l;
                if (v.TryGetValue<double>(out var d)) return (int)d;
                if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
            }
            return fallback;
        }
    }

    public static List<string> List(string key)
    {
        lock (Gate)
        {
            var result = new List<string>();
            if (Load()[key] is JsonArray arr)
                foreach (var n in arr)
                    if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                        result.Add(s);
            return result;
        }
    }

    public static void SetList(string key, IEnumerable<string> values) =>
        Set(key, new JsonArray(values.Select(v => (JsonNode)v).ToArray()));

    /// <summary>Every configured root, with the kind it forces (null = decide per file).</summary>
    public static List<(string Path, string? Kind)> AllRoots()
    {
        var roots = new List<(string, string?)>();
        roots.AddRange(List("library_roots").Select(p => (p, (string?)null)));
        roots.AddRange(List("movie_roots").Select(p => (p, (string?)"movie")));
        roots.AddRange(List("show_roots").Select(p => (p, (string?)"show")));
        return roots;
    }

    /// <summary>Forget the cached copy so the next read comes from disk.</summary>
    public static void Reload()
    {
        lock (Gate) _cache = null;
    }
}
