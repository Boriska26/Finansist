using Finansist.Core.Finance;

namespace Finansist.Core.Budgeting;

public sealed record BudgetBill(string Id, string Name, DateOnly Due, long AmountMinor, bool Monthly = false,
    string? SeriesId = null, bool Settled = false, bool Archived = false);
public sealed record BillFunding(string BillId, long AmountMinor, long PaidAtFundingMinor = 0);
public sealed record IncomeAllocation(string IncomeId, string AccountId, long OriginalMinor, DateOnly IncomeDate,
    DateOnly Until, long AvailableMinor, long NecessaryMinor, long SavingsMinor, long JoyMinor,
    List<BillFunding> Funding);
public sealed record ExpensePurpose(string OperationId, bool Necessary, string? BillId = null);
public sealed record BudgetDocument
{
    public string? CushionAccountId { get; init; }
    public List<BudgetBill> Bills { get; init; } = [];
    public List<IncomeAllocation> Allocations { get; init; } = [];
    public List<ExpensePurpose> Purposes { get; init; } = [];
    public List<string> BaselineOperationIds { get; init; } = [];
}
public sealed record BudgetState(long Revision, BudgetDocument Document, FinanceSnapshot Finance);
public sealed record BudgetProposal(string Fingerprint, long AvailableMinor, long NecessaryMinor, long SavingsMinor,
    long JoyMinor, long ShortageMinor, string Explanation, List<BillFunding> Funding);
public sealed record BudgetTotals(long NecessaryMinor, long PendingSavingsMinor, long JoyMinor, long CushionMinor, long FreeCashMinor);
public interface IBudgetStore
{
    Task<BudgetState> ReadBudgetAsync();
    Task<BudgetState> SaveBillsAsync(IReadOnlyList<BudgetBill> bills, long revision);
    Task<BudgetState> DeleteBillAsync(string billId, long revision);
    Task<BudgetState> AllocateAsync(string incomeId, string cushionAccountId, DateOnly until,
        IReadOnlyList<BudgetBill> bills, string fingerprint);
    Task<BudgetState> CancelAllocationAsync(string incomeId);
    Task<BudgetState> SaveBudgetOperationAsync(FinanceOperation operation, ExpensePurpose? purpose);
}
