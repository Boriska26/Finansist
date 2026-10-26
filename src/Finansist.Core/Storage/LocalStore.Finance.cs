using System.Globalization;
using Finansist.Core.Finance;
using Finansist.Core.Budgeting;

namespace Finansist.Core.Storage;

public sealed partial class LocalStore
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public Task<FinanceSnapshot> ReadFinanceAsync() => RunAsync(ReadSnapshot);

    public Task<FinanceSnapshot> SaveAccountAsync(Account account) => RunAsync(db =>
    {
        ValidateId(account.Id);
        var name = account.Name.Trim();
        if (name.Length is < 1 or > 60) throw new FinanceValidationException("Название счёта должно содержать от 1 до 60 символов.");
        if (account.OpeningMinor < -Money.MaximumMinor || account.OpeningMinor > Money.MaximumMinor)
            throw new FinanceValidationException("Начальный остаток слишком большой.");
        if (db.Query("SELECT id, name FROM accounts").Any(row => row[0] != account.Id &&
            string.Equals(row[1], name, StringComparison.OrdinalIgnoreCase)))
            throw new FinanceValidationException("Счёт с таким названием уже существует.");
        var amount = account.OpeningMinor.ToString(Invariant);
        if (db.Scalar("SELECT id FROM accounts WHERE id = ?", account.Id) is null)
            db.Execute("INSERT INTO accounts (id, name, opening_minor) VALUES (?, ?, ?)", account.Id, name, amount);
        else
            db.Execute("UPDATE accounts SET name = ?, opening_minor = ? WHERE id = ?", name, amount, account.Id);
        return ReadSnapshot(db);
    });

    public Task<FinanceSnapshot> DeleteAccountAsync(string id) => RunAsync(db =>
    {
        if (db.Scalar("SELECT id FROM operations WHERE account_id = ? OR destination_id = ? LIMIT 1", id, id) is not null)
            throw new FinanceValidationException("На счёте есть операции. Сначала перенесите или удалите их.");
        BudgetCalculator.Require(ReadBudgetDocument(db).CushionAccountId != id, "Этот счёт выбран для подушки и не может быть удалён.");
        db.Execute("DELETE FROM accounts WHERE id = ?", id);
        return ReadSnapshot(db);
    });

    public Task<FinanceSnapshot> SaveOperationAsync(FinanceOperation operation) => RunAsync(db => SaveOperationCore(db, operation));

    private static FinanceSnapshot SaveOperationCore(ISqliteConnection db, FinanceOperation operation)
    {
        ValidateId(operation.Id);
        ValidateBudgetOperation(db, operation);
        if (!Enum.IsDefined(operation.Kind)) throw new FinanceValidationException("Выберите тип операции.");
        if (operation.AmountMinor <= 0 || operation.AmountMinor > Money.MaximumMinor)
            throw new FinanceValidationException("Сумма операции должна быть больше нуля и не больше 1 трлн ₽.");
        if (operation.Date > DateOnly.FromDateTime(DateTime.Today))
            throw new FinanceValidationException("Выберите дату операции не позднее сегодня.");
        if (operation.Category.Trim().Length > 60 || operation.Note.Trim().Length > 300)
            throw new FinanceValidationException("Категория — до 60 символов, примечание — до 300.");
        RequireAccount(db, operation.AccountId);
        if (operation.Kind == OperationKind.Transfer)
        {
            if (string.IsNullOrWhiteSpace(operation.DestinationId) || operation.DestinationId == operation.AccountId)
                throw new FinanceValidationException("Для перевода выберите два разных счёта.");
            RequireAccount(db, operation.DestinationId);
        }
        else if (operation.DestinationId is not null)
            throw new FinanceValidationException("Счёт получателя нужен только для перевода.");
        var kind = operation.Kind switch { OperationKind.Income => "income", OperationKind.Expense => "expense", _ => "transfer" };
        var category = operation.Kind == OperationKind.Transfer ? "Перевод" : operation.Category.Trim();
        if (category.Length == 0) category = "Прочее";
        string[] values = [kind, operation.AccountId, operation.DestinationId ?? "", operation.AmountMinor.ToString(Invariant),
            operation.Date.ToString("yyyy-MM-dd", Invariant), category, operation.Note.Trim(), operation.Id];
        if (db.Scalar("SELECT id FROM operations WHERE id = ?", operation.Id) is null)
            db.Execute("INSERT INTO operations (kind, account_id, destination_id, amount_minor, occurred_on, category, note, id) VALUES (?, ?, NULLIF(?, ''), ?, ?, ?, ?, ?)", values);
        else
            db.Execute("UPDATE operations SET kind = ?, account_id = ?, destination_id = NULLIF(?, ''), amount_minor = ?, occurred_on = ?, category = ?, note = ? WHERE id = ?", values);
        return ReadSnapshot(db);
    }

    public Task<FinanceSnapshot> DeleteOperationAsync(string id) => RunAsync(db =>
    {
        var budget = ReadBudgetDocument(db);
        BudgetCalculator.Require(!budget.Allocations.Any(a => a.IncomeId == id), "Сначала отмените распределение этого дохода на вкладке «Бюджет».");
        if (budget.Purposes.Any(p => p.OperationId == id))
            WriteBudget(db, budget with { Purposes = budget.Purposes.Where(p => p.OperationId != id).ToList() });
        db.Execute("DELETE FROM operations WHERE id = ?", id);
        return ReadSnapshot(db);
    });

    private static void ValidateId(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new FinanceValidationException("Не удалось определить запись. Откройте форму заново.");
    }

    private static void RequireAccount(ISqliteConnection db, string id)
    {
        if (db.Scalar("SELECT id FROM accounts WHERE id = ?", id) is null)
            throw new FinanceValidationException("Счёт не найден. Откройте форму заново и выберите существующий счёт.");
    }

    private static FinanceSnapshot ReadSnapshot(ISqliteConnection db)
    {
        var accounts = db.Query("SELECT id, name, opening_minor FROM accounts ORDER BY name, id")
            .Select(row => new Account(row[0]!, row[1]!, long.Parse(row[2]!, Invariant))).ToArray();
        var order = ReadAccountLayout(db).Order.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        accounts = accounts.OrderBy(a => order[a.Id]).ToArray();
        var operations = db.Query("SELECT id, kind, account_id, destination_id, amount_minor, occurred_on, category, note FROM operations ORDER BY occurred_on DESC, rowid DESC")
            .Select(row => new FinanceOperation(row[0]!, row[1] switch
            {
                "income" => OperationKind.Income, "expense" => OperationKind.Expense, "transfer" => OperationKind.Transfer,
                _ => throw new StorageException("Неизвестный тип операции. Обновите приложение.")
            }, row[2]!, row[3], long.Parse(row[4]!, Invariant), DateOnly.ParseExact(row[5]!, "yyyy-MM-dd", Invariant), row[6]!, row[7]!)).ToArray();
        return Ledger.Calculate(accounts, operations);
    }
}
