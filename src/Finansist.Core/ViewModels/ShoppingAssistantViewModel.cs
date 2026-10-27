using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Assistant;
using Finansist.Core.Budgeting;
using Finansist.Core.Finance;
using Finansist.Core.Shopping;

namespace Finansist.Core.ViewModels;

public interface IShoppingAssistantNavigation
{
    Task OpenAsync();
    Task PickProductAsync(Action<Product?> selected);
    Task AddProductAsync(string name, ProductUnit unit, Action<Product> saved);
    Task OpenSavedListAsync(string id);
    Task<bool> ConfirmDeleteModelAsync();
}
public partial class AssistantIngredientRow : ObservableObject
{
    private IReadOnlyList<Product> catalog = [];
    private Product? product;
    private bool initializing = true;
    public string Id { get; } = Guid.NewGuid().ToString();
    public ObservableCollection<ProductChoice> Candidates { get; } = [];
    public IReadOnlyList<string> Units { get; } = ["шт.", "кг", "л"];
    public IReadOnlyList<string> Purposes { get; } = ["Необходимое", "Для себя"];
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string quantity = "1";
    private int unitIndex;
    public int UnitIndex
    {
        get => unitIndex;
        set
        {
            if (value is < 0 or > 2 || !SetProperty(ref unitIndex, value)) return;
            UnitChanged(value);
        }
    }
    [ObservableProperty] private int purposeIndex;
    [ObservableProperty] private ProductChoice? selectedCandidate;
    [ObservableProperty] private string notice = "";
    public Product? Product => product;
    public bool HasProduct => product is not null;
    public bool NeedsProduct => !HasProduct;
    public bool HasCandidates => Candidates.Count > 0 && NeedsProduct;
    public string PriceText => product is null ? "Товар ещё не выбран, цена неизвестна." : ShoppingCalculator.PriceBasis(product);
    public string CostText
    {
        get
        {
            try { return product is null ? "Без цены" : ShoppingCalculator.Cost(product, ShoppingCalculator.ParseQuantity(Quantity)) is { } amount ? $"≈ {Money.Format(amount)}" : "Цена неизвестна"; }
            catch (FinanceValidationException e) { return e.Message; }
            catch (OverflowException) { return "Проверьте количество и цену."; }
        }
    }
    public AssistantIngredientRow(SuggestedIngredient suggestion)
    {
        Name = suggestion.Name; UnitIndex = (int)suggestion.Unit; Quantity = ShoppingCalculator.QuantityText(suggestion.Quantity); initializing = false;
    }
    private void NotifyState()
    {
        foreach (var property in new[] { nameof(Product), nameof(HasProduct), nameof(NeedsProduct), nameof(HasCandidates), nameof(PriceText), nameof(CostText) }) OnPropertyChanged(property);
    }
    partial void OnQuantityChanged(string value) => OnPropertyChanged(nameof(CostText));
    partial void OnNameChanged(string value) { if (product is not null && product.Name != value) product = null; FindCandidates(); NotifyState(); }
    private void UnitChanged(int value)
    {
        if (product is not null && (int)product.Unit != value) product = null;
        if (!initializing) { Quantity = ""; Notice = "Единица изменилась. Укажите количество в новой единице."; }
        FindCandidates(); NotifyState();
    }
    partial void OnSelectedCandidateChanged(ProductChoice? value) { if (value?.Product is { } p) Assign(p); }
    public void Assign(Product value)
    {
        var changedUnit = UnitIndex != (int)value.Unit;
        product = value; Name = value.Name; UnitIndex = (int)value.Unit; PurposeIndex = (int)value.Purpose;
        if (changedUnit) { Quantity = ""; Notice = "Единица изменилась. Укажите количество в новой единице."; }
        else Notice = "";
        NotifyState();
    }
    public void UpdateCatalog(IReadOnlyList<Product> values)
    {
        catalog = values;
        if (product is not null)
        {
            var current = catalog.FirstOrDefault(p => p.Id == product.Id);
            if (current is null) { product = null; Notice = "Товар удалён из каталога. Выберите другой или добавьте заново."; }
            else if ((int)current.Unit != UnitIndex)
            {
                var purpose = PurposeIndex; Assign(current); PurposeIndex = purpose;
            }
            else { product = current; Name = current.Name; }
        }
        FindCandidates();
        if (product is null)
        {
            var exact = catalog.SingleOrDefault(p => (int)p.Unit == UnitIndex && p.Name.Equals(Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact is not null) Assign(exact);
        }
        NotifyState();
    }
    private void FindCandidates()
    {
        Candidates.Clear();
        if (catalog.Count > 0 && !string.IsNullOrWhiteSpace(Name))
        {
            var result = new ProductSearchIndex(catalog).Find(Name);
            foreach (var p in result.Matches.Concat(result.Similar).Where(p => (int)p.Unit == UnitIndex).DistinctBy(p => p.Id).Take(10))
                Candidates.Add(new(p, p.Name + " · " + ShoppingCalculator.PriceBasis(p)));
        }
        OnPropertyChanged(nameof(HasCandidates));
    }
}

public partial class ShoppingAssistantViewModel(IShoppingStore store, IBudgetStore budget, ILocalShoppingModel model,
    IShoppingAssistantNavigation navigation) : FinanceScreenViewModel
{
    private ShoppingSnapshot snapshot = new(0, new());
    private BudgetState? budgetState;
    private CancellationTokenSource? operation;
    private string listId = Guid.NewGuid().ToString();
    private bool updating;
    public ObservableCollection<AssistantIngredientRow> Rows { get; } = [];
    [ObservableProperty] private string request = "";
    [ObservableProperty] private string servings = "2";
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string estimateText = "";
    [ObservableProperty] private bool modelReady;
    [ObservableProperty] private bool hasModelFiles;
    [ObservableProperty] private bool hasDraft;
    [ObservableProperty] private bool saved;
    [ObservableProperty] private double downloadProgress;
    [ObservableProperty] private bool isDownloading;
    public string ModelText => $"{model.Description.Name} · около {model.Description.Bytes / 1_000_000} МБ · на телефоне";
    public bool NeedsModel => !ModelReady;
    public bool CanSave => HasDraft && !Saved && IsIdle;
    partial void OnModelReadyChanged(bool value) => OnPropertyChanged(nameof(NeedsModel));
    partial void OnHasDraftChanged(bool value) => OnPropertyChanged(nameof(CanSave));
    partial void OnSavedChanged(bool value) => OnPropertyChanged(nameof(CanSave));
    public void PrepareForOpen()
    {
        if (!Saved) return;
        foreach (var row in Rows) row.PropertyChanged -= RowChanged;
        Rows.Clear(); Saved = false; HasDraft = false; listId = Guid.NewGuid().ToString(); Title = ""; EstimateText = "";
    }
    public void CancelWork() => operation?.Cancel();
    private async Task WorkAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; OnPropertyChanged(nameof(CanSave)); ErrorMessage = "";
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = "Отменено. Загрузку можно продолжить, запрос — повторить."; }
        catch (FinanceValidationException e) { ErrorMessage = e.Message; }
        catch (Storage.StorageException e) { ErrorMessage = e.Message; }
        catch (HttpRequestException) { ErrorMessage = "Не удалось скачать модель. Проверьте соединение и повторите: загрузка продолжится."; }
        catch (IOException) { ErrorMessage = "Не удалось прочитать или сохранить файл модели. Проверьте место и повторите."; }
        catch (OverflowException) { ErrorMessage = "Суммы слишком велики. Проверьте количества."; }
        finally { HasModelFiles = model.HasFiles; operation = null; IsBusy = false; OnPropertyChanged(nameof(CanSave)); }
    }
    [RelayCommand] private Task RefreshAsync() => WorkAsync(async token =>
    {
        ModelReady = await model.IsReadyAsync(token);
        snapshot = await store.ReadShoppingAsync(); budgetState = await budget.ReadBudgetAsync();
        updating = true;
        try { foreach (var row in Rows) row.UpdateCatalog(snapshot.Document.Products); }
        finally { updating = false; }
        Recalculate(); Status = ModelReady ? "Модель готова. Запрос обрабатывается локально." : "Для первого запуска скачайте модель. После загрузки интернет для предложений не нужен.";
    });
    [RelayCommand] private Task DownloadAsync() => WorkAsync(async token =>
    {
        Status = "Скачивание модели…"; IsDownloading = true;
        try { await model.DownloadAsync(new Progress<ModelTransfer>(p => { DownloadProgress = (double)p.Received / p.Total; Status = $"Скачано {p.Received / 1_000_000} из {p.Total / 1_000_000} МБ"; }), token); }
        finally { IsDownloading = false; }
        ModelReady = true; Status = "Модель скачана и проверена. Можно составить список.";
    });
    [RelayCommand] private void Cancel() => CancelWork();
    [RelayCommand] private Task DeleteModelAsync() => WorkAsync(async token =>
    {
        if (!await navigation.ConfirmDeleteModelAsync()) return;
        await model.DeleteAsync(token); ModelReady = false; DownloadProgress = 0; Status = "Модель удалена. Списки и финансы сохранены.";
    });
    [RelayCommand] private Task GenerateAsync() => WorkAsync(async token =>
    {
        if (!ModelReady) throw new FinanceValidationException("Сначала скачайте модель.");
        if (!int.TryParse(Servings, NumberStyles.None, CultureInfo.InvariantCulture, out var count)) throw new FinanceValidationException("Введите число порций от 1 до 12.");
        _ = ShoppingAssistantProtocol.Prompt(Request, count);
        Status = "Модель составляет предложение… Можно отменить.";
        var output = await model.GenerateAsync(Request, count, token);
        token.ThrowIfCancellationRequested();
        var proposal = ShoppingAssistantProtocol.Parse(output);
        snapshot = await store.ReadShoppingAsync(); budgetState = await budget.ReadBudgetAsync();
        foreach (var row in Rows) row.PropertyChanged -= RowChanged;
        Rows.Clear(); Title = proposal.Title; listId = Guid.NewGuid().ToString(); Saved = false;
        updating = true;
        try
        {
            foreach (var ingredient in proposal.Items)
            {
                var row = new AssistantIngredientRow(ingredient); row.UpdateCatalog(snapshot.Document.Products);
                row.PropertyChanged += RowChanged; Rows.Add(row);
            }
        }
        finally { updating = false; }
        HasDraft = Rows.Count > 0; Recalculate(); Status = "Это черновик. Проверьте товары и количества перед сохранением.";
    });
    private void RowChanged(object? sender, PropertyChangedEventArgs e) { if (!updating) Recalculate(); }
    private void Recalculate()
    {
        try
        {
            long necessary = 0, joy = 0; int unknown = 0, unmatched = 0;
            foreach (var row in Rows)
            {
                var quantity = ShoppingCalculator.ParseQuantity(row.Quantity);
                var cost = row.Product is { } p ? ShoppingCalculator.Cost(p, quantity) : null;
                if (row.Product is null) unmatched++;
                if (cost is null) unknown++;
                else if (row.PurposeIndex == 0) necessary = checked(necessary + cost.Value); else joy = checked(joy + cost.Value);
            }
            EstimateText = $"Известная стоимость ≈ {Money.Format(checked(necessary + joy))}\nНеобходимое: ≈ {Money.Format(necessary)} · Для себя: ≈ {Money.Format(joy)}\nБез цены: {unknown} · нужно выбрать/добавить товар: {unmatched}";
            if (budgetState is not null)
            {
                var preview = ShoppingCalculator.Preview(new(necessary, joy, 0, 0), budgetState);
                EstimateText += preview.HasBudget ? $"\nПосле покупки по известным ценам: необходимое ≈ {Money.Format(preview.NecessaryAfter)}, для себя ≈ {Money.Format(preview.JoyAfter)}. Отрицательное значение — превышение." : "\nДля прогноза остатков сначала распределите доход на вкладке «Бюджет».";
            }
        }
        catch (FinanceValidationException) { EstimateText = "Проверьте количества в черновике."; }
        catch (OverflowException) { EstimateText = "Сумма слишком велика. Проверьте количества."; }
    }
    [RelayCommand] private void Remove(AssistantIngredientRow row)
    {
        if (IsBusy || Saved) return;
        row.PropertyChanged -= RowChanged; Rows.Remove(row); HasDraft = Rows.Count > 0; Recalculate();
    }
    [RelayCommand] private Task PickProductAsync(AssistantIngredientRow row) => WorkAsync(async _ =>
    {
        await navigation.PickProductAsync(p => { if (p is not null && Rows.Contains(row)) row.Assign(p); });
    });
    [RelayCommand] private Task AddProductAsync(AssistantIngredientRow row) => WorkAsync(async _ =>
    {
        if (string.IsNullOrWhiteSpace(row.Name) || !Enum.IsDefined((ProductUnit)row.UnitIndex)) throw new FinanceValidationException("Проверьте название и единицу нового товара.");
        await navigation.AddProductAsync(row.Name.Trim(), (ProductUnit)row.UnitIndex, p => { if (Rows.Contains(row)) row.Assign(p); });
    });
    [RelayCommand] private Task SaveAsync() => WorkAsync(async _ =>
    {
        if (Saved) { await navigation.OpenSavedListAsync(listId); return; }
        if (!HasDraft || Rows.Any(r => r.Product is null)) throw new FinanceValidationException("Выберите или добавьте ненайденные товары в каталог либо удалите лишние позиции.");
        var list = new ShoppingList(listId, Title, Rows.Select(r => new ShoppingItem(r.Id, r.Product!.Id,
            ShoppingCalculator.ParseQuantity(r.Quantity), (ShoppingPurpose)r.PurposeIndex)).ToList());
        snapshot = await store.SaveProposedListAsync(list, snapshot.Revision);
        Saved = true; Status = "Список сохранён.";
        await navigation.OpenSavedListAsync(listId);
    });
}
