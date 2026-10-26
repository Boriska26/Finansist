namespace Finansist.Core.Finance;

public sealed record AccountLayout(IReadOnlyList<string> Order, bool Expanded);

public interface IFinanceStore
{
    Task<FinanceSnapshot> ReadFinanceAsync();
    Task<AccountLayout> ReadAccountLayoutAsync();
    Task SaveAccountLayoutAsync(AccountLayout layout);
    Task<FinanceSnapshot> SaveAccountAsync(Account account);
    Task<FinanceSnapshot> DeleteAccountAsync(string id);
    Task<FinanceSnapshot> SaveOperationAsync(FinanceOperation operation);
    Task<FinanceSnapshot> DeleteOperationAsync(string id);
}
