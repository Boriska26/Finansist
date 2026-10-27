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
    Task PickProductAsync(Action<Product?> selected);
    Task BackAsync();
}
public sealed record ProductRow(Product Product)
{
    public string Name => Product.Name;
    public string Details => $"{ShoppingCalculator.PurposeText(Product.Purpose)} · {ShoppingCalculator.PriceBasis(Product)}" +
        (Product.PriceDate is { } date ? $"\nОбновлена {date:dd.MM.yyyy}" : "");
}
public sealed record ShoppingListRow(string Id, string Name, string Details);
public sealed record ShoppingItemRow(ShoppingItem Item, Product Product)
{
    public string Name => Product.Name;
    public string Details => $"{ShoppingCalculator.QuantityText(Item.Quantity)} {ShoppingCalculator.UnitText(Product.Unit)} · {ShoppingCalculator.PurposeText(Item.Purpose)}" +
        (Item.State == ShoppingItemState.AtHome ? " · Есть дома" : "");
    public string CostText => ShoppingCalculator.Cost(Product, Item.Quantity) is { } cost ? $"≈ {Money.Format(cost)}" : "Без цены";
    public bool IsInCart => Item.State == ShoppingItemState.Bought;
    public bool CanToggleCart => Item.State != ShoppingItemState.AtHome;
    public string CartHint => !CanToggleCart ? "Есть дома. Нажмите на строку, чтобы изменить состояние." : IsInCart ? "Убрать отметку «В корзине»" : "Отметить «В корзине»";
}
public partial class ShoppingViewModel(IShoppingStore store, IShoppingNavigation navigation) : FinanceScreenViewModel
{
    private ShoppingSnapshot snapshot = new(0, new());
    public ObservableCollection<ShoppingListRow> Lists { get; } = [];
    public CatalogBrowserViewModel CatalogBrowser { get; } = new();
    [ObservableProperty] private bool showCatalog;
    [ObservableProperty] private bool noLists = true;
    public bool ShowLists => !ShowCatalog;
    partial void OnShowCatalogChanged(bool value) => OnPropertyChanged(nameof(ShowLists));
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
            Lists.Add(new(list.Id, list.Name, $"Позиций: {list.Items.Count} · стоимость ≈ {Money.Format(estimate.TotalMinor)}" +
                (estimate.UnknownCount > 0 ? $"\nБез цены: {estimate.UnknownCount}" : "")));
        }
        NoLists = Lists.Count == 0; CatalogBrowser.SetProducts(snapshot.Document.Products);
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
        Name = list.Name;
        var products = snapshot.Document.Products.ToDictionary(p => p.Id);
        var rows = list.Items.Select(item => new ShoppingItemRow(item, products[item.ProductId])).ToArray();
        for (var i = 0; i < rows.Length; i++)
        {
            var oldIndex = Items.ToList().FindIndex(row => row.Item.Id == rows[i].Item.Id);
            if (oldIndex < 0) Items.Insert(i, rows[i]);
            else
            {
                if (oldIndex != i) Items.Move(oldIndex, i);
                if (Items[i] != rows[i]) Items[i] = rows[i];
            }
        }
        while (Items.Count > rows.Length) Items.RemoveAt(Items.Count - 1);
        IsEmpty = Items.Count == 0;
        var estimate = ShoppingCalculator.Estimate(snapshot.Document, list);
        TotalText = $"Стоимость списка ≈ {Money.Format(estimate.TotalMinor)}" + (estimate.UnknownCount > 0 ? $"\nЕщё без цены: {estimate.UnknownCount}. Итог неполный." : "");
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
    [RelayCommand] private Task ToggleItemAsync(ShoppingItemRow row) => RunAsync(async () =>
    {
        if (!row.CanToggleCart) return;
        await store.SaveShoppingItemAsync(listId, row.Item with { State = row.IsInCart ? ShoppingItemState.Needed : ShoppingItemState.Bought }, null, snapshot.Revision);
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
    public IReadOnlyList<string> Units { get; } = ["шт.", "кг", "л"];
    public IReadOnlyList<string> Purposes { get; } = ["Необходимое", "Для себя"];
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string price = "";
    [ObservableProperty] private string priceQuantity = "1";
    public string PriceBasisPreview
    {
        get
        {
            try { return ShoppingCalculator.PriceBasis(new("", Name, (ProductUnit)UnitIndex, ShoppingCalculator.ParsePrice(Price), null, PriceQuantity: ShoppingCalculator.ParseQuantity(PriceQuantity))); }
            catch (FinanceValidationException e) { return e.Message; }
        }
    }
    partial void OnPriceChanged(string value) => OnPropertyChanged(nameof(PriceBasisPreview));
    partial void OnPriceQuantityChanged(string value) => OnPropertyChanged(nameof(PriceBasisPreview));
    partial void OnUnitIndexChanged(int value) => OnPropertyChanged(nameof(PriceBasisPreview));
    [ObservableProperty] private int unitIndex;
    [ObservableProperty] private int purposeIndex;
    [ObservableProperty] private bool canDelete;
    [ObservableProperty] private string priceHint = "Цена приблизительная. Поле можно оставить пустым.";
    public void Configure(string? productId) { id = productId ?? Guid.NewGuid().ToString(); CanDelete = productId is not null; loaded = false; }
    [RelayCommand] private Task LoadAsync() => RunAsync(async () =>
    {
        if (loaded) return;
        var snapshot = await store.ReadShoppingAsync(); revision = snapshot.Revision;
        if (CanDelete)
        {
            var product = snapshot.Document.Products.SingleOrDefault(p => p.Id == id) ?? throw new FinanceValidationException("Товар уже удалён.");
            Name = product.Name; Price = product.PriceMinor is { } value ? Money.Input(value) : "";
            PriceQuantity = ShoppingCalculator.QuantityText(product.PriceQuantity);
            UnitIndex = (int)product.Unit; PurposeIndex = (int)product.Purpose;
            if (product.PriceDate is { } date) PriceHint = $"Цена обновлена {date:dd.MM.yyyy}. Изменение цены или количества для неё обновит дату во всех списках.";
        }
        loaded = true;
    });
    [RelayCommand] private Task SaveAsync() => RunAsync(async () =>
    {
        if (!loaded) throw new FinanceValidationException("Сначала загрузите форму.");
        await store.SaveProductAsync(new(id, Name, (ProductUnit)UnitIndex, ShoppingCalculator.ParsePrice(Price), null, (ShoppingPurpose)PurposeIndex, ShoppingCalculator.ParseQuantity(PriceQuantity)), revision);
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
    private ShoppingItem? originalItem;
    public ObservableCollection<ProductChoice> Products { get; } = [];
    public IReadOnlyList<string> Units { get; } = ["шт.", "кг", "л"];
    public IReadOnlyList<string> Purposes { get; } = ["Необходимое", "Для себя"];
    public IReadOnlyList<string> States { get; } = ["Нужно взять", "В корзине", "Есть дома"];
    [ObservableProperty] private ProductChoice? selectedProduct;
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string price = "";
    [ObservableProperty] private string priceQuantity = "1";
    [ObservableProperty] private string quantity = "1";
    [ObservableProperty] private int unitIndex;
    [ObservableProperty] private int purposeIndex;
    [ObservableProperty] private int stateIndex;
    [ObservableProperty] private bool isEditing;
    public bool CanChooseProduct => !IsEditing;
    public bool IsNewProduct => SelectedProduct?.Product is null;
    public bool CanEditPrice => !IsNewProduct;
    public string QuantityLabel => $"Количество ({ShoppingCalculator.UnitText(SelectedProduct?.Product?.Unit ?? (ProductUnit)UnitIndex)})";
    public string ProductPriceText => SelectedProduct?.Product is { } p ? ShoppingCalculator.PriceBasis(p) + (p.PriceDate is { } date ? $" · обновлена {date:dd.MM.yyyy}" : "") : "";
    public string PreviewText
    {
        get
        {
            try
            {
                var product = SelectedProduct?.Product ?? new Product(newProductId, Name, (ProductUnit)UnitIndex,
                    ShoppingCalculator.ParsePrice(Price), null, PriceQuantity: ShoppingCalculator.ParseQuantity(PriceQuantity));
                var cost = ShoppingCalculator.Cost(product, ShoppingCalculator.ParseQuantity(Quantity));
                return cost is { } value ? $"Примерная стоимость: {Money.Format(value)}" : "Цена неизвестна — стоимость пока не посчитана.";
            }
            catch (FinanceValidationException e) { return e.Message; }
            catch (OverflowException) { return "Сумма слишком велика. Проверьте цену и количество."; }
        }
    }
    private void NotifyPreview()
    {
        OnPropertyChanged(nameof(PreviewText)); OnPropertyChanged(nameof(QuantityLabel)); OnPropertyChanged(nameof(ProductPriceText));
    }
    partial void OnPriceChanged(string value) => NotifyPreview();
    partial void OnPriceQuantityChanged(string value) => NotifyPreview();
    partial void OnQuantityChanged(string value) => NotifyPreview();
    partial void OnUnitIndexChanged(int value) => NotifyPreview();
    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(CanChooseProduct));
    partial void OnSelectedProductChanged(ProductChoice? value)
    {
        PurposeIndex = (int)(value?.Product?.Purpose ?? ShoppingPurpose.Necessary);
        if (!IsEditing) Quantity = ShoppingCalculator.QuantityText(value?.Product?.PriceQuantity ?? 1m);
        OnPropertyChanged(nameof(IsNewProduct)); OnPropertyChanged(nameof(CanEditPrice)); OnPropertyChanged(nameof(ProductName)); NotifyPreview();
    }
    public void Configure(string list, string? itemId)
    {
        listId = list; id = itemId ?? Guid.NewGuid().ToString(); newProductId = Guid.NewGuid().ToString(); IsEditing = itemId is not null; loaded = false;
    }
    [RelayCommand] private Task LoadAsync() => RunAsync(async () =>
    {
        var snapshot = await store.ReadShoppingAsync();
        var list = snapshot.Document.Lists.SingleOrDefault(l => l.Id == listId) ?? throw new FinanceValidationException("Список уже удалён.");
        var item = IsEditing ? list.Items.SingleOrDefault(i => i.Id == id) ?? throw new FinanceValidationException("Позиция уже удалена.") : null;
        if (loaded && item != originalItem) throw new FinanceValidationException("Позиция изменилась. Откройте форму заново. Введённые значения пока сохранены на экране.");
        var selectedId = loaded ? SelectedProduct?.Product?.Id : item?.ProductId;
        if (selectedId is not null && !snapshot.Document.Products.Any(p => p.Id == selectedId))
            throw new FinanceValidationException("Товар уже удалён. Вернитесь к списку и выберите другой.");
        var savedPurpose = PurposeIndex; var savedQuantity = Quantity; var savedState = StateIndex;
        Products.Clear(); Products.Add(new(null, "+ Новый товар"));
        foreach (var product in snapshot.Document.Products.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) Products.Add(new(product, product.Name));
        SelectedProduct = selectedId is null ? Products[0] : Products.Single(p => p.Product?.Id == selectedId);
        if (loaded) { PurposeIndex = savedPurpose; Quantity = savedQuantity; StateIndex = savedState; }
        else if (item is not null) { Quantity = ShoppingCalculator.QuantityText(item.Quantity); PurposeIndex = (int)item.Purpose; StateIndex = (int)item.State; }
        originalItem = item; revision = snapshot.Revision; loaded = true; NotifyPreview();
    });
    public string ProductName => SelectedProduct?.Product?.Name ?? "Новый товар";
    [RelayCommand] private Task PickProductAsync() => RunAsync(async () =>
    {
        if (!loaded || IsEditing) return;
        await navigation.PickProductAsync(product => SelectedProduct = product is null ? Products[0] : new ProductChoice(product, product.Name));
    });
    [RelayCommand] private Task EditPriceAsync() => RunAsync(async () =>
    {
        if (!loaded || SelectedProduct?.Product is not { } product) return;
        await navigation.EditProductAsync(product.Id);
    });
    [RelayCommand] private Task SaveAsync() => RunAsync(async () =>
    {
        if (!loaded || SelectedProduct is null) throw new FinanceValidationException("Выберите товар.");
        var product = IsNewProduct ? new Product(newProductId, Name, (ProductUnit)UnitIndex, ShoppingCalculator.ParsePrice(Price), null,
            (ShoppingPurpose)PurposeIndex, ShoppingCalculator.ParseQuantity(PriceQuantity)) : null;
        var item = new ShoppingItem(id, product?.Id ?? SelectedProduct.Product!.Id, ShoppingCalculator.ParseQuantity(Quantity), (ShoppingPurpose)PurposeIndex, (ShoppingItemState)StateIndex);
        await store.SaveShoppingItemAsync(listId, item, product, revision); await navigation.BackAsync();
    });
    [RelayCommand] private Task DeleteAsync() => RunAsync(async () =>
    {
        if (!loaded || !IsEditing || !await navigation.ConfirmAsync("Удалить из списка?", "Позиция исчезнет из списка и расчёта стоимости. Товар останется в каталоге.")) return;
        await store.DeleteShoppingItemAsync(listId, id, revision); await navigation.BackAsync();
    });
}
