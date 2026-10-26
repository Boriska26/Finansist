using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Budgeting;
using Finansist.Core.Finance;
using Finansist.Core.Shopping;

namespace Finansist.Core.ViewModels;

public interface IShoppingNavigation
{
    Task<string?> AskListNameAsync(string? current);
    Task<bool> ConfirmAsync(string title, string message);
    Task OpenListAsync(string id);
    Task EditProductAsync(string? id);
    Task EditItemAsync(string listId, string? itemId);
    Task BackAsync();
}
public sealed record ProductRow(Product Product)
{
    public string Name => Product.Name;
    public string Details => $"{ShoppingCalculator.PurposeText(Product.Purpose)} · {ShoppingCalculator.UnitText(Product.Unit)}\n" +
        (Product.PriceMinor is { } price ? $"{Money.Format(price)} / {ShoppingCalculator.UnitText(Product.Unit)} · обновлена {Product.PriceDate:dd.MM.yyyy}" : "Цена неизвестна");
}
public sealed record ShoppingListRow(string Id, string Name, string Details);
public sealed record ShoppingItemRow(ShoppingItem Item, Product Product)
{
    public string Name => Product.Name;
    public string Details => $"{Item.Quantity.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"))} {ShoppingCalculator.UnitText(Product.Unit)} · {ShoppingCalculator.PurposeText(Item.Purpose)}\n" +
        (ShoppingCalculator.Cost(Product, Item.Quantity) is { } cost ? $"≈ {Money.Format(cost)} · обновлена {Product.PriceDate:dd.MM.yyyy}" : "Цена неизвестна");
    public string Status => Item.State switch { ShoppingItemState.Bought => "Куплено", ShoppingItemState.AtHome => "Есть дома", _ => "Нужно купить" };
    public string ToggleText => Item.State == ShoppingItemState.Needed ? "Куплено" : "Вернуть в покупки";
}
public partial class ShoppingViewModel(IShoppingStore store, IShoppingNavigation navigation) : FinanceScreenViewModel
{
    private ShoppingSnapshot snapshot = new(0, new());
    public ObservableCollection<ShoppingListRow> Lists { get; } = [];
    public ObservableCollection<ProductRow> Products { get; } = [];
    [ObservableProperty] private bool showCatalog;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private bool noLists = true;
    [ObservableProperty] private bool noProducts = true;
    public bool ShowLists => !ShowCatalog;
    partial void OnShowCatalogChanged(bool value) => OnPropertyChanged(nameof(ShowLists));
    partial void OnSearchChanged(string value) => FilterProducts();
    [RelayCommand] private void Catalog() => ShowCatalog = true;
    [RelayCommand] private void ListTab() => ShowCatalog = false;
    [RelayCommand] private Task RefreshAsync() => RunAsync(LoadAsync);
    private async Task LoadAsync()
    {
        snapshot = await store.ReadShoppingAsync();
        Lists.Clear();
        foreach (var list in snapshot.Document.Lists.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var estimate = ShoppingCalculator.Estimate(snapshot.Document, list);
            Lists.Add(new(list.Id, list.Name, $"Позиций: {list.Items.Count} · осталось купить ≈ {Money.Format(estimate.TotalMinor)}" +
                (estimate.UnknownCount > 0 ? $"\nБез цены: {estimate.UnknownCount}" : "")));
        }
        NoLists = Lists.Count == 0; FilterProducts();
    }
    private void FilterProducts()
    {
        Products.Clear();
        foreach (var product in snapshot.Document.Products.Where(p => p.Name.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) Products.Add(new(product));
        NoProducts = Products.Count == 0;
    }
    [RelayCommand] private Task AddListAsync() => RunAsync(async () =>
    {
        var name = await navigation.AskListNameAsync(null); if (name is null) return;
        var id = Guid.NewGuid().ToString(); await store.SaveListAsync(id, name, snapshot.Revision); await LoadAsync(); await navigation.OpenListAsync(id);
    });
    [RelayCommand] private Task OpenListAsync(ShoppingListRow row) => RunAsync(() => navigation.OpenListAsync(row.Id));
    [RelayCommand] private Task AddProductAsync() => RunAsync(() => navigation.EditProductAsync(null));
    [RelayCommand] private Task EditProductAsync(ProductRow row) => RunAsync(() => navigation.EditProductAsync(row.Product.Id));
}

public partial class ShoppingListViewModel(IShoppingStore store, IBudgetStore budget, IShoppingNavigation navigation) : FinanceScreenViewModel
{
    private string listId = "";
    private ShoppingSnapshot snapshot = new(0, new());
    public ObservableCollection<ShoppingItemRow> Items { get; } = [];
    [ObservableProperty] private string name = "Список покупок";
    [ObservableProperty] private string totalText = "";
    [ObservableProperty] private string necessaryText = "";
    [ObservableProperty] private string joyText = "";
    [ObservableProperty] private string forecastText = "";
    [ObservableProperty] private bool isEmpty = true;
    public void Configure(string id) => listId = id;
    [RelayCommand] private Task RefreshAsync() => RunAsync(LoadAsync);
    private async Task LoadAsync()
    {
        snapshot = await store.ReadShoppingAsync();
        var list = snapshot.Document.Lists.SingleOrDefault(l => l.Id == listId) ?? throw new FinanceValidationException("Список удалён. Вернитесь к покупкам.");
        Name = list.Name; Items.Clear();
        var products = snapshot.Document.Products.ToDictionary(p => p.Id);
        foreach (var item in list.Items.OrderBy(i => i.State)) Items.Add(new(item, products[item.ProductId]));
        IsEmpty = Items.Count == 0;
        var estimate = ShoppingCalculator.Estimate(snapshot.Document, list);
        TotalText = $"Осталось купить ≈ {Money.Format(estimate.TotalMinor)}" + (estimate.UnknownCount > 0 ? $"\nЕщё без цены: {estimate.UnknownCount}. Итог неполный." : "");
        NecessaryText = $"Необходимое: ≈ {Money.Format(estimate.NecessaryMinor)}" + (estimate.UnknownNecessary > 0 ? $" + {estimate.UnknownNecessary} без цены" : "");
        JoyText = $"Для себя: ≈ {Money.Format(estimate.JoyMinor)}" + (estimate.UnknownJoy > 0 ? $" + {estimate.UnknownJoy} без цены" : "");
        var preview = ShoppingCalculator.Preview(estimate, await budget.ReadBudgetAsync());
        if (!preview.HasBudget) ForecastText = "Бюджет ещё не распределён. Распределите зарплату на вкладке «Бюджет», чтобы сравнить покупки с доступными суммами.";
        else
        {
            static string After(long value) => value >= 0 ? $"останется ≈ {Money.Format(value)}" : $"превышение ≈ {Money.Format(checked(-value))}";
            ForecastText = $"После предполагаемой покупки по известным ценам:\nНеобходимое — доступно {Money.Format(preview.NecessaryAvailable)}, {After(preview.NecessaryAfter)}.\nДля себя — доступно {Money.Format(preview.JoyAvailable)}, {After(preview.JoyAfter)}.\nНа обязательные платежи защищено {Money.Format(preview.ReservedBills)}: в доступное для покупок не включено.";
        }
    }
    [RelayCommand] private Task AddItemAsync() => RunAsync(() => navigation.EditItemAsync(listId, null));
    [RelayCommand] private Task EditItemAsync(ShoppingItemRow row) => RunAsync(() => navigation.EditItemAsync(listId, row.Item.Id));
    [RelayCommand] private Task EditPriceAsync(ShoppingItemRow row) => RunAsync(() => navigation.EditProductAsync(row.Product.Id));
    [RelayCommand] private Task ToggleItemAsync(ShoppingItemRow row) => RunAsync(async () =>
    {
        await store.SaveShoppingItemAsync(listId, row.Item with { State = row.Item.State == ShoppingItemState.Needed ? ShoppingItemState.Bought : ShoppingItemState.Needed }, null, snapshot.Revision);
        await LoadAsync();
    });
    [RelayCommand] private Task RenameAsync() => RunAsync(async () =>
    {
        var value = await navigation.AskListNameAsync(Name); if (value is null) return;
        await store.SaveListAsync(listId, value, snapshot.Revision); await LoadAsync();
    });
    [RelayCommand] private Task DeleteAsync() => RunAsync(async () =>
    {
        if (!await navigation.ConfirmAsync("Удалить список?", "Его позиции будут удалены. Каталог товаров и финансы сохранятся.")) return;
        await store.DeleteListAsync(listId, snapshot.Revision); await navigation.BackAsync();
    });
}

public partial class ProductEditorViewModel(IShoppingStore store, IShoppingNavigation navigation) : FinanceScreenViewModel
{
    private string id = Guid.NewGuid().ToString();
    private long revision;
    private bool loaded;
    public IReadOnlyList<string> Units { get; } = ["Штуки / упаковки", "Килограммы", "Литры"];
    public IReadOnlyList<string> Purposes { get; } = ["Необходимое", "Для себя"];
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string price = "";
    [ObservableProperty] private int unitIndex;
    [ObservableProperty] private int purposeIndex;
    [ObservableProperty] private bool canDelete;
    [ObservableProperty] private string priceHint = "Цена пока неизвестна. Поле можно оставить пустым.";
    public void Configure(string? productId) { id = productId ?? Guid.NewGuid().ToString(); CanDelete = productId is not null; loaded = false; }
    [RelayCommand] private Task LoadAsync() => RunAsync(async () =>
    {
        if (loaded) return;
        var snapshot = await store.ReadShoppingAsync(); revision = snapshot.Revision;
        if (CanDelete)
        {
            var product = snapshot.Document.Products.SingleOrDefault(p => p.Id == id) ?? throw new FinanceValidationException("Товар уже удалён.");
            Name = product.Name; Price = product.PriceMinor is { } value ? Money.Input(value) : "";
            UnitIndex = (int)product.Unit; PurposeIndex = (int)product.Purpose;
            if (product.PriceDate is { } date) PriceHint = $"Цена обновлена {date:dd.MM.yyyy}. Изменение цены обновит дату во всех списках.";
        }
        loaded = true;
    });
    [RelayCommand] private Task SaveAsync() => RunAsync(async () =>
    {
        if (!loaded) throw new FinanceValidationException("Сначала загрузите форму.");
        await store.SaveProductAsync(new(id, Name, (ProductUnit)UnitIndex, ShoppingCalculator.ParsePrice(Price), null, (ShoppingPurpose)PurposeIndex), revision);
        await navigation.BackAsync();
    });
    [RelayCommand] private Task DeleteAsync() => RunAsync(async () =>
    {
        if (!CanDelete || !loaded || !await navigation.ConfirmAsync("Удалить товар?", "Удаление доступно, если товара нет в списках покупок.")) return;
        await store.DeleteProductAsync(id, revision); await navigation.BackAsync();
    });
}

public sealed record ProductChoice(Product? Product, string Name);
public partial class ShoppingItemEditorViewModel(IShoppingStore store, IShoppingNavigation navigation) : FinanceScreenViewModel
{
    private string listId = "", id = "", newProductId = "";
    private long revision;
    private bool loaded;
    public ObservableCollection<ProductChoice> Products { get; } = [];
    public IReadOnlyList<string> Units { get; } = ["Штуки / упаковки", "Килограммы", "Литры"];
    public IReadOnlyList<string> Purposes { get; } = ["Необходимое", "Для себя"];
    public IReadOnlyList<string> States { get; } = ["Нужно купить", "Куплено", "Есть дома"];
    [ObservableProperty] private ProductChoice? selectedProduct;
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string price = "";
    [ObservableProperty] private string quantity = "1";
    [ObservableProperty] private int unitIndex;
    [ObservableProperty] private int purposeIndex;
    [ObservableProperty] private int stateIndex;
    [ObservableProperty] private bool isEditing;
    public bool CanChooseProduct => !IsEditing;
    public bool IsNewProduct => SelectedProduct?.Product is null;
    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(CanChooseProduct));
    partial void OnSelectedProductChanged(ProductChoice? value)
    {
        PurposeIndex = (int)(value?.Product?.Purpose ?? ShoppingPurpose.Necessary);
        OnPropertyChanged(nameof(IsNewProduct));
    }
    public void Configure(string list, string? itemId)
    {
        listId = list; id = itemId ?? Guid.NewGuid().ToString(); newProductId = Guid.NewGuid().ToString(); IsEditing = itemId is not null; loaded = false;
    }
    [RelayCommand] private Task LoadAsync() => RunAsync(async () =>
    {
        if (loaded) return;
        var snapshot = await store.ReadShoppingAsync(); revision = snapshot.Revision;
        var list = snapshot.Document.Lists.SingleOrDefault(l => l.Id == listId) ?? throw new FinanceValidationException("Список уже удалён.");
        Products.Clear(); Products.Add(new(null, "+ Новый товар"));
        foreach (var product in snapshot.Document.Products.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) Products.Add(new(product, product.Name));
        if (IsEditing)
        {
            var item = list.Items.SingleOrDefault(i => i.Id == id) ?? throw new FinanceValidationException("Позиция уже удалена.");
            SelectedProduct = Products.Single(p => p.Product?.Id == item.ProductId);
            Quantity = item.Quantity.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")); PurposeIndex = (int)item.Purpose; StateIndex = (int)item.State;
        }
        else SelectedProduct = Products[0];
        loaded = true;
    });
    [RelayCommand] private Task SaveAsync() => RunAsync(async () =>
    {
        if (!loaded || SelectedProduct is null) throw new FinanceValidationException("Выберите товар.");
        var product = IsNewProduct ? new Product(newProductId, Name, (ProductUnit)UnitIndex, ShoppingCalculator.ParsePrice(Price), null, (ShoppingPurpose)PurposeIndex) : null;
        var item = new ShoppingItem(id, product?.Id ?? SelectedProduct.Product!.Id, ShoppingCalculator.ParseQuantity(Quantity), (ShoppingPurpose)PurposeIndex, (ShoppingItemState)StateIndex);
        await store.SaveShoppingItemAsync(listId, item, product, revision); await navigation.BackAsync();
    });
    [RelayCommand] private Task DeleteAsync() => RunAsync(async () =>
    {
        if (!loaded || !IsEditing || !await navigation.ConfirmAsync("Удалить позицию?", "Товар останется в каталоге.")) return;
        await store.DeleteShoppingItemAsync(listId, id, revision); await navigation.BackAsync();
    });
}
