using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Assistant;
using Finansist.Core.Finance;

namespace Finansist.Core.ViewModels;

public partial class CookingRecipeViewModel(ILocalShoppingModel model, Func<CookingRecipe, Task>? save = null) : FinanceScreenViewModel
{
    private string dish = "";
    private string[] ingredients = [];
    private CancellationTokenSource? operation;
    public CookingRecipe? Value { get; private set; }
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private string status = "";
    public bool HasRecipe => Value is not null;
    public bool HasCurrentRecipe => Value is not null && !IsStale;
    public bool IsStale => Value is not null && !Value.Matches(dish, ingredients);
    public string Text => HasCurrentRecipe ? Value!.Text : "";
    public string ButtonText => IsStale ? "Обновить рецепт по составу" : HasRecipe ? "Составить рецепт заново" : "Как приготовить";
    public void SetSource(string name, IEnumerable<string> names, CookingRecipe? loaded)
    {
        var next = names.Select(n => n.Trim()).ToArray();
        if (GemmaShoppingProtocol.Key(dish, ingredients) != GemmaShoppingProtocol.Key(name, next)) CancelWork();
        dish = name; ingredients = next; Value = loaded;
        NotifyRecipe();
    }
    private void NotifyRecipe()
    {
        foreach (var name in new[] { nameof(Value), nameof(HasRecipe), nameof(HasCurrentRecipe), nameof(IsStale), nameof(Text), nameof(ButtonText) }) OnPropertyChanged(name);
    }
    public void CancelWork() => operation?.Cancel();
    [RelayCommand] private void Cancel() => CancelWork();
    [RelayCommand] private void ToggleExpanded() => IsExpanded = !IsExpanded;
    [RelayCommand] private async Task GenerateAsync()
    {
        if (IsBusy) return;
        IsBusy = true; ErrorMessage = "";
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        try
        {
            var sourceDish = dish; var sourceNames = ingredients.ToArray();
            _ = GemmaShoppingProtocol.RecipePrompt(sourceDish, sourceNames);
            if (!await model.IsReadyAsync(cancellation.Token)) throw new FinanceValidationException("Сначала скачайте Gemma во вкладке «Покупки → Помощник».");
            Status = "Составляю инструкцию по текущим ингредиентам…";
            var text = await model.GenerateRecipeAsync(sourceDish, sourceNames, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var recipe = new CookingRecipe(GemmaShoppingProtocol.ParseRecipe(text), sourceDish, sourceNames);
            if (!recipe.Matches(dish, ingredients)) throw new OperationCanceledException();
            if (save is not null) await save(recipe);
            Value = recipe; IsExpanded = true; Status = "Проверьте инструкцию и готовность продуктов: модель может ошибаться."; NotifyRecipe();
        }
        catch (OperationCanceledException) { Status = "Подготовка рецепта отменена. Можно повторить по текущему составу."; }
        catch (FinanceValidationException e) { ErrorMessage = e.Message; }
        catch (Storage.StorageException e) { ErrorMessage = e.Message; }
        catch (IOException) { ErrorMessage = "Не удалось прочитать модель. Проверьте её загрузку в помощнике."; }
        finally { operation = null; IsBusy = false; }
    }
}
