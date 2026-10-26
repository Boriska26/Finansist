using System.Text.Json;
using Finansist.Core.Budgeting;
using Finansist.Core.Finance;

namespace Finansist.Core.Storage;

public sealed partial class LocalStore
{
    private static BudgetDocument ReadBudgetDocument(ISqliteConnection db)
    {
        var json = db.Scalar("SELECT payload FROM salary_budget WHERE id = 1");
        return json is null ? new() : JsonSerializer.Deserialize<BudgetDocument>(json)
            ?? throw new StorageException("Не удалось прочитать бюджет. Данные не изменены.");
    }
    private static BudgetState ReadBudgetState(ISqliteConnection db) => new(
        long.Parse(db.Scalar("SELECT revision FROM salary_budget WHERE id = 1") ?? "0", Invariant), ReadBudgetDocument(db), ReadSnapshot(db));
    private static void WriteBudget(ISqliteConnection db, BudgetDocument document)
    {
        var payload = JsonSerializer.Serialize(document);
        db.Execute("INSERT INTO salary_budget (id, revision, payload) VALUES (1, 1, ?) ON CONFLICT(id) DO UPDATE SET revision = revision + 1, payload = excluded.payload", payload);
    }
    public Task<BudgetState> ReadBudgetAsync() => RunAsync(ReadBudgetState);
    public Task<BudgetState> AllocateAsync(string incomeId, string cushionAccountId, DateOnly until,
        IReadOnlyList<BudgetBill> bills, string fingerprint) => RunAsync(db =>
    {
        var state = ReadBudgetState(db);
        var proposal = BudgetCalculator.Propose(state, incomeId, cushionAccountId, until, bills);
        BudgetCalculator.Require(proposal.Fingerprint == fingerprint, "Данные изменились. Обновите экран и проверьте три суммы ещё раз.");
        var income = state.Finance.Operations.Single(o => o.Id == incomeId);
        var allocation = new IncomeAllocation(incomeId, income.AccountId, income.AmountMinor, income.Date, until,
            proposal.AvailableMinor, proposal.NecessaryMinor, proposal.SavingsMinor, proposal.JoyMinor, proposal.Funding);
        WriteBudget(db, state.Document with
        {
            CushionAccountId = cushionAccountId, Bills = bills.ToList(), Allocations = [..state.Document.Allocations, allocation],
            BaselineOperationIds = state.Document.Allocations.Count == 0 ? state.Finance.Operations.Select(o => o.Id).ToList() : state.Document.BaselineOperationIds
        });
        return ReadBudgetState(db);
    });

    public Task<BudgetState> SaveBillsAsync(IReadOnlyList<BudgetBill> bills, long revision) => RunAsync(db =>
    {
        var state = ReadBudgetState(db);
        BudgetCalculator.Require(state.Revision == revision, "Платежи изменились. Откройте экран заново.");
        BudgetCalculator.ValidateBills(bills);
        BudgetCalculator.Require(state.Document.Bills.All(old => bills.Any(b => b.Id == old.Id)), "Для удаления существующего платежа используйте кнопку «Удалить платёж».");
        WriteBudget(db, state.Document with { Bills = bills.ToList() });
        return ReadBudgetState(db);
    });

    public Task<BudgetState> CancelAllocationAsync(string incomeId) => RunAsync(db =>
    {
        var state = ReadBudgetState(db);
        WriteBudget(db, state.Document with { Allocations = state.Document.Allocations.Where(a => a.IncomeId != incomeId).ToList() });
        return ReadBudgetState(db);
    });

    public Task<BudgetState> DeleteBillAsync(string billId, long revision) => RunAsync(db =>
    {
        var state = ReadBudgetState(db);
        BudgetCalculator.Require(state.Revision == revision, "Платежи изменились. Откройте экран заново перед удалением.");
        var bill = state.Document.Bills.SingleOrDefault(b => b.Id == billId);
        BudgetCalculator.Require(bill is not null, "Платёж уже удалён или не найден. Обновите экран.");
        var series = bill!.SeriesId ?? bill.Id;
        // Keep IDs and payment links for history; stop all occurrences of this obligation.
        var bills = state.Document.Bills.Select(b => b.Id == billId || b.SeriesId == series
            ? b with { Archived = true, Settled = true, Monthly = false } : b).ToList();
        WriteBudget(db, state.Document with { Bills = bills });
        return ReadBudgetState(db);
    });

    public Task<BudgetState> SaveBudgetOperationAsync(FinanceOperation operation, ExpensePurpose? purpose) => RunAsync(db => SaveBudgetOperationCore(db, operation, purpose));

    private static BudgetState SaveBudgetOperationCore(ISqliteConnection db, FinanceOperation operation, ExpensePurpose? purpose)
    {
        var document = ReadBudgetDocument(db);
        var purposes = document.Purposes.Where(p => p.OperationId != operation.Id).ToList();
        if (purpose is not null)
        {
            BudgetCalculator.Require(purpose.OperationId == operation.Id && operation.Kind == OperationKind.Expense,
                "Назначение бюджета указывается только для расхода.");
            BudgetCalculator.Require(purpose.BillId is null || purpose.Necessary && document.Bills.Any(b => b.Id == purpose.BillId), "Выберите существующий обязательный платёж.");
            BudgetCalculator.Require(purpose.BillId is null || !document.Bills.Single(b => b.Id == purpose.BillId).Archived ||
                document.Purposes.Any(p => p.OperationId == operation.Id && p.BillId == purpose.BillId),
                "Этот платёж удалён. Для нового расхода выберите другое назначение.");
            BudgetCalculator.Require(operation.AccountId != document.CushionAccountId || purpose.BillId is null,
                "Для оплаты из подушки сначала запишите перевод на основную карту.");
            purposes.Add(purpose);
        }
        WriteBudget(db, document with { Purposes = purposes });
        SaveOperationCore(db, operation);
        return ReadBudgetState(db);
    }

    private static void ValidateBudgetOperation(ISqliteConnection db, FinanceOperation operation)
    {
        var document = ReadBudgetDocument(db);
        var allocation = document.Allocations.SingleOrDefault(a => a.IncomeId == operation.Id);
        if (allocation is not null)
            BudgetCalculator.Require(operation.Kind == OperationKind.Income && operation.AmountMinor == allocation.OriginalMinor &&
                operation.AccountId == allocation.AccountId && operation.Date == allocation.IncomeDate,
                "Этот доход уже распределён. Сначала отмените распределение на вкладке «Бюджет».");
        var purpose = document.Purposes.SingleOrDefault(p => p.OperationId == operation.Id);
        if (purpose is not null)
            BudgetCalculator.Require(operation.Kind == OperationKind.Expense && (purpose.BillId is null || operation.AccountId != document.CushionAccountId),
                "Измените назначение расхода в бюджете перед сменой типа или счёта.");
    }
}
