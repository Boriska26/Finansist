using Finansist.Core.Finance;
using Finansist.Core.Budgeting;
using Finansist.Core.Importing;

namespace Finansist.Core.Storage;

public sealed partial class LocalStore(ISqliteConnectionFactory factory) : ILocalStore, IFinanceStore, IBudgetStore, IBankImportStore
{
    public const int SchemaVersion = 5;
    private readonly SemaphoreSlim gate = new(1, 1);

    public Task<ThemePreference> ReadThemeAsync() => RunAsync(db =>
    {
        var value = db.Scalar("SELECT value FROM app_settings WHERE key = ?", "theme");
        return value switch
        {
            null or "system" => ThemePreference.System,
            "light" => ThemePreference.Light,
            "dark" => ThemePreference.Dark,
            _ => throw new StorageException("Не удалось прочитать настройки. Данные сохранены без изменений.")
        };
    });

    public Task SaveThemeAsync(ThemePreference theme)
    {
        var value = theme switch
        {
            ThemePreference.System => "system",
            ThemePreference.Light => "light",
            ThemePreference.Dark => "dark",
            _ => throw new ArgumentOutOfRangeException(nameof(theme))
        };
        return RunAsync(db =>
        {
            db.Execute("INSERT OR REPLACE INTO app_settings (key, value) VALUES (?, ?)", "theme", value);
            return true;
        });
    }

    private async Task<T> RunAsync<T>(Func<ISqliteConnection, T> action)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Android's SQLite API is synchronous. Keep opening, migration and I/O off the UI thread.
            return await Task.Run(() =>
            {
                using var db = factory.Open();
                db.Execute("PRAGMA busy_timeout = 5000");
                db.Execute("PRAGMA foreign_keys = ON");
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    Migrate(db);
                    var result = action(db);
                    db.Execute("COMMIT");
                    return result;
                }
                catch
                {
                    try { db.Execute("ROLLBACK"); }
                    catch (Exception) { /* Preserve the original failure; never log database contents. */ }
                    throw;
                }
            }).ConfigureAwait(false);
        }
        catch (StorageException) { throw; }
        catch (FinanceValidationException) { throw; }
        catch (OverflowException)
        {
            throw new FinanceValidationException("Суммы слишком велики для расчёта. Уменьшите значения.");
        }
        catch (Exception exception)
        {
            throw new StorageException("Не удалось открыть или сохранить данные. Проверьте свободное место и повторите попытку.", exception);
        }
        finally { gate.Release(); }
    }

    private static void Migrate(ISqliteConnection db)
    {
        var version = int.Parse(db.Scalar("PRAGMA user_version") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
            throw new StorageException("База создана более новой версией приложения. Обновите приложение. Данные не изменены.");
        if (version == 0)
        {
            db.Execute("CREATE TABLE app_settings (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL)");
            db.Execute("PRAGMA user_version = 1");
            version = 1;
        }
        if (version == 1)
        {
            db.Execute("CREATE TABLE accounts (id TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL CHECK(length(trim(name)) BETWEEN 1 AND 60), opening_minor INTEGER NOT NULL CHECK(typeof(opening_minor) = 'integer' AND opening_minor BETWEEN -100000000000000 AND 100000000000000))");
            db.Execute("CREATE TABLE operations (id TEXT NOT NULL PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('expense','income','transfer')), account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE RESTRICT, destination_id TEXT REFERENCES accounts(id) ON DELETE RESTRICT, amount_minor INTEGER NOT NULL CHECK(typeof(amount_minor) = 'integer' AND amount_minor > 0 AND amount_minor <= 100000000000000), occurred_on TEXT NOT NULL, category TEXT NOT NULL, note TEXT NOT NULL, CHECK((kind = 'transfer' AND destination_id IS NOT NULL AND destination_id <> account_id) OR (kind <> 'transfer' AND destination_id IS NULL)))");
            db.Execute("CREATE INDEX ix_operations_date ON operations(occurred_on DESC, id DESC)");
            db.Execute("CREATE INDEX ix_operations_account ON operations(account_id)");
            db.Execute("CREATE INDEX ix_operations_destination ON operations(destination_id)");
            db.Execute("PRAGMA user_version = 2");
            version = 2;
        }
        if (version == 2)
        {
            // Keep schema 3 compatibility for devices that installed stage 3.
            // Existing planning_state is deliberately left untouched and unused.
            db.Execute("PRAGMA user_version = 3");
            version = 3;
        }
        if (version == 3)
        {
            db.Execute("CREATE TABLE salary_budget (id INTEGER NOT NULL PRIMARY KEY CHECK(id = 1), revision INTEGER NOT NULL, payload TEXT NOT NULL)");
            db.Execute("PRAGMA user_version = 4");
            version = 4;
        }
        if (version == 4)
        {
            db.Execute("CREATE TABLE notification_imports (id TEXT NOT NULL PRIMARY KEY, event_key TEXT NOT NULL UNIQUE, fingerprint TEXT NOT NULL, payload TEXT NOT NULL, status TEXT NOT NULL CHECK(status IN ('pending','confirmed','dismissed')), operation_id TEXT, received_at TEXT NOT NULL)");
            db.Execute("CREATE INDEX ix_notification_imports_status ON notification_imports(status, received_at)");
            db.Execute("CREATE INDEX ix_notification_imports_fingerprint ON notification_imports(fingerprint)");
            db.Execute("PRAGMA user_version = 5");
        }
    }
}
