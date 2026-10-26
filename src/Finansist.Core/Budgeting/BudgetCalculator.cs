using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Finansist.Core.Finance;

namespace Finansist.Core.Budgeting;

public static class BudgetCalculator
{
    public static void Require(bool condition, string message)
    {
        if (!condition) throw new FinanceValidationException(message);
    }
    public static long Paid(BudgetState state, string billId) => state.Document.Purposes
        .Where(p => p.BillId == billId).Sum(p => state.Finance.Operations.FirstOrDefault(o => o.Id == p.OperationId)?.AmountMinor ?? 0);
    public static long Funded(BudgetDocument doc, string billId) => doc.Allocations.Sum(a => a.Funding.Where(f => f.BillId == billId).Sum(f => f.AmountMinor));
    private static long PaidBeforeBudget(BudgetState state, string billId) => state.Document.Purposes
        .Where(p => p.BillId == billId && state.Document.BaselineOperationIds.Contains(p.OperationId))
        .Sum(p => state.Finance.Operations.FirstOrDefault(o => o.Id == p.OperationId)?.AmountMinor ?? 0);
    private static long Covered(BudgetState state, string billId)
    {
        var covered = PaidBeforeBudget(state, billId);
        foreach (var allocation in state.Document.Allocations)
            foreach (var funding in allocation.Funding.Where(f => f.BillId == billId))
                covered = checked(Math.Max(covered, funding.PaidAtFundingMinor) + funding.AmountMinor);
        return covered;
    }
    public static long Needed(BudgetState state, BudgetBill bill) => bill.Settled || bill.Archived ? 0 :
        Math.Max(0, bill.AmountMinor - Math.Max(Paid(state, bill.Id), Covered(state, bill.Id)));

    public static BudgetTotals Totals(BudgetState state)
    {
        var doc = state.Document;
        var cushion = state.Finance.Accounts.FirstOrDefault(a => a.Id == doc.CushionAccountId)?.BalanceMinor ?? 0;
        var cash = state.Finance.Accounts.Where(a => a.Id != doc.CushionAccountId).Sum(a => a.BalanceMinor);
        if (doc.Allocations.Count == 0) return new(0, 0, 0, cushion, Math.Max(0, cash));
        var ignored = doc.BaselineOperationIds.ToHashSet();
        var active = state.Finance.Operations.Where(o => !ignored.Contains(o.Id)).ToArray();
        long necessarySpent = 0, joySpent = 0;
        foreach (var expense in active.Where(o => o.Kind == OperationKind.Expense && o.AccountId != doc.CushionAccountId))
        {
            if (doc.Purposes.Any(p => p.OperationId == expense.Id && p.Necessary)) necessarySpent = checked(necessarySpent + expense.AmountMinor);
            else joySpent = checked(joySpent + expense.AmountMinor);
        }
        // A normal journal transfer is the source of truth: do not create a second savings operation.
        var deposited = active.Where(o => o.Kind == OperationKind.Transfer && o.DestinationId == doc.CushionAccountId).Sum(o => o.AmountMinor);
        long released = 0, protectedBills = 0;
        foreach (var bill in doc.Bills)
        {
            var paid = Paid(state, bill.Id);
            var target = bill.Settled || bill.Archived ? 0 : bill.AmountMinor;
            var covered = Covered(state, bill.Id);
            released = checked(released + Math.Max(0, covered - Math.Max(paid, target)));
            protectedBills = checked(protectedBills + Math.Max(0, Math.Min(covered, target) - paid));
        }
        var rawNecessary = checked(doc.Allocations.Sum(a => a.NecessaryMinor) - necessarySpent - released);
        var necessary = Math.Max(protectedBills, rawNecessary);
        var pending = Math.Max(0, doc.Allocations.Sum(a => a.SavingsMinor) - deposited);
        var budgetJoy = checked(doc.Allocations.Sum(a => a.JoyMinor) - joySpent + released + Math.Min(0, rawNecessary - protectedBills));
        // Keep the signed ledger total when identifying new money: clamping a deficit here
        // would count its recovery twice on the next salary.
        var freeCash = Math.Max(0, checked(cash - necessary - pending - budgetJoy));
        var joy = Math.Min(budgetJoy, checked(cash - necessary - pending));
        return new(necessary, pending, joy, cushion, freeCash);
    }

    public static BudgetProposal Propose(BudgetState state, string incomeId, string cushionAccountId,
        DateOnly until, IReadOnlyList<BudgetBill> bills)
    {
        var income = state.Finance.Operations.SingleOrDefault(o => o.Id == incomeId);
        Require(income is { Kind: OperationKind.Income }, "Выберите полученную зарплату или аванс из журнала.");
        Require(!state.Document.Allocations.Any(a => a.IncomeId == incomeId), "Это поступление уже распределено. Откройте его в истории бюджета.");
        Require(state.Finance.Accounts.Any(a => a.Id == cushionAccountId) && cushionAccountId != income!.AccountId,
            "Выберите отдельный счёт для подушки. Добавить счёт можно на вкладке «Финансы».");
        Require(state.Document.CushionAccountId is null || state.Document.CushionAccountId == cushionAccountId,
            "Для существующего бюджета используйте тот же счёт подушки.");
        var today = DateOnly.FromDateTime(DateTime.Today);
        Require(until >= today && until <= today.AddMonths(3), "Укажите следующее поступление: сегодня или в ближайшие три месяца.");
        ValidateBills(bills);
        foreach (var old in state.Document.Bills)
            Require(bills.Any(b => b.Id == old.Id), "Существующие платежи нельзя потерять. Обновите экран.");
        var working = state with { Document = state.Document with { CushionAccountId = cushionAccountId, Bills = bills.ToList() } };
        var totals = Totals(working);
        var available = Math.Min(income!.AmountMinor, totals.FreeCashMinor);
        Require(available > 0, "Свободных денег для этого распределения нет: остаток уже потрачен или распределён.");
        var pending = bills.Where(b => b.Due <= until && !b.Settled && !b.Archived).OrderBy(b => b.Due).ThenBy(b => b.Id)
            .Select(b => new BillFunding(b.Id, Needed(working, b), Paid(working, b.Id))).Where(f => f.AmountMinor > 0).ToArray();
        var required = pending.Sum(f => f.AmountMinor);
        var baseNecessary = available / 2;
        var baseSavings = available / 5;
        var necessary = Math.Min(available, Math.Max(baseNecessary, required));
        var savings = Math.Min(baseSavings, available - necessary);
        var joy = available - necessary - savings;
        var remaining = necessary;
        var funding = new List<BillFunding>();
        foreach (var entry in pending)
        {
            var amount = Math.Min(remaining, entry.AmountMinor);
            if (amount > 0) funding.Add(entry with { AmountMinor = amount });
            remaining -= amount;
        }
        var messages = new List<string>();
        if (available < income.AmountMinor) messages.Add($"Из поступления доступно для распределения {Money.Format(available)}: учитываем фактический остаток и ранее оставленные деньги.");
        if (required > baseNecessary) messages.Add($"На необходимое нужно больше половины поступления. Сначала уменьшили деньги на себя{(savings < baseSavings ? ", затем новый взнос в подушку" : "")}. Накопленную подушку не трогаем.");
        else messages.Add("50% оставляем на необходимое, 20% предлагаем перевести в подушку, 30% — на себя.");
        if (remaining > 0) messages.Add($"В необходимом ещё {Money.Format(remaining)} на повседневные траты сверх перечисленных платежей.");
        if (required > available) messages.Add($"Не хватает {Money.Format(required - available)} на обязательные расходы. Резервируем по ближайшим датам; часть платежей остаётся без денег.");
        var projected = working with { Document = working.Document with
        {
            Allocations = [..working.Document.Allocations, new(incomeId, income.AccountId, income.AmountMinor, income.Date, until,
                available, necessary, savings, joy, funding)],
            BaselineOperationIds = working.Document.Allocations.Count == 0
                ? state.Finance.Operations.Select(o => o.Id).ToList() : working.Document.BaselineOperationIds
        }};
        var projectedTotals = Totals(projected);
        if (totals.JoyMinor < 0)
            messages.Add($"Есть прежний перерасход. После этого распределения суммарно на себя останется {Money.Format(Math.Max(0, projectedTotals.JoyMinor))}.");
        if (projectedTotals.JoyMinor < 0)
            messages.Add($"Для оставленных сумм всё ещё не хватает {Money.Format(-projectedTotals.JoyMinor)}. Проверьте расходы; деньги из подушки автоматически не берём.");
        var shortage = Math.Max(Math.Max(0, required - available), Math.Max(0, -projectedTotals.JoyMinor));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { state, incomeId, cushionAccountId, until, bills, today }))));
        return new(fingerprint, available, necessary, savings, joy, shortage, string.Join("\n\n", messages), funding);
    }

    public static void ValidateBills(IReadOnlyList<BudgetBill> bills)
    {
        Require(bills.Select(b => b.Id).Distinct().Count() == bills.Count, "Платёж добавлен дважды.");
        foreach (var bill in bills)
        {
            Require(Guid.TryParse(bill.Id, out _) && !string.IsNullOrWhiteSpace(bill.Name) && bill.Name.Trim().Length <= 60,
                "Укажите название платежа до 60 символов.");
            Require(bill.AmountMinor >= 0 && bill.AmountMinor <= Money.MaximumMinor, "Сумма платежа должна быть от нуля до 1 трлн ₽.");
            Require(bill.Due.Year is >= 2000 and <= 2100, "Проверьте дату платежа.");
            Require(!bill.Archived || bill.Settled && !bill.Monthly, "Удалённый платёж должен быть закрыт без повторения.");
            Require(bill.SeriesId is null || Guid.TryParse(bill.SeriesId, out _), "Не удалось прочитать повторяющийся платёж.");
        }
        Require(bills.Where(b => b.SeriesId is not null).Select(b => (b.SeriesId, b.Due)).Distinct().Count() == bills.Count(b => b.SeriesId is not null),
            "Этот ежемесячный платёж на эту дату уже есть.");
    }
}
