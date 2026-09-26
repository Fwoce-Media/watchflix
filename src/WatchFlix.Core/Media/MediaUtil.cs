using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WatchFlix.Core.Media;

public sealed record ProbeInfo(double? Duration, int? Width, int? Height, string VCodec, string ACodec);


/// <summary>
/// ffmpeg work: probing, poster frames, hover previews, frames under the player,
/// subtitles and audio tracks. Everything degrades quietly without ffmpeg.
/// </summary>
public static class MediaUtil
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The cache key for a file's artwork: the Python build's, so its thumbnails are reused.</summary>
    public static string KeyFor(string path) => Config.Sha1Hex(path, 20);

    static JsonNode? ParseJson(string text)
    {
        try { return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); }
        catch { return null; }
    }

    static string S(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            return v.ToJsonString().Trim('"');
        }
        return "";
    }

    static int I(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return (int)l;
            if (v.TryGetValue<double>(out var d)) return (int)d;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        }
        return 0;
    }

    static bool B(JsonNode? node) => I(node) != 0 || (node is JsonValue v && v.TryGetValue<bool>(out var b) && b);

    public static ProbeInfo Probe(string path)
    {
        if (!Ffmpeg.HasFfprobe) return new ProbeInfo(null, null, null, "", "");
        var result = Ffmpeg.RunFfprobe(new[] { "-v", "error", "-print_format", "json",
                                               "-show_format", "-show_streams", path }, 60);
        var data = ParseJson(result.StdOut);
        if (data == null) return new ProbeInfo(null, null, null, "", "");
        double? duration = null;
        if (double.TryParse(S(data["format"]?["duration"]), NumberStyles.Float, Inv, out var d)) duration = d;
        int? width = null, height = null;
        string vcodec = "", acodec = "";
        bool haveVideo = false, haveAudio = false;
        if (data["streams"] is JsonArray streams)
            foreach (var stream in streams)
            {
                var type = S(stream?["codec_type"]);
                if (type == "video" && !haveVideo)
                {
                    haveVideo = true;
                    width = stream?["width"] != null ? I(stream["width"]) : null;
                    height = stream?["height"] != null ? I(stream["height"]) : null;
                    vcodec = S(stream?["codec_name"]);
                }
                else if (type == "audio" && !haveAudio)
                {
                    haveAudio = true;
                    acodec = S(stream?["codec_name"]);
                }
            }
        return new ProbeInfo(duration, width, height, vcodec, acodec);
    }

    /// <summary>Resolution tier, led by width so a letterboxed scope film is not called 720p.</summary>
    public static string QualityBadge(long? width, long? height = null)
    {
        var w = width ?? 0;
        var h = height ?? 0;
        if (w == 0 && h == 0) return "";
        if (w >= 3400 || h >= 2000) return "4K";
        if (w >= 2400 || h >= 1400) return "2K";
        if (w >= 1800 || h >= 1000) return "1080p";
        if (w >= 1200 || h >= 700) return "720p";
        if (w >= 900 || h >= 560) return "576p";
        return "SD";
    }

    public static string MakeThumb(string path, double? duration = null, double? at = null, bool force = false)
    {
        if (!Ffmpeg.HasFfmpeg) return "";
        var dest = Path.Combine(Config.ThumbDir, $"{KeyFor(path)}.jpg");
        if (File.Exists(dest) && !force) return $"/media/thumb/{Path.GetFileName(dest)}";
        var when = at ?? (duration is > 0 ? Math.Max(3.0, duration.Value * 0.18) : 30.0);
        var r = Ffmpeg.RunFfmpeg(new[] { "-y", "-ss", Config.F(when, 2), "-i", path, "-frames:v", "1",
                                         "-vf", $"scale={Config.ThumbWidth}:-2", "-q:v", "4", dest }, 90);
        if (r.ExitCode != 0 || !File.Exists(dest))
            Ffmpeg.RunFfmpeg(new[] { "-y", "-i", path, "-ss", "5", "-frames:v", "1",
                                     "-vf", $"scale={Config.ThumbWidth}:-2", "-q:v", "4", dest }, 120);
        return File.Exists(dest) ? $"/media/thumb/{Path.GetFileName(dest)}" : "";
    }

    public static (string Name, Config.PreviewPreset? Preset) PreviewSettings()
    {
        var name = Settings.Str("preview_quality", Config.PreviewQualityDefault);
        if (!Config.PreviewPresets.ContainsKey(name)) name = Config.PreviewQualityDefault;
        return (name, Config.PreviewPresets[name]);
    }

    /// <summary>A short silent loop stitched from moments across the file.</summary>
    public static string MakePreview(string path, double? duration, bool force = false)
    {
        var (quality, preset) = PreviewSettings();
        if (preset == null) return "";
        if (!Ffmpeg.HasFfmpeg || duration is null or < 30) return "";
        var dest = Path.Combine(Config.PreviewDir, $"{KeyFor(path)}-{quality}.mp4");
        if (File.Exists(dest) && !force) return $"/media/preview/{Path.GetFileName(dest)}";

        var start = duration.Value * 0.08;
        var span = duration.Value * 0.84;
        var n = Config.PreviewSegments;
        var points = Enumerable.Range(0, n).Select(i => start + span * i / Math.Max(n - 1, 1)).ToList();
        var args = new List<string> { "-y" };
        foreach (var point in points)
            args.AddRange(new[] { "-ss", Config.F(point, 2), "-t", Config.Inv(Config.PreviewSegSeconds), "-i", path });
        var chains = string.Concat(Enumerable.Range(0, points.Count).Select(i =>
            $"[{i}:v]scale={preset.Width}:-2,setsar=1,fps={preset.Fps}[v{i}];"));
        var concat = string.Concat(Enumerable.Range(0, points.Count).Select(i => $"[v{i}]"));
        args.AddRange(new[]
        {
            "-filter_complex", $"{chains}{concat}concat=n={points.Count}:v=1:a=0[out]",
            "-map", "[out]", "-an", "-c:v", "libx264", "-preset", preset.Preset,
            "-crf", preset.Crf.ToString(Inv), "-pix_fmt", "yuv420p", "-movflags", "+faststart", dest,
        });
        var r = Ffmpeg.RunFfmpeg(args, 600);
        if (r.ExitCode != 0)
        {
            TryDelete(dest);
            return "";
        }
        return File.Exists(dest) ? $"/media/preview/{Path.GetFileName(dest)}" : "";
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static void ClearArtwork(string path)
    {
        var key = KeyFor(path);
        TryDelete(Path.Combine(Config.ThumbDir, $"{key}.jpg"));
        try
        {
            foreach (var stale in Directory.EnumerateFiles(Config.PreviewDir, $"{key}-*.mp4"))
                TryDelete(stale);
        }
        catch { }
    }

    public sealed record Snapshot(string Url, double At, string File);

    /// <summary>Even frames across the middle of the file, skipping titles and credits.</summary>
    public static List<Snapshot> MakeSnapshots(string path, double? duration, int count = Config.SnapshotCount,
                                               bool force = false)
    {
        var shots = new List<Snapshot>();
        if (!Ffmpeg.HasFfmpeg || duration is null or < 20) return shots;
        var key = KeyFor(path);
        double spanStart = duration.Value * 0.12, spanEnd = duration.Value * 0.88;
        var step = (spanEnd - spanStart) / Math.Max(count, 1);
        for (var i = 0; i < count; i++)
        {
            var at = spanStart + step * (i + 0.5);
            var dest = Path.Combine(Config.ShotDir, $"{key}-{i}.jpg");
            if (force) TryDelete(dest);
            if (!File.Exists(dest))
                Ffmpeg.RunFfmpeg(new[] { "-y", "-ss", Config.F(at, 2), "-i", path, "-frames:v", "1",
                                         "-vf", $"scale={Config.SnapshotWidth}:-2", "-q:v", "5", dest }, 60);
            if (File.Exists(dest))
                shots.Add(new Snapshot($"/media/shot/{Path.GetFileName(dest)}", Math.Round(at, 2), dest));
        }
        return shots;
    }

    /// <summary>A /media/... URL from the database, turned into the file behind it.</summary>
    public static string? LocalFileFor(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
        string? Pick(string folder, string name)
        {
            if (name.Contains("..") || name.Contains('/') || name.Contains('\\')) return null;
            var full = Path.Combine(folder, name);
            return File.Exists(full) ? full : null;
        }
        var q = url.IndexOf('?');
        if (q >= 0) url = url[..q];
        if (url.StartsWith("/media/thumb/")) return Pick(Config.ThumbDir, url["/media/thumb/".Length..]);
        if (url.StartsWith("/media/preview/")) return Pick(Config.PreviewDir, url["/media/preview/".Length..]);
        if (url.StartsWith("/media/shot/")) return Pick(Config.ShotDir, url["/media/shot/".Length..]);
        if (url.StartsWith("/media/image/"))
        {
            var rest = url["/media/image/".Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) return null;
            var folder = rest[..slash];
            if (folder.Contains("..")) return null;
            return Pick(Path.Combine(Config.ImageDir, folder), rest[(slash + 1)..]);
        }
        return null;
    }
}
