using Finansist.Core.Finance;
using Finansist.Core.Storage;
using Finansist.Core.ViewModels;

namespace Finansist.Core.Tests;

public sealed class FinanceViewModelTests
{
    private static Account Account() => new(Guid.NewGuid().ToString(), "Тестовый счёт", 0);

    [Fact]
    public async Task InvalidAmount_KeepsFormOpenAndDoesNotCallStorage()
    {
        var store = new MemoryStore(); var navigation = new Navigation();
        var vm = new OperationEditorViewModel(store, navigation);
        vm.Configure(null); await vm.LoadCommand.ExecuteAsync(null);
        vm.Amount = "1,234";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.HasError); Assert.False(vm.IsBusy); Assert.False(vm.IsSaved);
        Assert.Equal(0, store.SaveCalls); Assert.Equal(0, navigation.BackCalls);
    }

    [Fact]
    public async Task RetryAfterStorageFailure_KeepsSameIdAndUserInput()
    {
        var store = new MemoryStore { FailSave = true }; var navigation = new Navigation();
        var vm = new OperationEditorViewModel(store, navigation);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.Amount = "12,34"; vm.Note = "Синтетическая заметка";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.HasError); Assert.False(vm.IsSaved); Assert.Equal(0, navigation.BackCalls);
        var firstId = store.LastOperation!.Id;
        store.FailSave = false;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(firstId, store.LastOperation.Id);
        Assert.Equal(1234, store.LastOperation.AmountMinor);
        Assert.Equal("Синтетическая заметка", store.LastOperation.Note);
        Assert.True(vm.IsSaved); Assert.False(vm.HasError); Assert.Equal(1, navigation.BackCalls);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(2, store.SaveCalls); // One failed attempt and one successful attempt only.
    }

    [Fact]
    public async Task SaveInProgress_PreventsAnotherWriteOrCancel()
    {
        var pending = new TaskCompletionSource<FinanceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MemoryStore { PendingSave = pending }; var navigation = new Navigation();
        var vm = new OperationEditorViewModel(store, navigation);
        await vm.LoadCommand.ExecuteAsync(null); vm.Amount = "1";
        var first = vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        await vm.SaveCommand.ExecuteAsync(null);
        await vm.CancelCommand.ExecuteAsync(null);
        Assert.Equal(1, store.SaveCalls); Assert.Equal(0, navigation.ConfirmCalls); Assert.Equal(0, navigation.BackCalls);
        pending.SetResult(store.Snapshot);
        await first;
        Assert.True(vm.IsSaved); Assert.Equal(1, navigation.BackCalls);
    }

    [Fact]
    public async Task DeleteRequiresConfirmation()
    {
        var store = new MemoryStore(); var navigation = new Navigation { Confirmation = false };
        var vm = new AccountEditorViewModel(store, navigation);
        vm.Configure(store.Account);
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(0, store.DeleteCalls); Assert.Equal(0, navigation.BackCalls);
        navigation.Confirmation = true;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(1, store.DeleteCalls); Assert.Equal(1, navigation.BackCalls);
    }

    [Fact]
    public async Task EditingOperation_RestoresDateAndTransferAccounts()
    {
        var store = new MemoryStore(); var destination = Account() with { Name = "Получатель" };
        store.Snapshot = new([new(store.Account, 100), new(destination, 200)], []);
        var operation = new FinanceOperation(Guid.NewGuid().ToString(), OperationKind.Transfer, store.Account.Id,
            destination.Id, 1234, new DateOnly(2025, 3, 4), "Перевод", "Тест");
        var vm = new OperationEditorViewModel(store, new Navigation()); vm.Configure(operation);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.True(vm.IsTransfer); Assert.True(vm.IsExisting);
        Assert.Equal(destination.Id, vm.DestinationAccount!.Id);
        Assert.Equal(operation.Date.ToDateTime(TimeOnly.MinValue), vm.Date);
        Assert.Equal("12,34", vm.Amount);
    }

    [Fact]
    public async Task HistoryLoadsInBatches_AndEmptyStateUpdatesAfterRefresh()
    {
        var store = new MemoryStore(); var nav = new Navigation();
        var operations = Enumerable.Range(0, 65).Select(i => new FinanceOperation(Guid.NewGuid().ToString(),
            OperationKind.Income, store.Account.Id, null, 1, new DateOnly(2026, 1, 1), "Тест", "")).ToArray();
        store.Snapshot = Ledger.Calculate([store.Account], operations);
        var app = new AppViewModel(new ThemeStore(), new ThemeService());
        var vm = new FinanceViewModel(store, nav, app);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(10, vm.Operations.Count); Assert.False(vm.HasMore); Assert.False(vm.IsEmpty);
        vm.HistoryPeriodIndex = 2; await vm.ApplyHistoryFiltersCommand.ExecuteAsync(null);
        Assert.Equal(10, vm.Operations.Count); Assert.True(vm.HasMore);
        while (vm.HasMore) vm.LoadMoreCommand.Execute(null);
        Assert.Equal(65, vm.Operations.Count); Assert.False(vm.HasMore);
        store.Snapshot = FinanceSnapshot.Empty;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsEmpty); Assert.True(vm.NoOperations); Assert.Empty(vm.Operations); Assert.Equal("— ₽", vm.TotalText);
    }

    [Fact]
    public async Task Accounts_CollapseKeepsFirstAndReorderPersistsAcrossRefresh()
    {
        var store = new MemoryStore();
        var second = Account(); var third = Account();
        store.Snapshot = new([new(store.Account, 100), new(second, 200), new(third, 300)], []);
        var vm = new FinanceViewModel(store, new Navigation(), new(new ThemeStore(), new ThemeService()));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.IsAccountsExpanded); Assert.True(vm.CanToggleAccounts);
        Assert.Equal(store.Account.Id, Assert.Single(vm.VisibleAccounts).Id);
        await vm.ToggleAccountsCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.VisibleAccounts.Count);
        await vm.MoveAccountCommand.ExecuteAsync(new AccountMove(third.Id, store.Account.Id, false));
        Assert.Equal(third.Id, vm.Accounts[0].Id);
        await vm.MoveAccountCommand.ExecuteAsync(new AccountMove(store.Account.Id, second.Id, true));
        Assert.Equal(new[] { third.Id, second.Id, store.Account.Id }, store.Layout.Order);
        await vm.ToggleAccountsCommand.ExecuteAsync(null);
        Assert.Equal(third.Id, Assert.Single(vm.VisibleAccounts).Id);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.IsAccountsExpanded); Assert.Equal(third.Id, Assert.Single(vm.VisibleAccounts).Id);
        Assert.Equal(600, store.Snapshot.TotalMinor);
    }

    [Fact]
    public async Task FailedLayoutWritePreservesVisibleOrderAndExpansion()
    {
        var store = new MemoryStore(); var second = Account();
        store.Snapshot = new([new(store.Account, 100), new(second, 200)], []);
        var vm = new FinanceViewModel(store, new Navigation(), new(new ThemeStore(), new ThemeService()));
        await vm.RefreshCommand.ExecuteAsync(null);
        store.FailLayout = true;
        await vm.ToggleAccountsCommand.ExecuteAsync(null);
        Assert.True(vm.HasError); Assert.False(vm.IsAccountsExpanded); Assert.Single(vm.VisibleAccounts);
        store.FailLayout = false; await vm.ToggleAccountsCommand.ExecuteAsync(null);
        store.FailLayout = true;
        await vm.MoveAccountCommand.ExecuteAsync(new AccountMove(second.Id, store.Account.Id, false));
        Assert.True(vm.HasError); Assert.Equal(store.Account.Id, vm.VisibleAccounts[0].Id);
        Assert.True(vm.IsAccountsExpanded);
    }

    [Fact]
    public async Task ZeroOrOneAccountHasNoExpansionAction()
    {
        var store = new MemoryStore();
        var vm = new FinanceViewModel(store, new Navigation(), new(new ThemeStore(), new ThemeService()));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.CanToggleAccounts); Assert.Single(vm.VisibleAccounts);
        store.Snapshot = FinanceSnapshot.Empty;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.CanToggleAccounts); Assert.Empty(vm.VisibleAccounts);
        await vm.ToggleAccountsCommand.ExecuteAsync(null);
        Assert.False(vm.IsAccountsExpanded);
    }

    private static MemoryStore HistoryStore()
    {
        var store = new MemoryStore(); var other = Account() with { Name = "Тестовый второй счёт" };
        var operations = Enumerable.Range(0, 45).Select(i => new FinanceOperation(Guid.NewGuid().ToString(),
            (OperationKind)(i % 3), store.Account.Id, i % 3 == 2 ? other.Id : null,
            100 + i, new DateOnly(2025, 1, 1).AddDays(i), "Синтетика", "")).ToArray();
        store.Snapshot = Ledger.Calculate([store.Account, other], operations);
        return store;
    }

    [Fact]
    public async Task HistoryDefaultsToTenNewestWithoutChangingLedger()
    {
        var store = HistoryStore(); var vm = new FinanceViewModel(store, new Navigation(), new(new ThemeStore(), new ThemeService()));
        var total = store.Snapshot.TotalMinor;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(store.Snapshot.Operations.OrderByDescending(o => o.Date).Take(10).Select(o => o.Id), vm.Operations.Select(r => r.Operation.Id));
        Assert.False(vm.HasMore); Assert.Equal(total, store.Snapshot.TotalMinor);
        Assert.Equal(0, vm.HistoryPeriodIndex); Assert.Equal(0, vm.HistoryKindIndex);
    }

    [Theory]
    [InlineData(1, OperationKind.Income)]
    [InlineData(2, OperationKind.Expense)]
    [InlineData(3, OperationKind.Transfer)]
    public async Task HistoryCombinesInclusiveDateRangeWithOperationKind(int index, OperationKind kind)
    {
        var store = HistoryStore(); var vm = new FinanceViewModel(store, new Navigation(), new(new ThemeStore(), new ThemeService()));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.HistoryPeriodIndex = 1; vm.HistoryKindIndex = index;
        vm.HistoryFrom = new DateTime(2025, 1, 5); vm.HistoryTo = new DateTime(2025, 1, 20);
        await vm.ApplyHistoryFiltersCommand.ExecuteAsync(null);
        var expected = store.Snapshot.Operations.Where(o => o.Kind == kind && o.Date >= new DateOnly(2025, 1, 5) && o.Date <= new DateOnly(2025, 1, 20)).OrderByDescending(o => o.Date).ToArray();
        Assert.Equal(expected.Select(o => o.Id), vm.Operations.Select(r => r.Operation.Id));
        Assert.False(vm.HasError); Assert.False(vm.HasMore); Assert.False(vm.NoMatchingOperations);
    }

    [Fact]
    public async Task LastTenIncomeMeansTenIncomesRatherThanIncomesAmongLastTenOperations()
    {
        var store = HistoryStore(); var vm = new FinanceViewModel(store, new Navigation(), new(new ThemeStore(), new ThemeService()));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.HistoryKindIndex = 1; await vm.ApplyHistoryFiltersCommand.ExecuteAsync(null);
        Assert.Equal(10, vm.Operations.Count); Assert.All(vm.Operations, row => Assert.Equal(OperationKind.Income, row.Operation.Kind));
        Assert.False(vm.HasMore);
        vm.HistoryPeriodIndex = 2; await vm.ApplyHistoryFiltersCommand.ExecuteAsync(null);
        while (vm.HasMore) vm.LoadMoreCommand.Execute(null);
        Assert.Equal(15, vm.Operations.Count); Assert.Equal(15, vm.Operations.Select(o => o.Operation.Id).Distinct().Count());
    }

    [Fact]
    public async Task InvalidRangeKeepsPreviousResultsAndResetRestoresDefaults()
    {
        var store = HistoryStore(); var vm = new FinanceViewModel(store, new Navigation(), new(new ThemeStore(), new ThemeService()));
        await vm.RefreshCommand.ExecuteAsync(null);
        var ids = vm.Operations.Select(r => r.Operation.Id).ToArray(); var summary = vm.HistorySummary;
        vm.HistoryPeriodIndex = 1; vm.HistoryFrom = new DateTime(2025, 1, 20); vm.HistoryTo = new DateTime(2025, 1, 5);
        await vm.ApplyHistoryFiltersCommand.ExecuteAsync(null);
        Assert.True(vm.HasError); Assert.Equal(ids, vm.Operations.Select(r => r.Operation.Id)); Assert.Equal(summary, vm.HistorySummary);
        await vm.ResetHistoryFiltersCommand.ExecuteAsync(null);
        Assert.False(vm.HasError); Assert.Equal(0, vm.HistoryPeriodIndex); Assert.Equal(0, vm.HistoryKindIndex);
        Assert.Equal(ids, vm.Operations.Select(r => r.Operation.Id));
    }

    [Fact]
    public async Task EmptyFilteredResultIsDistinctFromEmptyJournal()
    {
        var store = HistoryStore(); var vm = new FinanceViewModel(store, new Navigation(), new(new ThemeStore(), new ThemeService()));
        await vm.RefreshCommand.ExecuteAsync(null); var total = vm.TotalText;
        vm.HistoryPeriodIndex = 1; vm.HistoryFrom = new DateTime(2024, 1, 1); vm.HistoryTo = new DateTime(2024, 1, 31);
        await vm.ApplyHistoryFiltersCommand.ExecuteAsync(null);
        Assert.Empty(vm.Operations); Assert.True(vm.NoMatchingOperations); Assert.False(vm.NoOperations); Assert.False(vm.HasMore);
        Assert.Equal(total, vm.TotalText);
        await vm.ResetHistoryFiltersCommand.ExecuteAsync(null); Assert.False(vm.NoMatchingOperations); Assert.Equal(10, vm.Operations.Count);
    }

    [Fact]
    public async Task RefreshPreservesAppliedFilterAndDoesNotApplyDraftChanges()
    {
        var store = HistoryStore(); var vm = new FinanceViewModel(store, new Navigation(), new(new ThemeStore(), new ThemeService()));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.HistoryPeriodIndex = 2; vm.HistoryKindIndex = 1; await vm.ApplyHistoryFiltersCommand.ExecuteAsync(null);
        vm.LoadMoreCommand.Execute(null); Assert.Equal(15, vm.Operations.Count);
        vm.HistoryPeriodIndex = 0; vm.HistoryKindIndex = 2;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(10, vm.Operations.Count); Assert.True(vm.HasMore);
        Assert.All(vm.Operations, row => Assert.Equal(OperationKind.Income, row.Operation.Kind));
        Assert.Contains("За всё время", vm.HistorySummary); Assert.Contains("Доход", vm.HistorySummary);
    }

    private sealed class MemoryStore : IFinanceStore
    {
        public Account Account { get; } = FinanceViewModelTests.Account();
        public FinanceSnapshot Snapshot { get; set; }
        public MemoryStore() => Snapshot = new([new(Account, 0)], []);
        public int SaveCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public bool FailSave { get; set; }
        public TaskCompletionSource<FinanceSnapshot>? PendingSave { get; set; }
        public FinanceOperation? LastOperation { get; private set; }
        public AccountLayout Layout { get; set; } = new([], false);
        public bool FailLayout { get; set; }
        public Task<AccountLayout> ReadAccountLayoutAsync() => Task.FromResult(Layout);
        public Task SaveAccountLayoutAsync(AccountLayout layout)
        {
            if (FailLayout) return Task.FromException(new StorageException("Синтетическая ошибка"));
            Layout = layout;
            var order = layout.Order.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
            Snapshot = Snapshot with { Accounts = Snapshot.Accounts.OrderBy(a => order[a.Id]).ToArray() };
            return Task.CompletedTask;
        }
        public Task<FinanceSnapshot> ReadFinanceAsync() => Task.FromResult(Snapshot);
        public Task<FinanceSnapshot> SaveAccountAsync(Account account) => Task.FromResult(Snapshot);
        public Task<FinanceSnapshot> DeleteAccountAsync(string id) { DeleteCalls++; return Task.FromResult(Snapshot); }
        public Task<FinanceSnapshot> DeleteOperationAsync(string id) { DeleteCalls++; return Task.FromResult(Snapshot); }
        public Task<FinanceSnapshot> SaveOperationAsync(FinanceOperation operation)
        {
            SaveCalls++; LastOperation = operation;
            if (FailSave) return Task.FromException<FinanceSnapshot>(new StorageException("Синтетическая ошибка"));
            return PendingSave?.Task ?? Task.FromResult(Snapshot);
        }
    }
    private sealed class Navigation : IFinanceNavigation
    {
        public int BackCalls { get; private set; }
        public int ConfirmCalls { get; private set; }
        public bool Confirmation { get; set; } = true;
        public Task EditSalaryAsync() => Task.CompletedTask;
        public Task DistributeIncomeAsync(string incomeId) => Task.CompletedTask;
        public Task EditAccountAsync(Account? account) => Task.CompletedTask;
        public Task EditOperationAsync(FinanceOperation? operation) => Task.CompletedTask;
        public Task BackAsync() { BackCalls++; return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string message) { ConfirmCalls++; return Task.FromResult(Confirmation); }
    }
    private sealed class ThemeStore : ILocalStore
    {
        public Task<ThemePreference> ReadThemeAsync() => Task.FromResult(ThemePreference.System);
        public Task SaveThemeAsync(ThemePreference theme) => Task.CompletedTask;
    }
    private sealed class ThemeService : IThemeService { public void Apply(ThemePreference theme) { } }
}
