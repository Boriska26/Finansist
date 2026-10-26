using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Finance;
using Finansist.Core.Storage;

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

public partial class FinanceViewModel(IFinanceStore store, IFinanceNavigation navigation, AppViewModel appState) : FinanceScreenViewModel
{
    public AppViewModel AppState { get; } = appState;
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
        IsLoaded = true;
        RebuildHistory();
        OnPropertyChanged(nameof(NoOperations));
    });

    public IReadOnlyList<string> HistoryPeriods { get; } = ["Последние 10", "За период", "За всё время"];
    public IReadOnlyList<string> HistoryKinds { get; } = ["Все операции", "Доход", "Расход", "Перевод"];
    [ObservableProperty] private int historyPeriodIndex;
    [ObservableProperty] private int historyKindIndex;
    [ObservableProperty] private DateTime? historyFrom = DateTime.Today.AddMonths(-1);
    [ObservableProperty] private DateTime? historyTo = DateTime.Today;
    [ObservableProperty] private bool showHistoryFilters;
    [ObservableProperty] private string historySummary = "Последние 10 · Все операции";
    private int appliedPeriod;
    private int appliedKind;
    private DateOnly appliedFrom;
    private DateOnly appliedTo;
    private IReadOnlyList<FinanceOperation> history = [];
    private int matchedCount;
    public DateTime Today => DateTime.Today;
    public bool ShowDateRange => HistoryPeriodIndex == 1;
    public bool NoMatchingOperations => IsLoaded && snapshot.Operations.Count > 0 && history.Count == 0;
    partial void OnHistoryPeriodIndexChanged(int value) => OnPropertyChanged(nameof(ShowDateRange));
    [RelayCommand] private void ToggleHistoryFilters() => ShowHistoryFilters = !ShowHistoryFilters;
    [RelayCommand] private Task ApplyHistoryFiltersAsync() => RunAsync(() =>
    {
        if (HistoryPeriodIndex is < 0 or > 2 || HistoryKindIndex is < 0 or > 3)
            throw new FinanceValidationException("Выберите период и тип операций.");
        if (HistoryPeriodIndex == 1 && (HistoryFrom is null || HistoryTo is null || HistoryFrom.Value.Date > HistoryTo.Value.Date))
            throw new FinanceValidationException("Укажите начало и конец периода. Начало не может быть позже конца.");
        appliedPeriod = HistoryPeriodIndex; appliedKind = HistoryKindIndex;
        if (appliedPeriod == 1)
        {
            appliedFrom = DateOnly.FromDateTime(HistoryFrom!.Value);
            appliedTo = DateOnly.FromDateTime(HistoryTo!.Value);
        }
        RebuildHistory(); ShowHistoryFilters = false;
        return Task.CompletedTask;
    });
    [RelayCommand] private Task ResetHistoryFiltersAsync() => RunAsync(() =>
    {
        HistoryPeriodIndex = 0; HistoryKindIndex = 0;
        HistoryFrom = DateTime.Today.AddMonths(-1); HistoryTo = DateTime.Today;
        appliedPeriod = 0; appliedKind = 0;
        RebuildHistory(); return Task.CompletedTask;
    });
    private void RebuildHistory()
    {
        IEnumerable<FinanceOperation> query = snapshot.Operations;
        var kind = appliedKind switch { 1 => OperationKind.Income, 2 => OperationKind.Expense, 3 => OperationKind.Transfer, _ => (OperationKind?)null };
        if (kind is { } selected) query = query.Where(o => o.Kind == selected);
        if (appliedPeriod == 1) query = query.Where(o => o.Date >= appliedFrom && o.Date <= appliedTo);
        var matched = query.OrderByDescending(o => o.Date).ToArray();
        matchedCount = matched.Length;
        history = appliedPeriod == 0 ? matched.Take(10).ToArray() : matched;
        Operations.Clear(); AppendOperations();
        OnPropertyChanged(nameof(NoMatchingOperations));
    }
    [RelayCommand] private void LoadMore() { if (IsIdle) AppendOperations(); }
    private void AppendOperations()
    {
        var names = snapshot.Accounts.ToDictionary(a => a.Id, a => a.Name);
        foreach (var operation in history.Skip(Operations.Count).Take(10))
        {
            var name = names[operation.AccountId];
            if (operation.DestinationId is not null) name += " → " + names[operation.DestinationId];
            Operations.Add(new OperationRow(operation, name));
        }
        HasMore = Operations.Count < history.Count;
        var period = appliedPeriod == 1 ? $"{appliedFrom:dd.MM.yyyy} — {appliedTo:dd.MM.yyyy}" : HistoryPeriods[appliedPeriod];
        HistorySummary = $"{period} · {HistoryKinds[appliedKind]}\nПоказано {Operations.Count} из {matchedCount}";
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
