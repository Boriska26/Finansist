using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Shopping;

namespace Finansist.Core.ViewModels;

public sealed class ProductResultGroup(string title, IEnumerable<ProductRow> rows) : ObservableCollection<ProductRow>(rows)
{
    public string Title { get; } = title;
}
public partial class CatalogBrowserViewModel : ObservableObject
{
    public const int PageSize = 20;
    private ProductSearchIndex index = new([]);
    private ProductSearchResult result = new([], []);
    private int visible;
    private ProductResultGroup? matchesGroup;
    public ObservableCollection<ProductResultGroup> Groups { get; } = [];
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool hasMore;
    public void SetProducts(IEnumerable<Product> products) { index = new(products); ApplySearch(); }
    partial void OnSearchChanged(string value) => ApplySearch();
    private void ApplySearch() { result = index.Find(Search); visible = Math.Min(PageSize, result.Matches.Count); UpdateRows(true); }
    [RelayCommand] private void LoadMore() { visible = Math.Min(visible + PageSize, result.Matches.Count); UpdateRows(false); }
    private void UpdateRows(bool reset)
    {
        if (reset)
        {
            Groups.Clear(); matchesGroup = null;
            if (visible > 0)
            {
                matchesGroup = new(string.IsNullOrWhiteSpace(Search) ? "Каталог" : "Совпадения", result.Matches.Take(visible).Select(p => new ProductRow(p)));
                Groups.Add(matchesGroup);
            }
            if (result.Similar.Count > 0) Groups.Add(new("Похожие по названию", result.Similar.Select(p => new ProductRow(p))));
        }
        else if (matchesGroup is not null)
            foreach (var product in result.Matches.Skip(matchesGroup.Count).Take(visible - matchesGroup.Count)) matchesGroup.Add(new(product));
        HasMore = visible < result.Matches.Count;
        Status = string.IsNullOrWhiteSpace(Search) ? $"Показано {visible} из {result.Matches.Count}" : $"Совпадений: {result.Matches.Count} · показано {visible} · похожих: {result.Similar.Count}";
    }
}

public partial class ProductPickerViewModel(IShoppingStore store, IShoppingNavigation navigation) : FinanceScreenViewModel
{
    private Action<Product?>? selected;
    public CatalogBrowserViewModel Catalog { get; } = new();
    public void Configure(Action<Product?> onSelected) => selected = onSelected;
    [RelayCommand] private Task RefreshAsync() => RunAsync(async () => Catalog.SetProducts((await store.ReadShoppingAsync()).Document.Products));
    [RelayCommand] private Task ChooseAsync(ProductRow row) => RunAsync(async () => { selected?.Invoke(row.Product); await navigation.BackAsync(); });
    [RelayCommand] private Task NewProductAsync() => RunAsync(async () => { selected?.Invoke(null); await navigation.BackAsync(); });
}
