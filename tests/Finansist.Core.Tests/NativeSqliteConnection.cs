using System.Runtime.InteropServices;
using Finansist.Core.Storage;

namespace Finansist.Core.Tests;

// Test-only Linux adapter, exercising the production SQL against real SQLite files.
// Android uses the supported Android.Database.Sqlite API, not native private libraries.
internal sealed class NativeSqliteConnection : ISqliteConnection
{
    private nint database;

    public NativeSqliteConnection(string path)
    {
        var result = Native.sqlite3_open(path, out database);
        if (result != 0)
        {
            Dispose();
            throw new InvalidOperationException($"SQLite open error {result}");
        }
    }

    public void Execute(string sql, params string[] arguments) => _ = Scalar(sql, arguments);

    public string? Scalar(string sql, params string[] arguments) => Query(sql, arguments).FirstOrDefault()?[0];

    public IReadOnlyList<string?[]> Query(string sql, params string[] arguments)
    {
        Check(Native.sqlite3_prepare_v2(database, sql, -1, out var statement, 0));
        try
        {
            for (var i = 0; i < arguments.Length; i++)
                Check(Native.sqlite3_bind_text(statement, i + 1, arguments[i], -1, new nint(-1)));
            var rows = new List<string?[]>();
            while (true)
            {
                var result = Native.sqlite3_step(statement);
                if (result == 101) return rows;
                if (result != 100) Check(result);
                var row = new string?[Native.sqlite3_column_count(statement)];
                for (var i = 0; i < row.Length; i++) row[i] = Marshal.PtrToStringUTF8(Native.sqlite3_column_text(statement, i));
                rows.Add(row);
            }
        }
        finally { Check(Native.sqlite3_finalize(statement)); }
    }

    private static void Check(int result)
    {
        if (result != 0) throw new InvalidOperationException($"SQLite error {result}");
    }

    public void Dispose()
    {
        if (database == 0) return;
        Native.sqlite3_close(database);
        database = 0;
    }

    private static class Native
    {
        private const string Library = "libsqlite3.so.0";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out nint database);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close(nint database);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out nint statement, nint tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_text(nint statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, nint destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(nint statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint sqlite3_column_text(nint statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(nint statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_count(nint statement);
    }
}

internal sealed class TestConnectionFactory(string path) : ISqliteConnectionFactory
{
    public ISqliteConnection Open() => new NativeSqliteConnection(path);
}
