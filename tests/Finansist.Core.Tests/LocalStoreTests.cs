using Finansist.Core.Storage;

namespace Finansist.Core.Tests;

public sealed class LocalStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "finansist-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(directory, "synthetic.db");
    private LocalStore NewStore() => new(new TestConnectionFactory(DatabasePath));

    public LocalStoreTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task NewDatabase_MigratesWithoutSeedingUserData()
    {
        Assert.Equal(ThemePreference.System, await NewStore().ReadThemeAsync());
        using var db = new NativeSqliteConnection(DatabasePath);
        Assert.Equal("4", db.Scalar("PRAGMA user_version"));
        Assert.Equal("0", db.Scalar("SELECT COUNT(*) FROM app_settings"));
        Assert.Equal("4", db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"));
        Assert.Equal("ok", db.Scalar("PRAGMA integrity_check"));
    }

    [Theory]
    [InlineData(ThemePreference.System)]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    public async Task Theme_SurvivesClosingAndReopeningDatabase(ThemePreference theme)
    {
        await NewStore().SaveThemeAsync(theme);
        Assert.Equal(theme, await NewStore().ReadThemeAsync());
    }

    [Fact]
    public async Task RepeatedWrites_UpdateOneRow_AndInitializationIsIdempotent()
    {
        for (var i = 0; i < 5; i++) await NewStore().SaveThemeAsync(ThemePreference.Dark);
        using var db = new NativeSqliteConnection(DatabasePath);
        Assert.Equal("1", db.Scalar("SELECT COUNT(*) FROM app_settings WHERE key='theme'"));
    }

    [Fact]
    public async Task FutureSchema_IsRejectedWithoutChangingDataOrVersion()
    {
        await NewStore().SaveThemeAsync(ThemePreference.Dark);
        using (var db = new NativeSqliteConnection(DatabasePath)) db.Execute("PRAGMA user_version = 99");
        await Assert.ThrowsAsync<StorageException>(() => NewStore().ReadThemeAsync());
        await Assert.ThrowsAsync<StorageException>(() => NewStore().SaveThemeAsync(ThemePreference.Light));
        using var check = new NativeSqliteConnection(DatabasePath);
        Assert.Equal("99", check.Scalar("PRAGMA user_version"));
        Assert.Equal("dark", check.Scalar("SELECT value FROM app_settings WHERE key='theme'"));
    }

    [Fact]
    public async Task MigrationFailure_RollsBackTableAndVersion_AndCanRetry()
    {
        var store = new LocalStore(new FailingMigrationFactory(DatabasePath));
        await Assert.ThrowsAsync<StorageException>(() => store.ReadThemeAsync());
        using (var db = new NativeSqliteConnection(DatabasePath))
        {
            Assert.Equal("0", db.Scalar("PRAGMA user_version"));
            Assert.Equal("0", db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='app_settings'"));
        }
        Assert.Equal(ThemePreference.System, await NewStore().ReadThemeAsync());
    }

    [Fact]
    public async Task ConcurrentInitializationAndWrites_DoNotDuplicateSettings()
    {
        var store = NewStore();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.SaveThemeAsync(ThemePreference.Dark)));
        Assert.Equal(ThemePreference.Dark, await store.ReadThemeAsync());
        using var db = new NativeSqliteConnection(DatabasePath);
        Assert.Equal("1", db.Scalar("SELECT COUNT(*) FROM app_settings"));
        Assert.Equal("ok", db.Scalar("PRAGMA integrity_check"));
    }

    [Fact]
    public async Task CorruptFile_IsNotDeletedOrRecreated()
    {
        byte[] contents = "synthetic invalid database"u8.ToArray();
        await File.WriteAllBytesAsync(DatabasePath, contents);
        await Assert.ThrowsAsync<StorageException>(() => NewStore().ReadThemeAsync());
        Assert.Equal(contents, await File.ReadAllBytesAsync(DatabasePath));
    }

    [Fact]
    public async Task InvalidStoredTheme_IsReportedWithoutOverwritingIt()
    {
        await NewStore().ReadThemeAsync();
        using (var db = new NativeSqliteConnection(DatabasePath))
            db.Execute("INSERT INTO app_settings (key,value) VALUES (?,?)", "theme", "unknown");
        await Assert.ThrowsAsync<StorageException>(() => NewStore().ReadThemeAsync());
        using var check = new NativeSqliteConnection(DatabasePath);
        Assert.Equal("unknown", check.Scalar("SELECT value FROM app_settings WHERE key='theme'"));
    }

    [Fact]
    public async Task InvalidEnum_DoesNotOverwriteSavedTheme()
    {
        var store = NewStore();
        await store.SaveThemeAsync(ThemePreference.Dark);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveThemeAsync((ThemePreference)100));
        Assert.Equal(ThemePreference.Dark, await store.ReadThemeAsync());
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class FailingMigrationFactory(string path) : ISqliteConnectionFactory
    {
        public ISqliteConnection Open() => new FailingMigrationConnection(new NativeSqliteConnection(path));
    }
    private sealed class FailingMigrationConnection(ISqliteConnection inner) : ISqliteConnection
    {
        public void Execute(string sql, params string[] arguments)
        {
            if (sql == "PRAGMA user_version = 1") throw new IOException("Synthetic write failure");
            inner.Execute(sql, arguments);
        }
        public string? Scalar(string sql, params string[] arguments) => inner.Scalar(sql, arguments);
        public IReadOnlyList<string?[]> Query(string sql, params string[] arguments) => inner.Query(sql, arguments);
        public void Dispose() => inner.Dispose();
    }
}
