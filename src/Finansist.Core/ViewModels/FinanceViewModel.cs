using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Finance;
using Finansist.Core.Storage;
using Finansist.Core.Importing;

namespace Finansist.Core.ViewModels;

public interface IFinanceNavigation
{
    Task EditAccountAsync(Account? account);
    Task EditSalaryAsync();
    Task DistributeIncomeAsync(string incomeId);
    Task EditOperationAsync(FinanceOperation? operation);
    Task BackAsync();
    Task<bool> ConfirmAsync(string title, string message);
}

public abstract partial class FinanceScreenViewModel : ObservableObject
{
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string errorMessage = "";
    public bool IsIdle => !IsBusy;
    public bool HasError => ErrorMessage.Length > 0;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    protected async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = "";
        try { await action(); }
        catch (FinanceValidationException exception) { ErrorMessage = exception.Message; }
        catch (StorageException exception) { ErrorMessage = exception.Message; }
        catch (OverflowException) { ErrorMessage = "Суммы слишком велики для расчёта. Уменьшите значения."; }
        finally { IsBusy = false; }
    }
}

public sealed record OperationRow(FinanceOperation Operation, string AccountText)
{
    public string Title => Operation.Kind == OperationKind.Transfer ? "Перевод между счетами" : Operation.Category;
    public string AmountText => (Operation.Kind switch { OperationKind.Expense => "−", OperationKind.Income => "+", _ => "↔ " }) + Money.Format(Operation.AmountMinor);
    public string Details => $"{Operation.Date:dd.MM.yyyy} · {AccountText}";
    public string Note => Operation.Note;
    public bool HasNote => Note.Length > 0;
}

public sealed record AccountMove(string SourceId, string TargetId, bool After);

public partial class FinanceViewModel(IFinanceStore store, IFinanceNavigation navigation, AppViewModel appState, IBankImportStore? importStore = null, IImportNavigation? importNavigation = null) : FinanceScreenViewModel
{
    public AppViewModel AppState { get; } = appState;
    [ObservableProperty] private string importLabel = "Банковские уведомления — настроить / проверить";
    [RelayCommand] private Task ImportInboxAsync() => RunAsync(() => importNavigation?.OpenInboxAsync() ?? Task.CompletedTask);
    public ObservableCollection<AccountBalance> Accounts { get; } = [];
    public ObservableCollection<AccountBalance> VisibleAccounts { get; } = [];
    [ObservableProperty] private bool isAccountsExpanded;
    public bool CanToggleAccounts => Accounts.Count > 1;
    public bool CanReorderAccounts => CanToggleAccounts && IsAccountsExpanded && IsIdle;
    public string AccountsToggleText => IsAccountsExpanded ? "Свернуть: оставить первый счёт" : $"Показать остальные ({Math.Max(0, Accounts.Count - 1)})";
    partial void OnIsAccountsExpandedChanged(bool value) => UpdateVisibleAccounts();
    private void UpdateVisibleAccounts()
    {
        VisibleAccounts.Clear();
        foreach (var account in Accounts.Take(IsAccountsExpanded ? Accounts.Count : 1)) VisibleAccounts.Add(account);
        OnPropertyChanged(nameof(CanToggleAccounts));
        OnPropertyChanged(nameof(CanReorderAccounts));
        OnPropertyChanged(nameof(AccountsToggleText));
    }
    [RelayCommand] private Task ToggleAccountsAsync() => RunAsync(async () =>
    {
        if (!CanToggleAccounts) return;
        var expanded = !IsAccountsExpanded;
        await store.SaveAccountLayoutAsync(new AccountLayout(Accounts.Select(a => a.Id).ToArray(), expanded));
        IsAccountsExpanded = expanded;
    });
    [RelayCommand] private Task MoveAccountAsync(AccountMove move) => RunAsync(async () =>
    {
        if (!IsAccountsExpanded || move.SourceId == move.TargetId) return;
        var ordered = Accounts.ToList();
        var source = ordered.FindIndex(a => a.Id == move.SourceId);
        if (source < 0 || !ordered.Any(a => a.Id == move.TargetId))
            throw new FinanceValidationException("Список счетов изменился. Обновите экран.");
        var account = ordered[source]; ordered.RemoveAt(source);
        var target = ordered.FindIndex(a => a.Id == move.TargetId);
        ordered.Insert(target + (move.After ? 1 : 0), account);
        if (Accounts.Select(a => a.Id).SequenceEqual(ordered.Select(a => a.Id))) return;
        await store.SaveAccountLayoutAsync(new AccountLayout(ordered.Select(a => a.Id).ToArray(), true));
        Accounts.Clear(); foreach (var item in ordered) Accounts.Add(item);
        UpdateVisibleAccounts();
    });
    public ObservableCollection<OperationRow> Operations { get; } = [];
    private FinanceSnapshot snapshot = FinanceSnapshot.Empty;
    [ObservableProperty] private string totalText = "— ₽";
    [ObservableProperty] private bool hasAccounts;
    [ObservableProperty] private bool isLoaded;
    [ObservableProperty] private bool hasMore;
    public bool IsEmpty => IsLoaded && !HasAccounts;
    public bool NoOperations => IsLoaded && snapshot.Operations.Count == 0;
    partial void OnHasAccountsChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnIsLoadedChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        snapshot = await store.ReadFinanceAsync();
        var layout = await store.ReadAccountLayoutAsync();
        Accounts.Clear();
        foreach (var account in snapshot.Accounts) Accounts.Add(account);
        IsAccountsExpanded = layout.Expanded;
        UpdateVisibleAccounts();
        HasAccounts = Accounts.Count > 0;
        TotalText = HasAccounts ? Money.Format(snapshot.TotalMinor) : "— ₽";
        Operations.Clear();
        AppendOperations();
        IsLoaded = true;
        OnPropertyChanged(nameof(NoOperations));
        if (importStore is not null)
        {
            var inbox = await importStore.ReadImportsAsync();
            ImportLabel = inbox.Pending.Count > 0 ? $"Найдено операций: {inbox.Pending.Count} — проверить" : "Банковские уведомления — настроить / проверить";
        }
    });

    [RelayCommand]
    private void LoadMore() => AppendOperations();

    private void AppendOperations()
    {
        var names = snapshot.Accounts.ToDictionary(a => a.Id, a => a.Name);
        foreach (var operation in snapshot.Operations.Skip(Operations.Count).Take(50))
        {
            var name = names[operation.AccountId];
            if (operation.DestinationId is not null) name += " → " + names[operation.DestinationId];
            Operations.Add(new OperationRow(operation, name));
        }
        HasMore = Operations.Count < snapshot.Operations.Count;
    }

    [RelayCommand] private Task SalaryAsync() => RunAsync(navigation.EditSalaryAsync);
    [RelayCommand] private Task AddAccountAsync() => RunAsync(() => navigation.EditAccountAsync(null));
    [RelayCommand] private Task EditAccountAsync(AccountBalance account) => RunAsync(() => navigation.EditAccountAsync(account.Account));
    [RelayCommand] private Task AddOperationAsync() => RunAsync(async () =>
    {
        if (!HasAccounts) throw new FinanceValidationException("Сначала добавьте хотя бы один счёт.");
        await navigation.EditOperationAsync(null);
    });
    [RelayCommand] private Task EditOperationAsync(OperationRow row) => RunAsync(() => navigation.EditOperationAsync(row.Operation));
}
