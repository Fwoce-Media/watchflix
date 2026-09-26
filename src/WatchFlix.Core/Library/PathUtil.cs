using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WatchFlix.Core.Library;

/// <summary>
/// Paths spelled the way the Python build stored them.
///
/// Python's Path.resolve() asks Windows for a folder's real spelling — true
/// letter case, junctions followed. A root typed as "f:\movies" was therefore
/// stored as "F:\Movies\…". Every file row is keyed by that string, so this
/// build has to spell paths the same way or it would see a whole library of new
/// files and lose the progress kept against the old ones.
/// </summary>
public static class PathUtil
{
    const uint FILE_SHARE_ALL = 0x7;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
                                             uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    public static string Resolve(string path)
    {
        string full;
        try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(ExpandHome(path))); }
        catch { return path; }
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var info = new DirectoryInfo(full);
                if (info.Exists && info.ResolveLinkTarget(true) is { } target) return target.FullName;
            }
            catch { }
            return full.Length > 1 ? full.TrimEnd('/') : full;
        }
        try
        {
            using var handle = CreateFileW(full, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                                           FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid) return full;
            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) return full;
            if (length > buffer.Capacity)
            {
                buffer = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0) return full;
            }
            var result = buffer.ToString();
            // Python strips the \\?\ prefix the same way.
            if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) result = @"\\" + result[8..];
            else if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) result = result[4..];
            return result;
        }
        catch { return full; }
    }

    public static string ExpandHome(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/") || path.StartsWith("~\\"))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    public static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
