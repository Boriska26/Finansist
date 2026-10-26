using System.Text.Json;
using Finansist.Core.Finance;

namespace Finansist.Core.Storage;

public sealed partial class LocalStore
{
    public Task<AccountLayout> ReadAccountLayoutAsync() => RunAsync(ReadAccountLayout);

    public Task SaveAccountLayoutAsync(AccountLayout layout) => RunAsync(db =>
    {
        var current = db.Query("SELECT id FROM accounts").Select(row => row[0]!).ToHashSet();
        if (layout.Order.Count != current.Count || layout.Order.Distinct().Count() != layout.Order.Count || !current.SetEquals(layout.Order))
            throw new FinanceValidationException("Список счетов изменился. Обновите экран и повторите перемещение.");
        db.Execute("INSERT OR REPLACE INTO app_settings (key, value) VALUES (?, ?)", "account_layout", JsonSerializer.Serialize(layout));
        return true;
    });

    private static AccountLayout ReadAccountLayout(ISqliteConnection db)
    {
        var current = db.Query("SELECT id FROM accounts ORDER BY name, id").Select(row => row[0]!).ToList();
        var json = db.Scalar("SELECT value FROM app_settings WHERE key = ?", "account_layout");
        var saved = json is null ? new AccountLayout([], false) : JsonSerializer.Deserialize<AccountLayout>(json)
            ?? throw new StorageException("Не удалось прочитать порядок счетов. Данные не изменены.");
        var ids = current.ToHashSet();
        // Removed accounts disappear; new accounts are appended without disturbing the chosen first account.
        return new([..saved.Order.Where(ids.Contains).Distinct(), ..current.Where(id => !saved.Order.Contains(id))], saved.Expanded);
    }
}
