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
        Assert.Equal(50, vm.Operations.Count); Assert.True(vm.HasMore); Assert.False(vm.IsEmpty);
        vm.LoadMoreCommand.Execute(null);
        Assert.Equal(65, vm.Operations.Count); Assert.False(vm.HasMore);
        store.Snapshot = FinanceSnapshot.Empty;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsEmpty); Assert.True(vm.NoOperations); Assert.Empty(vm.Operations); Assert.Equal("— ₽", vm.TotalText);
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
