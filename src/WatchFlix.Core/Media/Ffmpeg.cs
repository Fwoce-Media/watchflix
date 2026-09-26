using System.Diagnostics;
using System.Text;

namespace WatchFlix.Core.Media;

/// <summary>
/// Finding and running ffmpeg and ffprobe. Every helper process starts without a
/// window, so none flashes a console on screen.
/// </summary>
public static class Ffmpeg
{
    static string? _ffmpeg, _ffprobe;
    static bool _looked;
    static readonly object Gate = new();

    static string? Find(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var here = AppContext.BaseDirectory;
        // A copy dropped beside the app wins, so nothing has to be put on PATH.
        foreach (var candidate in new[]
                 {
                     Path.Combine(here, exe),
                     Path.Combine(here, "ffmpeg", exe),
                     Path.Combine(here, "ffmpeg", "bin", exe),
                 })
            if (File.Exists(candidate)) return candidate;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    static void Look()
    {
        lock (Gate)
        {
            if (_looked) return;
            _ffmpeg = Find("ffmpeg");
            _ffprobe = Find("ffprobe");
            _looked = true;
        }
    }

    public static void Forget() { lock (Gate) _looked = false; }

    public static string? FfmpegPath { get { Look(); return _ffmpeg; } }
    public static string? FfprobePath { get { Look(); return _ffprobe; } }
    public static bool HasFfmpeg => FfmpegPath != null;
    public static bool HasFfprobe => FfprobePath != null;
    public static bool Available => HasFfmpeg && HasFfprobe;

    public sealed record Result(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        return info;
    }

    /// <summary>Run to completion and collect output. Killed after the timeout.</summary>
    public static Result Run(string exe, IEnumerable<string> args, int timeoutSeconds = 120)
    {
        try
        {
            using var proc = Process.Start(StartInfo(exe, args))!;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutSeconds * 1000))
            {
                try { proc.Kill(true); } catch { }
                return new Result(-1, "", "timed out", true);
            }
            proc.WaitForExit();
            return new Result(proc.ExitCode, stdout.Result, stderr.Result, false);
        }
        catch (Exception ex)
        {
            return new Result(-1, "", ex.Message, false);
        }
    }

    public static Result RunFfmpeg(IEnumerable<string> args, int timeoutSeconds = 120) =>
        FfmpegPath is { } exe ? Run(exe, args, timeoutSeconds) : new Result(-1, "", "ffmpeg not found", false);

    public static Result RunFfprobe(IEnumerable<string> args, int timeoutSeconds = 60) =>
        FfprobePath is { } exe ? Run(exe, args, timeoutSeconds) : new Result(-1, "", "ffprobe not found", false);
}
