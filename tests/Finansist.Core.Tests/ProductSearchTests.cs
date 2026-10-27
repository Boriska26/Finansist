using System.Text.Json;
using Finansist.Core.Shopping;
using Finansist.Core.Storage;
using Finansist.Core.ViewModels;

namespace Finansist.Core.Tests;

public sealed class ProductSearchTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "finansist-search-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(directory, "synthetic.db");
    private LocalStore Store() => new(new TestConnectionFactory(DatabasePath));
    private static Product Product(string name) => new(Guid.NewGuid().ToString(), name, ProductUnit.Piece, null, null);
    public ProductSearchTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public void ThousandProductsStartAtTwentyAndPagingKeepsPreviouslyVisibleRows()
    {
        var products = Enumerable.Range(0, 1000).Select(i => Product($"Тестовый товар {i:D4}")).ToArray();
        var vm = new CatalogBrowserViewModel(); vm.SetProducts(products);
        var group = Assert.Single(vm.Groups); var first = group[0];
        Assert.Equal(20, group.Count); Assert.True(vm.HasMore); Assert.Contains("20 из 1000", vm.Status);
        vm.LoadMoreCommand.Execute(null);
        Assert.Same(group, Assert.Single(vm.Groups)); Assert.Same(first, group[0]); Assert.Equal(40, group.Count);
        for (var i = 0; i < 48; i++) vm.LoadMoreCommand.Execute(null);
        Assert.Equal(1000, group.Count); Assert.Equal(1000, group.Select(r => r.Product.Id).Distinct().Count()); Assert.False(vm.HasMore);
        vm.Search = "0999";
        Assert.Single(Assert.Single(vm.Groups)); Assert.Equal(products[999].Id, vm.Groups[0][0].Product.Id);
        vm.Search = ""; Assert.Equal(20, Assert.Single(vm.Groups).Count); Assert.True(vm.HasMore);
    }

    [Fact]
    public void DirectResultsAreRankedExactThenPrefixThenOtherMatches()
    {
        var exact = Product("Молоко"); var prefix = Product("Молоко тестовое"); var contains = Product("Тестовое молоко");
        var result = new ProductSearchIndex([contains, prefix, exact, Product("Тестовый хлеб")]).Find("  МОЛОКО  ");
        Assert.Equal(new[] { exact.Id, prefix.Id, contains.Id }, result.Matches.Select(p => p.Id));
        Assert.Empty(result.Similar);
    }

    [Fact]
    public void YoPunctuationDecimalSeparatorAndWordOrderAreNormalized()
    {
        var product = Product("Мёд, тестовый 0,5 л"); var index = new ProductSearchIndex([product]);
        Assert.Equal(product.Id, Assert.Single(index.Find("МЕД").Matches).Id);
        Assert.Equal(product.Id, Assert.Single(index.Find("тестовый мед").Matches).Id);
        Assert.Equal(product.Id, Assert.Single(index.Find("мед 0.5").Matches).Id);
    }

    [Fact]
    public void TypoSuggestionsAreSeparateBoundedAndNeverPadWithUnrelatedProducts()
    {
        var milk = Product("Молоко тестовое"); var typoResult = new ProductSearchIndex([milk, Product("Тестовый картофель"), Product("Тестовые яблоки")]).Find("молко");
        Assert.Empty(typoResult.Matches); Assert.Equal(milk.Id, Assert.Single(typoResult.Similar).Id);
        var unrelated = new ProductSearchIndex([milk]).Find("фантазия"); Assert.Empty(unrelated.Matches); Assert.Empty(unrelated.Similar);
        Assert.Empty(new ProductSearchIndex([milk]).Find("м").Similar);
    }

    [Fact]
    public void MoreThanTenSimilarCandidatesStayCappedAndDoNotRepeatDirectMatches()
    {
        var exact = Product("Молоко"); var products = Enumerable.Range(0, 30).Select(i => Product($"Молока тестовое {i:D2}")).Append(exact).ToArray();
        var result = new ProductSearchIndex(products).Find("молоко");
        Assert.Equal(exact.Id, Assert.Single(result.Matches).Id); Assert.Equal(10, result.Similar.Count);
        Assert.DoesNotContain(result.Similar, p => p.Id == exact.Id);
        var vm = new CatalogBrowserViewModel(); vm.SetProducts(products); vm.Search = "молоко";
        Assert.Equal(2, vm.Groups.Count); Assert.Equal("Совпадения", vm.Groups[0].Title); Assert.Equal("Похожие по названию", vm.Groups[1].Title);
        Assert.Single(vm.Groups[0]); Assert.Equal(10, vm.Groups[1].Count);
    }

    [Fact]
    public void SuggestionsNeverIncludeDirectResultsHiddenOnLaterPages()
    {
        var direct = Enumerable.Range(0, 75).Select(i => Product($"Молоко тестовое {i:D2}")).ToArray();
        var similar = Product("Молока тестовое"); var vm = new CatalogBrowserViewModel(); vm.SetProducts([..direct, similar]); vm.Search = "молоко";
        Assert.Equal(20, vm.Groups[0].Count); Assert.Equal(similar.Id, Assert.Single(vm.Groups[1]).Product.Id);
        vm.LoadMoreCommand.Execute(null);
        Assert.Equal(40, vm.Groups[0].Count); Assert.Single(vm.Groups[1]);
        Assert.Equal(41, vm.Groups.SelectMany(g => g).Select(p => p.Product.Id).Distinct().Count());
        vm.Search = "несуществующий"; Assert.Empty(vm.Groups); Assert.False(vm.HasMore);
    }

    [Fact]
    public void RefreshedCatalogKeepsQueryButResetsPageAndRemovesDeletedResults()
    {
        var products = Enumerable.Range(0, 50).Select(i => Product($"Тестовое молоко {i}")).ToArray();
        var vm = new CatalogBrowserViewModel(); vm.SetProducts(products); vm.Search = "молоко"; vm.LoadMoreCommand.Execute(null);
        Assert.Equal(40, vm.Groups[0].Count);
        vm.SetProducts(products.Skip(25));
        Assert.Equal("молоко", vm.Search); Assert.Equal(20, vm.Groups[0].Count);
        Assert.DoesNotContain(vm.Groups.SelectMany(g => g), row => products.Take(25).Any(p => p.Id == row.Product.Id));
    }

    [Fact]
    public async Task CatalogAndProductPickerSearchAllThousandStoredProductsWithoutWriting()
    {
        var store = Store(); await store.ReadShoppingAsync();
        var products = Enumerable.Range(0, 1000).Select(i => Product($"Тестовый товар {i:D4}")).ToList();
        using (var db = new NativeSqliteConnection(DatabasePath))
            db.Execute("INSERT INTO shopping_state (id, revision, payload) VALUES (1, 1, ?)", JsonSerializer.Serialize(new ShoppingDocument { Products = products }));
        var before = JsonSerializer.Serialize(await store.ReadShoppingAsync()); var nav = new Navigation();
        var main = new ShoppingViewModel(store, nav); await main.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(20, Assert.Single(main.CatalogBrowser.Groups).Count);
        Product? selected = null; var picker = new ProductPickerViewModel(store, nav); picker.Configure(p => selected = p);
        await picker.RefreshCommand.ExecuteAsync(null); picker.Catalog.Search = "0999";
        var row = Assert.Single(Assert.Single(picker.Catalog.Groups));
        await picker.ChooseCommand.ExecuteAsync(row); Assert.Equal(products[999].Id, selected?.Id); Assert.Equal(1, nav.Backs);
        await picker.NewProductCommand.ExecuteAsync(null); Assert.Null(selected);
        Assert.Equal(before, JsonSerializer.Serialize(await store.ReadShoppingAsync()));
    }

    [Fact]
    public async Task ChoosingProductThroughPickerRetainsCatalogDefaultsAndDraftAfterReturn()
    {
        var store = Store(); var listId = Guid.NewGuid().ToString();
        var s = await store.SaveListAsync(listId, "Синтетический список", 0);
        var product = Product("Тестовое филе") with { Unit = ProductUnit.Kilogram, PriceMinor = 27000, PriceQuantity = .45m, Purpose = ShoppingPurpose.Joy };
        await store.SaveProductAsync(product, s.Revision);
        var nav = new Navigation(); var vm = new ShoppingItemEditorViewModel(store, nav);
        vm.Configure(listId, null); await vm.LoadCommand.ExecuteAsync(null);
        await vm.PickProductCommand.ExecuteAsync(null); Assert.NotNull(nav.PickerCallback);
        nav.PickerCallback!(product);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(product.Name, vm.ProductName); Assert.Equal("0,45", vm.Quantity); Assert.Equal(1, vm.PurposeIndex);
        Assert.Contains("270,00", vm.PreviewText);
        await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError);
        Assert.Equal(product.Id, (await store.ReadShoppingAsync()).Document.Lists[0].Items[0].ProductId);
    }

    private sealed class Navigation : IShoppingNavigation
    {
        public int Backs { get; private set; }
        public Action<Product?>? PickerCallback { get; private set; }
        public Task PickProductAsync(Action<Product?> selected) { PickerCallback = selected; return Task.CompletedTask; }
        public Task BackAsync() { Backs++; return Task.CompletedTask; }
        public Task<string?> AskListNameAsync(string? current) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task OpenListAsync(string id) => Task.CompletedTask;
        public Task EditProductAsync(string? id) => Task.CompletedTask;
        public Task EditItemAsync(string listId, string? itemId) => Task.CompletedTask;
    }
}
