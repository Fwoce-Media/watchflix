using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WatchFlix.Core.Data;

/// <summary>
/// A thin binding to the SQLite C library.
///
/// Windows 10 and 11 ship SQLite themselves as winsqlite3.dll, so no package is
/// needed to open the library database. A bundled e_sqlite3.dll beside the exe is
/// preferred when present (it is newer), then the system copy. On Linux, which is
/// only used for testing, the distribution's libsqlite3 is used.
/// </summary>
internal static unsafe class Native
{
    const string Lib = "sqlite3";

    public const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
    public const int SQLITE_INTEGER = 1, SQLITE_FLOAT = 2, SQLITE_TEXT = 3,
                     SQLITE_BLOB = 4, SQLITE_NULL = 5;
    public const int OPEN_READWRITE = 0x2, OPEN_CREATE = 0x4, OPEN_FULLMUTEX = 0x10000;
    public static readonly IntPtr TRANSIENT = new(-1);

    static Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
    }

    public static void EnsureLoaded() { /* runs the static constructor */ }

    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        var candidates = new List<string>();
        var here = AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(here, "e_sqlite3.dll"));
            candidates.Add(Path.Combine(here, "runtimes", "win-x64", "native", "e_sqlite3.dll"));
            candidates.Add("e_sqlite3");
            candidates.Add("winsqlite3");
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("libsqlite3.dylib");
            candidates.Add("/usr/lib/libsqlite3.dylib");
        }
        else
        {
            candidates.Add("libsqlite3.so.0");
            candidates.Add("libsqlite3.so");
        }
        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
            if (NativeLibrary.TryLoad(candidate, assembly, path, out handle)) return handle;
        }
        return IntPtr.Zero;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_libversion_number();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr cb, IntPtr arg, out IntPtr err);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sqlite3_free(IntPtr p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_prepare_v2(IntPtr db, byte* sql, int nBytes, out IntPtr stmt, out byte* tail);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_int64(IntPtr stmt, int i, long v);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_double(IntPtr stmt, int i, double v);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_text(IntPtr stmt, int i, byte[] v, int n, IntPtr destructor);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_blob(IntPtr stmt, int i, byte[] v, int n, IntPtr destructor);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_null(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_bind_parameter_count(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_column_name(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_column_type(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long sqlite3_column_int64(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern double sqlite3_column_double(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_column_bytes(IntPtr stmt, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sqlite3_changes(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long sqlite3_last_insert_rowid(IntPtr db);

    public static string Utf8(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";

    public static byte[] Z(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Array.Resize(ref bytes, bytes.Length + 1);
        return bytes;
    }
}

public sealed class SqliteException : Exception
{
    public SqliteException(string message) : base(message) { }
}

/// <summary>One result row. Column values are long, double, string, byte[] or null.</summary>
public sealed class Row : Dictionary<string, object?>
{
    public Row() : base(StringComparer.Ordinal) { }
    public Row(IDictionary<string, object?> source) : base(source, StringComparer.Ordinal) { }

    public object? Get(string key) => TryGetValue(key, out var v) ? v : null;

    public long? Long(string key) => Get(key) switch
    {
        long l => l,
        double d => (long)d,
        int i => i,
        string s when long.TryParse(s, out var p) => p,
        _ => null,
    };

    public int? Int(string key) => Long(key) is long l ? (int)l : null;

    public double? Double(string key) => Get(key) switch
    {
        double d => d,
        long l => l,
        int i => i,
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    public string Str(string key) => Get(key) switch
    {
        null => "",
        string s => s,
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        var o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>Python truthiness of a column: 0, 0.0, "" and null are false.</summary>
    public bool Truthy(string key) => Get(key) switch
    {
        null => false,
        long l => l != 0,
        double d => d != 0,
        string s => s.Length > 0,
        bool b => b,
        _ => true,
    };
}

/// <summary>
/// One connection shared by the whole app, each statement run under a lock.
///
/// Every statement commits on its own, as the Python build's did, so the
/// database file is always consistent if the app is killed mid-scan. WAL mode
/// lets another reader (the Python build, a backup tool) look at it meanwhile.
/// </summary>
public sealed class SqliteDb : IDisposable
{
    readonly IntPtr _db;
    readonly object _gate = new();
    public string FilePath { get; }

    public SqliteDb(string path)
    {
        Native.EnsureLoaded();
        FilePath = path;
        var rc = Native.sqlite3_open_v2(Native.Z(path), out _db,
            Native.OPEN_READWRITE | Native.OPEN_CREATE | Native.OPEN_FULLMUTEX, IntPtr.Zero);
        if (rc != Native.SQLITE_OK)
            throw new SqliteException($"Could not open {path}: {Native.Utf8(Native.sqlite3_errmsg(_db))}");
        Native.sqlite3_busy_timeout(_db, 30000);
        Exec("PRAGMA foreign_keys=ON;");
    }

    public static int LibraryVersion
    {
        get { Native.EnsureLoaded(); return Native.sqlite3_libversion_number(); }
    }

    public void Exec(string script)
    {
        lock (_gate)
        {
            var rc = Native.sqlite3_exec(_db, Native.Z(script), IntPtr.Zero, IntPtr.Zero, out var err);
            if (rc != Native.SQLITE_OK)
            {
                var message = Native.Utf8(err);
                if (err != IntPtr.Zero) Native.sqlite3_free(err);
                throw new SqliteException(message);
            }
        }
    }

    public List<Row> Query(string sql, params object?[] args)
    {
        var rows = new List<Row>();
        Run(sql, args, rows, out _, out _);
        return rows;
    }

    public Row? QueryOne(string sql, params object?[] args)
    {
        var rows = new List<Row>();
        Run(sql, args, rows, out _, out _, limit: 1);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Runs a write. Returns the number of rows changed.</summary>
    public int Execute(string sql, params object?[] args)
    {
        Run(sql, args, null, out var changes, out _);
        return changes;
    }

    /// <summary>Runs an INSERT and returns the new row's id.</summary>
    public long Insert(string sql, params object?[] args)
    {
        Run(sql, args, null, out _, out var id);
        return id;
    }

    unsafe void Run(string sql, object?[] args, List<Row>? rows, out int changes, out long lastId,
                    int limit = int.MaxValue)
    {
        var bytes = Encoding.UTF8.GetBytes(sql);
        lock (_gate)
        {
            IntPtr stmt;
            fixed (byte* p = bytes)
            {
                var rc = Native.sqlite3_prepare_v2(_db, p, bytes.Length, out stmt, out _);
                if (rc != Native.SQLITE_OK)
                    throw new SqliteException($"{Native.Utf8(Native.sqlite3_errmsg(_db))}\n{sql}");
            }
            try
            {
                Bind(stmt, args);
                var columns = rows != null ? Native.sqlite3_column_count(stmt) : 0;
                string[] names = new string[columns];
                for (var i = 0; i < columns; i++)
                    names[i] = Native.Utf8(Native.sqlite3_column_name(stmt, i));

                while (true)
                {
                    var rc = Native.sqlite3_step(stmt);
                    if (rc == Native.SQLITE_DONE) break;
                    if (rc != Native.SQLITE_ROW)
                        throw new SqliteException($"{Native.Utf8(Native.sqlite3_errmsg(_db))}\n{sql}");
                    if (rows == null) continue;
                    var row = new Row();
                    for (var i = 0; i < columns; i++)
                        row[names[i]] = ReadColumn(stmt, i);
                    rows.Add(row);
                    if (rows.Count >= limit) break;
                }
                changes = Native.sqlite3_changes(_db);
                lastId = Native.sqlite3_last_insert_rowid(_db);
            }
            finally
            {
                Native.sqlite3_finalize(stmt);
            }
        }
    }

    static object? ReadColumn(IntPtr stmt, int i)
    {
        switch (Native.sqlite3_column_type(stmt, i))
        {
            case Native.SQLITE_INTEGER: return Native.sqlite3_column_int64(stmt, i);
            case Native.SQLITE_FLOAT: return Native.sqlite3_column_double(stmt, i);
            case Native.SQLITE_TEXT:
            {
                var ptr = Native.sqlite3_column_text(stmt, i);
                var len = Native.sqlite3_column_bytes(stmt, i);
                return ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(ptr, len);
            }
            case Native.SQLITE_BLOB:
            {
                var len = Native.sqlite3_column_bytes(stmt, i);
                var buf = new byte[len];
                if (len > 0) Marshal.Copy(Native.sqlite3_column_blob(stmt, i), buf, 0, len);
                return buf;
            }
            default: return null;
        }
    }

    static void Bind(IntPtr stmt, object?[] args)
    {
        var expected = Native.sqlite3_bind_parameter_count(stmt);
        if (expected != args.Length)
            throw new SqliteException($"Statement wants {expected} values, got {args.Length}.");
        for (var i = 0; i < args.Length; i++)
        {
            var n = i + 1;
            switch (args[i])
            {
                case null: Native.sqlite3_bind_null(stmt, n); break;
                case bool b: Native.sqlite3_bind_int64(stmt, n, b ? 1 : 0); break;
                case int v: Native.sqlite3_bind_int64(stmt, n, v); break;
                case long v: Native.sqlite3_bind_int64(stmt, n, v); break;
                case double v: Native.sqlite3_bind_double(stmt, n, v); break;
                case float v: Native.sqlite3_bind_double(stmt, n, v); break;
                case decimal v: Native.sqlite3_bind_double(stmt, n, (double)v); break;
                case byte[] v: Native.sqlite3_bind_blob(stmt, n, v, v.Length, Native.TRANSIENT); break;
                case string s:
                {
                    var bytes = Encoding.UTF8.GetBytes(s);
                    Native.sqlite3_bind_text(stmt, n, bytes, bytes.Length, Native.TRANSIENT);
                    break;
                }
                case var other:
                {
                    var text = Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    var bytes = Encoding.UTF8.GetBytes(text);
                    Native.sqlite3_bind_text(stmt, n, bytes, bytes.Length, Native.TRANSIENT);
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) Native.sqlite3_close_v2(_db);
    }
}
