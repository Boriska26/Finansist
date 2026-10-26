using System.Text.Json;
using Finansist.Core.Budgeting;
using Finansist.Core.Finance;
using Finansist.Core.Shopping;
using Finansist.Core.Storage;
using Finansist.Core.ViewModels;

namespace Finansist.Core.Tests;

public sealed class ShoppingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "finansist-shopping-tests", Guid.NewGuid().ToString("N"));
    private string PathDb => Path.Combine(directory, "synthetic.db");
    private LocalStore Store() => new(new TestConnectionFactory(PathDb));
    private static string Id() => Guid.NewGuid().ToString();
    private static Product Product(long? price = 10000, ShoppingPurpose purpose = ShoppingPurpose.Necessary) => new(Id(), "Синтетический товар", ProductUnit.Piece, price, price is null ? null : DateOnly.FromDateTime(DateTime.Today), purpose);
    public ShoppingTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public async Task CatalogAndSeveralListsSurviveReopenWithoutFinancialWrites()
    {
        var store = Store(); var before = await store.ReadBudgetAsync(); var a = Id(); var b = Id(); var product = Product();
        var snapshot = await store.SaveListAsync(a, "Тестовый список А", 0);
        snapshot = await store.SaveListAsync(b, "Тестовый список Б", snapshot.Revision);
        snapshot = await store.SaveShoppingItemAsync(a, new(Id(), product.Id, 2, ShoppingPurpose.Necessary), product, snapshot.Revision);
        snapshot = await store.SaveShoppingItemAsync(b, new(Id(), product.Id, 1, ShoppingPurpose.Joy), null, snapshot.Revision);
        var reopened = await Store().ReadShoppingAsync();
        Assert.Equal(2, reopened.Document.Lists.Count); Assert.Single(reopened.Document.Products);
        Assert.Equal(20_000, ShoppingCalculator.Estimate(reopened.Document, reopened.Document.Lists.Single(l => l.Id == a)).NecessaryMinor);
        Assert.Equal(10_000, ShoppingCalculator.Estimate(reopened.Document, reopened.Document.Lists.Single(l => l.Id == b)).JoyMinor);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await Store().ReadBudgetAsync()));
    }

    [Theory]
    [InlineData(ShoppingItemState.Bought)]
    [InlineData(ShoppingItemState.AtHome)]
    public async Task MarkingItemsRemovesFutureCostWithoutCreatingExpense(ShoppingItemState state)
    {
        var store = Store(); var listId = Id(); var product = Product(); var item = new ShoppingItem(Id(), product.Id, 1, ShoppingPurpose.Necessary);
        var s = await store.SaveListAsync(listId, "Синтетический список", 0);
        s = await store.SaveShoppingItemAsync(listId, item, product, s.Revision);
        s = await store.SaveShoppingItemAsync(listId, item with { State = state }, null, s.Revision);
        Assert.Equal(0, ShoppingCalculator.Estimate(s.Document, s.Document.Lists[0]).TotalMinor);
        Assert.Empty((await store.ReadFinanceAsync()).Operations);
        s = await store.SaveShoppingItemAsync(listId, item, null, s.Revision);
        Assert.Equal(10_000, ShoppingCalculator.Estimate(s.Document, s.Document.Lists[0]).TotalMinor);
    }

    [Fact]
    public void UnknownPricesAreCountedByPurposeAndZeroIsKnown()
    {
        var unknown = Product(null); var free = Product(0) with { Name = "Тестовый бесплатный" }; var known = Product(100) with { Name = "Тестовый известный" };
        var list = new ShoppingList(Id(), "Синтетика", [new(Id(), unknown.Id, 1, ShoppingPurpose.Necessary), new(Id(), unknown.Id, 1, ShoppingPurpose.Joy), new(Id(), free.Id, 3, ShoppingPurpose.Necessary), new(Id(), known.Id, 2, ShoppingPurpose.Joy)]);
        var estimate = ShoppingCalculator.Estimate(new() { Products = [unknown, free, known] }, list);
        Assert.Equal(1, estimate.UnknownNecessary); Assert.Equal(1, estimate.UnknownJoy);
        Assert.Equal(200, estimate.TotalMinor); Assert.Equal(0, estimate.NecessaryMinor);
    }

    [Theory]
    [InlineData("0,125", 0.125)]
    [InlineData("1.5", 1.5)]
    [InlineData("100000", 100000)]
    public void FractionalQuantitiesParseExactly(string input, double expected) => Assert.Equal((decimal)expected, ShoppingCalculator.ParseQuantity(input));
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("100001")]
    [InlineData("1.0001")]
    [InlineData("abc")]
    public void InvalidQuantitiesAreRejected(string text) => Assert.Throws<FinanceValidationException>(() => ShoppingCalculator.ParseQuantity(text));
    [Fact]
    public void CostsRoundEachLineToKopecksAndDetectOverflow()
    {
        Assert.Equal(1, ShoppingCalculator.Cost(Product(1), .5m));
        Assert.Equal(10, ShoppingCalculator.Cost(Product(9999), .001m));
        Assert.Null(ShoppingCalculator.ParsePrice("")); Assert.Equal(0, ShoppingCalculator.ParsePrice("0"));
        Assert.Throws<FinanceValidationException>(() => ShoppingCalculator.ParsePrice("-1"));
        Assert.Throws<OverflowException>(() => ShoppingCalculator.Cost(Product(Money.MaximumMinor), 100_000));
    }

    [Fact]
    public async Task ItemPurposeOverridesCatalogWithoutChangingDefaultsForNextPurchase()
    {
        var store = Store(); var listId = Id(); var product = Product(100, ShoppingPurpose.Joy);
        var s = await store.SaveListAsync(listId, "Подарок", 0);
        s = await store.SaveShoppingItemAsync(listId, new(Id(), product.Id, 1, ShoppingPurpose.Necessary), product, s.Revision);
        Assert.Equal(ShoppingPurpose.Joy, s.Document.Products[0].Purpose);
        Assert.Equal(ShoppingPurpose.Necessary, s.Document.Lists[0].Items[0].Purpose);
        var vm = new ShoppingItemEditorViewModel(store, new Navigation()); vm.Configure(listId, null); await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedProduct = vm.Products.Single(p => p.Product?.Id == product.Id);
        Assert.Equal(1, vm.PurposeIndex);
        vm.SelectedProduct = vm.Products[0]; Assert.Equal(0, vm.PurposeIndex);
    }

    [Fact]
    public async Task PriceUpdateAffectsAllListsAndRenamePreservesPriceDate()
    {
        var store = Store(); var product = Product(); var list = Id();
        var s = await store.SaveListAsync(list, "Синтетика", 0);
        s = await store.SaveShoppingItemAsync(list, new(Id(), product.Id, 2, ShoppingPurpose.Necessary), product, s.Revision);
        var oldDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-30);
        var doc = s.Document with { Products = [product with { PriceDate = oldDate }] };
        using (var db = new NativeSqliteConnection(PathDb)) db.Execute("UPDATE shopping_state SET payload = ?", JsonSerializer.Serialize(doc));
        s = await store.SaveProductAsync(product with { Name = "Новое тестовое имя" }, s.Revision);
        Assert.Equal(oldDate, s.Document.Products[0].PriceDate);
        s = await store.SaveProductAsync(s.Document.Products[0] with { PriceMinor = 20_000 }, s.Revision);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), s.Document.Products[0].PriceDate);
        Assert.Equal(40_000, ShoppingCalculator.Estimate(s.Document, s.Document.Lists[0]).NecessaryMinor);
        s = await store.SaveProductAsync(s.Document.Products[0] with { PriceMinor = null }, s.Revision);
        Assert.Null(s.Document.Products[0].PriceDate); Assert.Equal(1, ShoppingCalculator.Estimate(s.Document, s.Document.Lists[0]).UnknownCount);
    }

    [Fact]
    public async Task StaleWritesAndInvalidNewProductLeaveDocumentUnchanged()
    {
        var store = Store(); var list = Id(); var s = await store.SaveListAsync(list, "Синтетика", 0);
        var product = Product();
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveShoppingItemAsync(list, new(Id(), product.Id, 0, ShoppingPurpose.Necessary), product, s.Revision));
        Assert.Equal(JsonSerializer.Serialize(s), JsonSerializer.Serialize(await store.ReadShoppingAsync()));
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveProductAsync(product, 0));
        Assert.Equal(s.Revision, (await store.ReadShoppingAsync()).Revision);
    }

    [Fact]
    public async Task DuplicateNamesAndChangingUsedUnitAreRejectedWithoutLosingItems()
    {
        var store = Store(); var product = Product(); var list = Id();
        var s = await store.SaveListAsync(list, "Синтетика", 0);
        s = await store.SaveShoppingItemAsync(list, new(Id(), product.Id, 1, ShoppingPurpose.Necessary), product, s.Revision);
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveProductAsync(product with { Id = Id(), Name = product.Name.ToUpperInvariant() }, s.Revision));
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveProductAsync(product with { Unit = ProductUnit.Kilogram }, s.Revision));
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.DeleteProductAsync(product.Id, s.Revision));
        Assert.Single((await store.ReadShoppingAsync()).Document.Lists[0].Items);
        s = await store.DeleteListAsync(list, s.Revision);
        Assert.Single(s.Document.Products);
        s = await store.DeleteProductAsync(product.Id, s.Revision); Assert.Empty(s.Document.Products);
    }

    private static BudgetState Budget()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var finance = Ledger.Calculate([new("card", "Тестовый счёт", 100_000), new("cushion", "Подушка", 900_000)], []);
        var bill = new BudgetBill("bill", "Тестовая аренда", today.AddDays(10), 20_000);
        var allocation = new IncomeAllocation("salary", "card", 100_000, today, today.AddDays(15), 100_000, 50_000, 20_000, 30_000, [new("bill", 20_000)]);
        return new(1, new() { CushionAccountId = "cushion", Bills = [bill], Allocations = [allocation] }, finance);
    }
    [Fact]
    public void ForecastProtectsMandatoryReservesAndDoesNotSpendCushionOrMutateBudget()
    {
        var budget = Budget(); var before = JsonSerializer.Serialize(budget);
        var forecast = ShoppingCalculator.Preview(new(5_000, 3_000, 0, 1), budget);
        Assert.True(forecast.HasBudget); Assert.Equal(20_000, forecast.ReservedBills);
        Assert.Equal(30_000, forecast.NecessaryAvailable); Assert.Equal(25_000, forecast.NecessaryAfter);
        Assert.Equal(30_000, forecast.JoyAvailable); Assert.Equal(27_000, forecast.JoyAfter);
        var excess = ShoppingCalculator.Preview(new(40_000, 50_000, 0, 0), budget);
        Assert.Equal(-10_000, excess.NecessaryAfter); Assert.Equal(-20_000, excess.JoyAfter);
        Assert.Equal(before, JsonSerializer.Serialize(budget));
        Assert.False(ShoppingCalculator.Preview(new(1, 1, 0, 0), budget with { Document = new() }).HasBudget);
    }

    [Fact]
    public async Task EditorsPreserveInvalidInputAndCreatingNewItemAlsoCreatesCatalogProduct()
    {
        var store = Store(); var list = Id(); await store.SaveListAsync(list, "Синтетика", 0);
        var nav = new Navigation(); var vm = new ShoppingItemEditorViewModel(store, nav);
        vm.Configure(list, null); await vm.LoadCommand.ExecuteAsync(null);
        vm.Name = "Тестовый помидор"; vm.Price = "120"; vm.UnitIndex = 1; vm.Quantity = "0";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.HasError); Assert.Equal("0", vm.Quantity); Assert.Equal(0, nav.Backs);
        Assert.Empty((await store.ReadShoppingAsync()).Document.Products);
        vm.Quantity = "0,5"; await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.HasError); Assert.Equal(1, nav.Backs);
        var saved = await store.ReadShoppingAsync(); Assert.Single(saved.Document.Products);
        Assert.Equal(6_000, ShoppingCalculator.Estimate(saved.Document, saved.Document.Lists[0]).TotalMinor);
    }

    [Fact]
    public async Task ListViewModelShowsPartialEstimateAndNoUnallocatedBudgetAsZeroAllowance()
    {
        var store = Store(); var list = Id(); var unknown = Product(null);
        var s = await store.SaveListAsync(list, "Синтетика", 0);
        await store.SaveShoppingItemAsync(list, new(Id(), unknown.Id, 1, ShoppingPurpose.Necessary), unknown, s.Revision);
        var vm = new ShoppingListViewModel(store, store, new Navigation()); vm.Configure(list);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.HasError); Assert.Contains("Итог неполный", vm.TotalText);
        Assert.Contains("ещё не распределён", vm.ForecastText);
        await vm.ToggleItemCommand.ExecuteAsync(vm.Items[0]);
        Assert.DoesNotContain("Итог неполный", vm.TotalText);
        Assert.Empty((await store.ReadFinanceAsync()).Operations);
    }

    [Fact]
    public async Task MigrationFromFivePreservesFinanceAndNotificationConfiguration()
    {
        var store = Store(); var account = new Account(Id(), "Тестовая карта", 42_000);
        await store.SaveAccountAsync(account);
        await store.ConfigureImportAsync(new("synthetic.bank", "Синтетический банк"), true);
        using (var db = new NativeSqliteConnection(PathDb)) { db.Execute("DROP TABLE shopping_state"); db.Execute("PRAGMA user_version = 5"); }
        var s = await Store().ReadShoppingAsync();
        Assert.Empty(s.Document.Products); Assert.Empty(s.Document.Lists);
        Assert.Equal(42_000, (await Store().ReadFinanceAsync()).TotalMinor);
        Assert.Equal("synthetic.bank", (await Store().ReadImportSettingsAsync()).PackageId);
        using var check = new NativeSqliteConnection(PathDb);
        Assert.Equal("6", check.Scalar("PRAGMA user_version")); Assert.Equal("ok", check.Scalar("PRAGMA integrity_check"));
    }
    [Fact]
    public async Task FailedShoppingMigrationRollsBackAndPreservesExistingMoney()
    {
        var account = new Account(Id(), "Синтетическая карта", 12_345);
        await Store().SaveAccountAsync(account);
        using (var db = new NativeSqliteConnection(PathDb)) { db.Execute("DROP TABLE shopping_state"); db.Execute("PRAGMA user_version = 5"); }
        var failing = new LocalStore(new MigrationFailureFactory(PathDb));
        await Assert.ThrowsAsync<StorageException>(() => failing.ReadShoppingAsync());
        using (var db = new NativeSqliteConnection(PathDb))
        {
            Assert.Equal("5", db.Scalar("PRAGMA user_version"));
            Assert.Equal("0", db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'shopping_state'"));
            Assert.Equal("12345", db.Scalar("SELECT opening_minor FROM accounts"));
        }
        Assert.Empty((await Store().ReadShoppingAsync()).Document.Products);
        Assert.Equal(12_345, (await Store().ReadFinanceAsync()).TotalMinor);
    }
    private sealed class MigrationFailureFactory(string path) : ISqliteConnectionFactory
    {
        public ISqliteConnection Open() => new MigrationFailureConnection(new NativeSqliteConnection(path));
    }
    private sealed class MigrationFailureConnection(ISqliteConnection inner) : ISqliteConnection
    {
        public void Execute(string sql, params string[] args)
        {
            if (sql == "PRAGMA user_version = 6") throw new IOException("Synthetic migration failure");
            inner.Execute(sql, args);
        }
        public string? Scalar(string sql, params string[] args) => inner.Scalar(sql, args);
        public IReadOnlyList<string?[]> Query(string sql, params string[] args) => inner.Query(sql, args);
        public void Dispose() => inner.Dispose();
    }
    private sealed class Navigation : IShoppingNavigation
    {
        public int Backs { get; private set; }
        public Task BackAsync() { Backs++; return Task.CompletedTask; }
        public Task<string?> AskListNameAsync(string? current) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task OpenListAsync(string id) => Task.CompletedTask;
        public Task EditProductAsync(string? id) => Task.CompletedTask;
        public Task EditItemAsync(string listId, string? itemId) => Task.CompletedTask;
    }
}
