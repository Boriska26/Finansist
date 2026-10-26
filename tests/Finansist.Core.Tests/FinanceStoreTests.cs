using Finansist.Core.Finance;
using Finansist.Core.Storage;

namespace Finansist.Core.Tests;

public sealed class FinanceStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "finansist-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(directory, "synthetic.db");
    private LocalStore Store() => new(new TestConnectionFactory(DatabasePath));
    private static Account NewAccount(string name, long opening = 0) => new(Guid.NewGuid().ToString(), name, opening);
    private static FinanceOperation Operation(Account account, OperationKind kind, long amount, Account? destination = null) =>
        new(Guid.NewGuid().ToString(), kind, account.Id, destination?.Id, amount, DateOnly.FromDateTime(DateTime.Today), "Синтетическая категория", "Тест");
    public FinanceStoreTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task StageTwoRollback_PreservesFinanceAndIgnoresOldPlanning(int version)
    {
        var store = Store();
        var account = NewAccount("Синтетический счёт", 100000);
        await store.SaveAccountAsync(account);
        var operation = Operation(account, OperationKind.Expense, 20000);
        await store.SaveOperationAsync(operation);
        await store.SaveThemeAsync(ThemePreference.Dark);
        using (var db = new NativeSqliteConnection(DatabasePath))
        {
            db.Execute("DROP TABLE salary_budget");
            db.Execute("PRAGMA user_version = " + version);
            if (version == 3)
            {
                db.Execute("CREATE TABLE planning_state (id INTEGER PRIMARY KEY, revision INTEGER NOT NULL, payload TEXT NOT NULL, accepted TEXT)");
                // Deliberately unreadable plan: stage 2 must never deserialize it.
                db.Execute("INSERT INTO planning_state VALUES (1, 7, 'synthetic archived plan', 'synthetic approval')");
            }
        }
        Assert.Equal(80000, (await Store().ReadFinanceAsync()).TotalMinor);
        Assert.Equal(ThemePreference.Dark, await Store().ReadThemeAsync());
        Assert.Equal(70000, (await Store().SaveOperationAsync(operation with { AmountMinor = 30000 })).TotalMinor);
        Assert.Equal(100000, (await Store().DeleteOperationAsync(operation.Id)).TotalMinor);
        using var check = new NativeSqliteConnection(DatabasePath);
        Assert.Equal("4", check.Scalar("PRAGMA user_version"));
        Assert.Equal("ok", check.Scalar("PRAGMA integrity_check"));
        if (version == 3)
        {
            Assert.Equal("synthetic archived plan", check.Scalar("SELECT payload FROM planning_state WHERE id = 1"));
            Assert.Equal("synthetic approval", check.Scalar("SELECT accepted FROM planning_state WHERE id = 1"));
            Assert.Equal("7", check.Scalar("SELECT revision FROM planning_state WHERE id = 1"));
        }
        else Assert.Equal("0", check.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='planning_state'"));
    }

    [Fact]
    public async Task UpgradeFromStageOne_PreservesThemeAndStartsWithEmptyFinance()
    {
        using (var db = new NativeSqliteConnection(DatabasePath))
        {
            db.Execute("CREATE TABLE app_settings (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL)");
            db.Execute("INSERT INTO app_settings VALUES ('theme', 'dark')");
            db.Execute("PRAGMA user_version = 1");
        }
        var store = Store();
        Assert.Equal(ThemePreference.Dark, await store.ReadThemeAsync());
        var snapshot = await store.ReadFinanceAsync();
        Assert.Empty(snapshot.Accounts);
        Assert.Empty(snapshot.Operations);
        using var check = new NativeSqliteConnection(DatabasePath);
        Assert.Equal("4", check.Scalar("PRAGMA user_version"));
        Assert.Equal("ok", check.Scalar("PRAGMA integrity_check"));
    }

    [Fact]
    public async Task FailedUpgradeFromStageOne_PreservesOldSchemaAndTheme()
    {
        using (var db = new NativeSqliteConnection(DatabasePath))
        {
            db.Execute("CREATE TABLE app_settings (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL)");
            db.Execute("INSERT INTO app_settings VALUES ('theme', 'light')");
            db.Execute("PRAGMA user_version = 1");
        }
        var store = new LocalStore(new FaultFactory(DatabasePath, "PRAGMA user_version = 2"));
        await Assert.ThrowsAsync<StorageException>(() => store.ReadFinanceAsync());
        using (var check = new NativeSqliteConnection(DatabasePath))
        {
            Assert.Equal("1", check.Scalar("PRAGMA user_version"));
            Assert.Equal("light", check.Scalar("SELECT value FROM app_settings WHERE key='theme'"));
            Assert.Equal("0", check.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('accounts','operations')"));
        }
        Assert.Empty((await Store().ReadFinanceAsync()).Accounts);
    }

    [Fact]
    public async Task IncomeExpenseAndTransfer_AreExactAndSurviveReopening()
    {
        var card = NewAccount("Тестовая карта", 100_000);
        var cash = NewAccount("Тестовые наличные", 20_000);
        var store = Store();
        await store.SaveAccountAsync(card);
        await store.SaveAccountAsync(cash);
        await store.SaveOperationAsync(Operation(card, OperationKind.Income, 50_001));
        await store.SaveOperationAsync(Operation(card, OperationKind.Expense, 12_345));
        await store.SaveOperationAsync(Operation(card, OperationKind.Transfer, 30_000, cash));
        var result = await Store().ReadFinanceAsync();
        Assert.Equal(107_656, result.Accounts.Single(a => a.Id == card.Id).BalanceMinor);
        Assert.Equal(50_000, result.Accounts.Single(a => a.Id == cash.Id).BalanceMinor);
        Assert.Equal(157_656, result.TotalMinor);
        Assert.Equal(3, result.Operations.Count);
    }

    [Fact]
    public async Task EditingTransferAndDeletingIt_RecalculatesBothSides()
    {
        var first = NewAccount("A", 10_000);
        var second = NewAccount("B", 5_000);
        var store = Store();
        await store.SaveAccountAsync(first); await store.SaveAccountAsync(second);
        var transfer = Operation(first, OperationKind.Transfer, 3_000, second);
        await store.SaveOperationAsync(transfer);
        var edited = await store.SaveOperationAsync(transfer with { AccountId = second.Id, DestinationId = first.Id, AmountMinor = 2_000 });
        Assert.Single(edited.Operations);
        Assert.Equal(12_000, edited.Accounts.Single(a => a.Id == first.Id).BalanceMinor);
        Assert.Equal(3_000, edited.Accounts.Single(a => a.Id == second.Id).BalanceMinor);
        Assert.Equal(15_000, edited.TotalMinor);
        var deleted = await store.DeleteOperationAsync(transfer.Id);
        Assert.Empty(deleted.Operations);
        Assert.Equal(10_000, deleted.Accounts.Single(a => a.Id == first.Id).BalanceMinor);
        Assert.Equal(5_000, deleted.Accounts.Single(a => a.Id == second.Id).BalanceMinor);
    }

    [Fact]
    public async Task ChangingExpenseToIncomeAndAccount_RemovesOldEffect()
    {
        var a = NewAccount("A", 1_000); var b = NewAccount("B"); var store = Store();
        await store.SaveAccountAsync(a); await store.SaveAccountAsync(b);
        var operation = Operation(a, OperationKind.Expense, 250);
        await store.SaveOperationAsync(operation);
        var result = await store.SaveOperationAsync(operation with { Kind = OperationKind.Income, AccountId = b.Id, AmountMinor = 175 });
        Assert.Equal(1_000, result.Accounts.Single(x => x.Id == a.Id).BalanceMinor);
        Assert.Equal(175, result.Accounts.Single(x => x.Id == b.Id).BalanceMinor);
        Assert.Equal(1_175, result.TotalMinor);
    }

    [Fact]
    public async Task RepeatedSaveWithSameId_DoesNotDuplicateMoney()
    {
        var account = NewAccount("A"); var store = Store();
        await store.SaveAccountAsync(account);
        var operation = Operation(account, OperationKind.Income, 101);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.SaveOperationAsync(operation)));
        var result = await store.ReadFinanceAsync();
        Assert.Single(result.Operations);
        Assert.Equal(101, result.TotalMinor);
    }

    [Fact]
    public async Task FailedTransferCommit_RollsBackWholeOperation()
    {
        var a = NewAccount("A", 100); var b = NewAccount("B", 200); var store = Store();
        await store.SaveAccountAsync(a); await store.SaveAccountAsync(b);
        var transfer = Operation(a, OperationKind.Transfer, 50, b);
        var failingStore = new LocalStore(new FaultFactory(DatabasePath, "COMMIT"));
        await Assert.ThrowsAsync<StorageException>(() => failingStore.SaveOperationAsync(transfer));
        var after = await store.ReadFinanceAsync();
        Assert.Empty(after.Operations);
        Assert.Equal(100, after.Accounts.Single(x => x.Id == a.Id).BalanceMinor);
        Assert.Equal(200, after.Accounts.Single(x => x.Id == b.Id).BalanceMinor);
        var retried = await store.SaveOperationAsync(transfer);
        Assert.Single(retried.Operations);
        Assert.Equal(300, retried.TotalMinor);
    }

    [Fact]
    public async Task FailedEdit_DoesNotLosePreviouslySavedOperation()
    {
        var account = NewAccount("A"); var store = Store(); await store.SaveAccountAsync(account);
        var operation = Operation(account, OperationKind.Income, 101);
        await store.SaveOperationAsync(operation);
        await Assert.ThrowsAsync<StorageException>(() => new LocalStore(new FaultFactory(DatabasePath, "COMMIT"))
            .SaveOperationAsync(operation with { AmountMinor = 900 }));
        Assert.Equal(101, (await store.ReadFinanceAsync()).TotalMinor);
    }

    [Fact]
    public async Task InvalidTransfers_AreRejectedWithoutAnyMutation()
    {
        var account = NewAccount("A", 500); var store = Store(); await store.SaveAccountAsync(account);
        var transfer = Operation(account, OperationKind.Transfer, 100, account);
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveOperationAsync(transfer));
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveOperationAsync(transfer with { DestinationId = null }));
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveOperationAsync(transfer with { DestinationId = Guid.NewGuid().ToString() }));
        var result = await store.ReadFinanceAsync();
        Assert.Equal(500, result.TotalMinor); Assert.Empty(result.Operations);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(100000000000001L)]
    public async Task InvalidOperationAmounts_AreRejected(long amount)
    {
        var account = NewAccount("A"); var store = Store(); await store.SaveAccountAsync(account);
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveOperationAsync(Operation(account, OperationKind.Income, amount)));
        Assert.Empty((await store.ReadFinanceAsync()).Operations);
    }

    [Fact]
    public async Task FutureDateAndMissingSource_AreRejected()
    {
        var account = NewAccount("A"); var store = Store(); await store.SaveAccountAsync(account);
        var operation = Operation(account, OperationKind.Expense, 100);
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveOperationAsync(operation with { Date = DateOnly.FromDateTime(DateTime.Today).AddDays(1) }));
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveOperationAsync(operation with { AccountId = Guid.NewGuid().ToString() }));
    }

    [Fact]
    public async Task AccountRenameAndOpeningCorrection_PreserveOperations()
    {
        var account = NewAccount("A", 500); var store = Store(); await store.SaveAccountAsync(account);
        await store.SaveOperationAsync(Operation(account, OperationKind.Expense, 100));
        var result = await store.SaveAccountAsync(account with { Name = "Кошелёк", OpeningMinor = 700 });
        Assert.Equal(600, result.TotalMinor); Assert.Single(result.Operations);
        Assert.Equal("Кошелёк", result.Accounts.Single().Name);
    }

    [Fact]
    public async Task DeletingAccountReferencedByEitherSide_IsBlocked()
    {
        var a = NewAccount("A"); var b = NewAccount("B"); var store = Store();
        await store.SaveAccountAsync(a); await store.SaveAccountAsync(b);
        var transfer = Operation(a, OperationKind.Transfer, 10, b); await store.SaveOperationAsync(transfer);
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.DeleteAccountAsync(a.Id));
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.DeleteAccountAsync(b.Id));
        await store.DeleteOperationAsync(transfer.Id);
        Assert.Single((await store.DeleteAccountAsync(a.Id)).Accounts);
    }

    [Fact]
    public async Task NegativeBalancesAreAllowed_AndOpeningIsNotAnOperation()
    {
        var account = NewAccount("Долг", -100); var store = Store();
        var initial = await store.SaveAccountAsync(account);
        Assert.Empty(initial.Operations); Assert.Equal(-100, initial.TotalMinor);
        Assert.Equal(-200, (await store.SaveOperationAsync(Operation(account, OperationKind.Expense, 100))).TotalMinor);
    }

    [Fact]
    public async Task DuplicateAccountNames_AreRejected_CyrillicCaseInsensitive()
    {
        var store = Store(); await store.SaveAccountAsync(NewAccount("Карта"));
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveAccountAsync(NewAccount("  карта  ")));
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveAccountAsync(NewAccount(" ")));
        Assert.Single((await store.ReadFinanceAsync()).Accounts);
    }

    [Fact]
    public async Task UserText_IsParameterizedAndRoundTrips()
    {
        var account = NewAccount("Тест '); DROP TABLE accounts; --"); var store = Store();
        await store.SaveAccountAsync(account);
        var operation = Operation(account, OperationKind.Income, 1) with { Note = "Тест ' ? ; 👋", Category = "Еда '" };
        await store.SaveOperationAsync(operation);
        var result = await Store().ReadFinanceAsync();
        Assert.Equal(account.Name, result.Accounts.Single().Name);
        Assert.Equal(operation.Note, result.Operations.Single().Note);
        Assert.Equal(operation.Category, result.Operations.Single().Category);
    }

    [Fact]
    public async Task History_IsOrderedByDateAndInsertion()
    {
        var account = NewAccount("A"); var store = Store(); await store.SaveAccountAsync(account);
        var today = Operation(account, OperationKind.Income, 1);
        var yesterday = Operation(account, OperationKind.Income, 2) with { Date = today.Date.AddDays(-1) };
        await store.SaveOperationAsync(today); await store.SaveOperationAsync(yesterday);
        Assert.Equal(today.Id, (await store.ReadFinanceAsync()).Operations[0].Id);
    }

    private sealed class FaultFactory(string path, string failSql) : ISqliteConnectionFactory
    {
        public ISqliteConnection Open() => new FaultConnection(new NativeSqliteConnection(path), failSql);
    }
    private sealed class FaultConnection(ISqliteConnection inner, string failSql) : ISqliteConnection
    {
        public void Execute(string sql, params string[] arguments)
        {
            if (sql == failSql) throw new IOException("Synthetic failure");
            inner.Execute(sql, arguments);
        }
        public string? Scalar(string sql, params string[] arguments) => inner.Scalar(sql, arguments);
        public IReadOnlyList<string?[]> Query(string sql, params string[] arguments) => inner.Query(sql, arguments);
        public void Dispose() => inner.Dispose();
    }
}
