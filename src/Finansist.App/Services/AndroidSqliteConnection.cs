using Android.Database.Sqlite;
using Finansist.Core.Storage;

namespace Finansist.App.Services;

public sealed class AndroidSqliteConnectionFactory(string path) : ISqliteConnectionFactory
{
    public ISqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new AndroidSqliteConnection(SQLiteDatabase.OpenOrCreateDatabase(path, null) ?? throw new IOException("SQLite did not open"));
    }
}

internal sealed class AndroidSqliteConnection(SQLiteDatabase database) : ISqliteConnection
{
    public void Execute(string sql, params string[] arguments)
    {
        // PRAGMAs may return a result; RawQuery ensures they are actually evaluated.
        if (sql.StartsWith("PRAGMA", StringComparison.Ordinal))
        {
            using var cursor = database.RawQuery(sql, arguments);
            cursor.MoveToFirst();
        }
        else if (arguments.Length == 0) database.ExecSQL(sql);
        else
        {
            using var statement = database.CompileStatement(sql) ?? throw new IOException("SQLite did not prepare statement");
            for (var i = 0; i < arguments.Length; i++) statement.BindString(i + 1, arguments[i]);
            statement.Execute();
        }
    }

    public string? Scalar(string sql, params string[] arguments)
    {
        using var cursor = database.RawQuery(sql, arguments);
        return cursor.MoveToFirst() && !cursor.IsNull(0) ? cursor.GetString(0) : null;
    }

    public IReadOnlyList<string?[]> Query(string sql, params string[] arguments)
    {
        using var cursor = database.RawQuery(sql, arguments);
        var rows = new List<string?[]>();
        while (cursor.MoveToNext())
        {
            var row = new string?[cursor.ColumnCount];
            for (var i = 0; i < row.Length; i++) row[i] = cursor.IsNull(i) ? null : cursor.GetString(i);
            rows.Add(row);
        }
        return rows;
    }

    public void Dispose()
    {
        database.Close();
        database.Dispose();
    }
}
