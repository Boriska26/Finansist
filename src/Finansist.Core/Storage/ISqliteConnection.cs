namespace Finansist.Core.Storage;

// Adapters own platform details; all SQL, migrations and transactions live in LocalStore.
public interface ISqliteConnection : IDisposable
{
    void Execute(string sql, params string[] arguments);
    IReadOnlyList<string?[]> Query(string sql, params string[] arguments);
    string? Scalar(string sql, params string[] arguments);
}

public interface ISqliteConnectionFactory
{
    ISqliteConnection Open();
}
