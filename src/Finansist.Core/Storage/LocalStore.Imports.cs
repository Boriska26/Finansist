using System.Text.Json;
using Finansist.Core.Budgeting;
using Finansist.Core.Finance;
using Finansist.Core.Importing;

namespace Finansist.Core.Storage;

public sealed partial class LocalStore
{
    private static ImportSettings ReadImportSettings(ISqliteConnection db)
    {
        var json = db.Scalar("SELECT value FROM app_settings WHERE key = ?", "notification_import");
        return json is null ? new() : JsonSerializer.Deserialize<ImportSettings>(json) ?? throw new StorageException("Не удалось прочитать настройки уведомлений.");
    }
    private static ImportRow ImportFromRow(string?[] row) => new(JsonSerializer.Deserialize<ImportCandidate>(row[0]!)
        ?? throw new StorageException("Не удалось прочитать найденную операцию."), row[1]!, row[2]);
    private static ImportRow GetImport(ISqliteConnection db, string id) => db.Query("SELECT payload, status, operation_id FROM notification_imports WHERE id = ?", id)
        .Select(ImportFromRow).SingleOrDefault() ?? throw new FinanceValidationException("Найденная операция больше не доступна. Обновите очередь.");
    public Task<ImportSettings> ReadImportSettingsAsync() => RunAsync(ReadImportSettings);
    public Task<ImportSnapshot> ReadImportsAsync() => RunAsync(db => new ImportSnapshot(ReadImportSettings(db),
        db.Query("SELECT payload, status, operation_id FROM notification_imports WHERE status = 'pending' ORDER BY received_at, id").Select(ImportFromRow).ToArray(),
        int.Parse(db.Scalar("SELECT COUNT(*) FROM notification_imports WHERE status <> 'pending'") ?? "0", Invariant)));
    public Task<ImportRow> ReadImportAsync(string id) => RunAsync(db => GetImport(db, id));
    public Task ConfigureImportAsync(NotificationSource source, bool enabled) => RunAsync(db =>
    {
        if (string.IsNullOrWhiteSpace(source.PackageId) || source.PackageId.Length > 200 || string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 200)
            throw new FinanceValidationException("Выберите приложение ВБРР Онлайн из списка.");
        var current = ReadImportSettings(db);
        var since = enabled && (!current.Enabled || current.PackageId != source.PackageId) ? DateTimeOffset.UtcNow : current.EnabledSince;
        db.Execute("INSERT OR REPLACE INTO app_settings (key, value) VALUES (?, ?)", "notification_import",
            JsonSerializer.Serialize(new ImportSettings(source.PackageId, source.Name, enabled, since)));
        return true;
    });
    public Task<bool> ReceivePushAsync(BankPush push) => RunAsync(db =>
    {
        var settings = ReadImportSettings(db);
        if (!settings.Enabled || settings.PackageId != push.PackageId || settings.EnabledSince is null || push.PostedAt < settings.EnabledSince ||
            push.Title.Length + push.Text.Length > 7999 || push.Key.Length > 500) return false;
        var parsed = VbrrPushParser.Parse(push);
        if (parsed is null) return false;
        var eventKey = VbrrPushParser.EventKey(push);
        if (db.Scalar("SELECT id FROM notification_imports WHERE event_key = ?", eventKey) is not null) return false;
        var fingerprint = VbrrPushParser.Fingerprint(push);
        var repeat = db.Scalar("SELECT id FROM notification_imports WHERE fingerprint = ? LIMIT 1", fingerprint) is not null;
        var snapshot = ReadSnapshot(db);
        var candidate = new ImportCandidate(Guid.NewGuid().ToString(), eventKey, fingerprint, push, parsed, snapshot.Accounts.FirstOrDefault()?.Id, repeat);
        db.Execute("INSERT INTO notification_imports (id, event_key, fingerprint, payload, status, received_at) VALUES (?, ?, ?, ?, 'pending', ?)",
            candidate.Id, eventKey, fingerprint, JsonSerializer.Serialize(candidate), DateTimeOffset.UtcNow.ToString("O", Invariant));
        return true;
    });
    public Task DismissImportAsync(string id) => RunAsync(db =>
    {
        _ = GetImport(db, id);
        db.Execute("UPDATE notification_imports SET status = 'dismissed' WHERE id = ? AND status = 'pending'", id);
        return true;
    });
    public Task<string> ConfirmImportAsync(string id, FinanceOperation operation, ExpensePurpose? purpose, string? existingOperationId = null) => RunAsync(db =>
    {
        var item = GetImport(db, id);
        if (item.Status != "pending") throw new FinanceValidationException("Это уведомление уже обработано. Обновите очередь.");
        if (operation.Id != id) throw new FinanceValidationException("Не удалось определить подтверждаемую операцию.");
        string operationId;
        if (existingOperationId is not null)
        {
            var existing = ReadSnapshot(db).Operations.SingleOrDefault(o => o.Id == existingOperationId);
            if (existing is null || !ImportMatcher.Matches(existing, operation))
                throw new FinanceValidationException("Выбранная запись уже изменилась или не совпадает. Проверьте сумму, счёт и дату.");
            operationId = existing.Id;
        }
        else
        {
            if (db.Scalar("SELECT id FROM operations WHERE id = ?", operation.Id) is not null)
                throw new FinanceValidationException("Запись уже существует. Свяжите уведомление с операцией из журнала.");
            SaveBudgetOperationCore(db, operation, purpose);
            operationId = operation.Id;
        }
        db.Execute("UPDATE notification_imports SET status = 'confirmed', operation_id = ? WHERE id = ? AND status = 'pending'", operationId, id);
        return operationId;
    });
}
