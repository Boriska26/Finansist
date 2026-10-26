namespace Finansist.Core.Finance;

public interface IFinanceStore
{
    Task<FinanceSnapshot> ReadFinanceAsync();
    Task<FinanceSnapshot> SaveAccountAsync(Account account);
    Task<FinanceSnapshot> DeleteAccountAsync(string id);
    Task<FinanceSnapshot> SaveOperationAsync(FinanceOperation operation);
    Task<FinanceSnapshot> DeleteOperationAsync(string id);
}
