using System.Text.Json;
using Finansist.Core.Assistant;
using Finansist.Core.Finance;
using Finansist.Core.Shopping;
using Finansist.Core.Storage;
using Finansist.Core.ViewModels;

namespace Finansist.Core.Tests;

public sealed class RecipeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "finansist-recipe-tests", Guid.NewGuid().ToString("N"));
    private LocalStore Store() => new(new TestConnectionFactory(Path.Combine(directory, "synthetic.db")));
    public RecipeTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private const string Steps = "1. Подготовьте продукты.\n2. Приготовьте их до готовности.";

    [Fact]
    public void NamesOnlyParsingHandlesListsAlternativesAndRejectsInvalidResponses()
    {
        var names = GemmaShoppingProtocol.ParseIngredients("Вот список ингредиентов:\n* Яйца\n* Помидоры\n* Зелень (укроп, петрушка)\n* яйца");
        Assert.Equal(3, names.Count); Assert.Equal("Зелень (укроп, петрушка)", names[2]);
        Assert.Equal(2, GemmaShoppingProtocol.ParseIngredients("Яйца, помидоры").Count);
        Assert.Throws<FinanceValidationException>(() => GemmaShoppingProtocol.ParseIngredients("{}"));
        Assert.Throws<FinanceValidationException>(() => GemmaShoppingProtocol.ParseIngredients(""));
        Assert.Throws<FinanceValidationException>(() => GemmaShoppingProtocol.ParseIngredients(new string('x', 81)));
        Assert.Throws<FinanceValidationException>(() => GemmaShoppingProtocol.ParseIngredients(string.Join('\n', Enumerable.Range(0, 33).Select(i => "Продукт " + i))));
        var prompt = GemmaShoppingProtocol.IngredientsPrompt("Омлет");
        Assert.DoesNotContain("<|im_start|>", prompt); Assert.DoesNotContain("<bos>", prompt);
        Assert.Contains("<start_of_turn>user", prompt);
        Assert.DoesNotContain("<start_of_turn>model\n<start_of_turn>", GemmaShoppingProtocol.IngredientsPrompt("<start_of_turn>test"));
    }
    [Fact]
    public void RecipeUsesOnlyCurrentNamesAndSourceComparisonIgnoresOrderCaseAndDuplicates()
    {
        var prompt = GemmaShoppingProtocol.RecipePrompt("Омлет", ["Яйца", "Помидоры"]);
        Assert.Contains("- Яйца\n- Помидоры", prompt); Assert.DoesNotContain("молоко", prompt);
        var recipe = new CookingRecipe(Steps, "Омлет", ["Яйца", "Помидоры"]);
        Assert.True(recipe.Matches(" омлет ", ["помидоры", "ЯЙЦА", "яйца"]));
        Assert.False(recipe.Matches("Омлет", ["Яйца"]));
        Assert.Throws<FinanceValidationException>(() => GemmaShoppingProtocol.RecipePrompt("Омлет", []));
        Assert.Throws<FinanceValidationException>(() => GemmaShoppingProtocol.ParseRecipe(""));
        Assert.Throws<FinanceValidationException>(() => GemmaShoppingProtocol.ParseRecipe(new string('x', 6001)));
    }
    [Fact]
    public async Task DraftIncludesHomeIngredientsExcludesRemovedAndDoesNotSaveOrChangeMoney()
    {
        var store = Store(); var before = JsonSerializer.Serialize(await store.ReadFinanceAsync());
        var model = new Model(); var vm = new ShoppingAssistantViewModel(store, store, model, new Navigation());
        await vm.RefreshCommand.ExecuteAsync(null); vm.Request = "Омлет";
        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(0, model.RecipeCalls); Assert.All(vm.Rows, r => Assert.Equal("", r.Quantity));
        vm.Rows[0].IsAtHome = true;
        vm.RemoveCommand.Execute(vm.Rows[2]);
        await vm.Recipe.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Яйца", "Помидоры" }, model.Names);
        Assert.True(vm.Recipe.HasCurrentRecipe); Assert.True(vm.Recipe.IsExpanded);
        Assert.Empty((await store.ReadShoppingAsync()).Document.Lists);
        Assert.Equal(before, JsonSerializer.Serialize(await store.ReadFinanceAsync()));
        vm.Rows[1].Name = "Сыр";
        Assert.True(vm.Recipe.IsStale); Assert.Equal("", vm.Recipe.Text);
        await vm.Recipe.GenerateCommand.ExecuteAsync(null);
        Assert.Contains("Сыр", model.Names); Assert.False(vm.Recipe.IsStale);
    }
    [Fact]
    public async Task RecipePersistsAcrossReloadIncludesHomeAndRenameDoesNotDiscardIt()
    {
        var store = Store();
        var a = new Product(Guid.NewGuid().ToString(), "Яйца", ProductUnit.Piece, 1000, null);
        var b = new Product(Guid.NewGuid().ToString(), "Помидоры", ProductUnit.Kilogram, 10000, null);
        var snapshot = await store.SaveProductAsync(a, 0); snapshot = await store.SaveProductAsync(b, snapshot.Revision);
        var list = new ShoppingList(Guid.NewGuid().ToString(), "Омлет", [new(Guid.NewGuid().ToString(), a.Id, 2, ShoppingPurpose.Necessary), new(Guid.NewGuid().ToString(), b.Id, .2m, ShoppingPurpose.Necessary, ShoppingItemState.AtHome)]);
        snapshot = await store.SaveProposedListAsync(list, snapshot.Revision);
        var before = JsonSerializer.Serialize(await store.ReadBudgetAsync());
        var model = new Model(); var vm = new ShoppingListViewModel(store, store, new Navigation(), model); vm.Configure(list.Id);
        await vm.RefreshCommand.ExecuteAsync(null); await vm.Recipe.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Яйца", "Помидоры" }, model.Names); Assert.False(vm.Recipe.HasError);
        var reloaded = new ShoppingListViewModel(Store(), Store(), new Navigation(), model); reloaded.Configure(list.Id);
        await reloaded.RefreshCommand.ExecuteAsync(null); Assert.True(reloaded.Recipe.HasCurrentRecipe); Assert.Equal(Steps, reloaded.Recipe.Text);
        var current = await store.ReadShoppingAsync();
        await store.SaveListAsync(list.Id, "Другой омлет", current.Revision);
        await reloaded.RefreshCommand.ExecuteAsync(null); Assert.True(reloaded.Recipe.IsStale); Assert.NotNull(reloaded.Recipe.Value);
        Assert.Equal(before, JsonSerializer.Serialize(await store.ReadBudgetAsync()));
    }
    [Fact]
    public async Task CancelAndSourceChangeCannotPublishAnObsoleteRecipe()
    {
        var model = new Model { Wait = true }; var recipe = new CookingRecipeViewModel(model);
        recipe.SetSource("Омлет", ["Яйца"], null);
        var work = recipe.GenerateCommand.ExecuteAsync(null); await model.Started.Task;
        recipe.SetSource("Омлет", ["Яйца", "Помидоры"], null);
        await work;
        Assert.False(recipe.HasRecipe); Assert.False(recipe.IsBusy); Assert.Contains("отменена", recipe.Status);
        model.Wait = false; await recipe.GenerateCommand.ExecuteAsync(null); Assert.True(recipe.HasCurrentRecipe);
        var previous = recipe.Value; model.Output = ""; await recipe.GenerateCommand.ExecuteAsync(null);
        Assert.True(recipe.HasError); Assert.Same(previous, recipe.Value);
    }
    [Fact]
    public async Task StoreRejectsStaleRecipeAndInvalidBodyWithoutMutatingItems()
    {
        var store = Store(); var p = new Product(Guid.NewGuid().ToString(), "Яйца", ProductUnit.Piece, null, null);
        var snapshot = await store.SaveProductAsync(p, 0);
        var list = new ShoppingList(Guid.NewGuid().ToString(), "Омлет", [new(Guid.NewGuid().ToString(), p.Id, 2, ShoppingPurpose.Necessary)]);
        snapshot = await store.SaveProposedListAsync(list, snapshot.Revision);
        var recipe = new CookingRecipe(Steps, "Омлет", ["Помидоры"]);
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveCookingRecipeAsync(list.Id, recipe, snapshot.Revision));
        recipe = recipe with { Ingredients = ["Яйца"], Text = "" };
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveCookingRecipeAsync(list.Id, recipe, snapshot.Revision));
        recipe = recipe with { Text = Steps };
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveCookingRecipeAsync(list.Id, recipe, 0));
        var saved = await store.SaveCookingRecipeAsync(list.Id, recipe, snapshot.Revision);
        Assert.Equal(list.Items[0], saved.Document.Lists[0].Items[0]);
        Assert.True(saved.Document.Lists[0].Recipe!.Matches("Омлет", ["Яйца"]));
    }
    [Fact]
    public async Task OldShoppingJsonWithoutRecipeLoadsAndRecipeSurvivesItemEditsButBecomesStale()
    {
        var store = Store(); var product = new Product(Guid.NewGuid().ToString(), "Яйца", ProductUnit.Piece, null, null);
        var snapshot = await store.SaveProductAsync(product, 0);
        var list = new ShoppingList(Guid.NewGuid().ToString(), "Омлет", [new(Guid.NewGuid().ToString(), product.Id, 2, ShoppingPurpose.Necessary)]);
        snapshot = await store.SaveProposedListAsync(list, snapshot.Revision);
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(snapshot.Document))!;
        json["Lists"]![0]!.AsObject().Remove("Recipe");
        using (var db = new TestConnectionFactory(Path.Combine(directory, "synthetic.db")).Open())
        {
            db.Execute("UPDATE shopping_state SET payload = ?", json.ToJsonString());
            Assert.Equal("6", db.Scalar("PRAGMA user_version"));
        }
        snapshot = await Store().ReadShoppingAsync(); Assert.Null(snapshot.Document.Lists[0].Recipe);
        snapshot = await store.SaveCookingRecipeAsync(list.Id, new(Steps, "Омлет", ["Яйца"]), snapshot.Revision);
        snapshot = await store.SaveShoppingItemAsync(list.Id, list.Items[0] with { State = ShoppingItemState.AtHome }, null, snapshot.Revision);
        Assert.True(snapshot.Document.Lists[0].Recipe!.Matches("Омлет", ["Яйца"]));
        snapshot = await store.DeleteShoppingItemAsync(list.Id, list.Items[0].Id, snapshot.Revision);
        Assert.NotNull(snapshot.Document.Lists[0].Recipe); Assert.False(snapshot.Document.Lists[0].Recipe!.Matches("Омлет", []));
    }
    [Fact]
    public async Task SavingAssistantDraftKeepsItsRecipeAndHomeFlagInTheSameList()
    {
        var store = Store();
        var eggs = new Product(Guid.NewGuid().ToString(), "Яйца", ProductUnit.Piece, null, null);
        var tomatoes = new Product(Guid.NewGuid().ToString(), "Помидоры", ProductUnit.Kilogram, null, null);
        var snapshot = await store.SaveProductAsync(eggs, 0); await store.SaveProductAsync(tomatoes, snapshot.Revision);
        var vm = new ShoppingAssistantViewModel(store, store, new Model(), new Navigation());
        await vm.RefreshCommand.ExecuteAsync(null); vm.Request = "Омлет"; await vm.GenerateCommand.ExecuteAsync(null);
        vm.RemoveCommand.Execute(vm.Rows[2]);
        vm.Rows[0].Quantity = "2"; vm.Rows[0].IsAtHome = true; vm.Rows[1].Quantity = "0,2";
        await vm.Recipe.GenerateCommand.ExecuteAsync(null); await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.HasError);
        var list = Assert.Single((await Store().ReadShoppingAsync()).Document.Lists);
        Assert.Equal(ShoppingItemState.AtHome, list.Items[0].State);
        Assert.Equal(.2m, list.Items[1].Quantity); Assert.NotNull(list.Recipe);
        Assert.True(list.Recipe.Matches("Омлет", ["Яйца", "Помидоры"]));
        Assert.Empty((await store.ReadFinanceAsync()).Operations);
    }
    private sealed class Model : ILocalShoppingModel
    {
        public ModelDescription Description => GemmaShoppingProtocol.Model;
        public bool HasFiles => true;
        public bool Wait { get; set; }
        public string Output { get; set; } = Steps;
        public string[] Names { get; private set; } = [];
        public int RecipeCalls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> IsReadyAsync(CancellationToken token) => Task.FromResult(true);
        public Task DownloadAsync(IProgress<ModelTransfer> progress, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken token) => Task.CompletedTask;
        public Task<string> GenerateAsync(string request, int servings, CancellationToken token) => Task.FromResult("Яйца\nПомидоры\nМолоко");
        public async Task<string> GenerateRecipeAsync(string dish, IReadOnlyList<string> ingredients, CancellationToken token)
        {
            RecipeCalls++; Names = ingredients.ToArray(); Started.TrySetResult();
            if (Wait) await Task.Delay(Timeout.Infinite, token);
            return Output;
        }
    }
    private sealed class Navigation : IShoppingNavigation, IShoppingAssistantNavigation
    {
        public Task<string?> AskListNameAsync(string? current) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task OpenListAsync(string id) => Task.CompletedTask;
        public Task EditProductAsync(string? id) => Task.CompletedTask;
        public Task EditItemAsync(string listId, string? itemId) => Task.CompletedTask;
        public Task BackAsync() => Task.CompletedTask;
        public Task OpenAsync() => Task.CompletedTask;
        public Task PickProductAsync(Action<Product?> selected) => Task.CompletedTask;
        public Task AddProductAsync(string name, ProductUnit unit, Action<Product> saved) => Task.CompletedTask;
        public Task OpenSavedListAsync(string id) => Task.CompletedTask;
        public Task<bool> ConfirmDeleteModelAsync() => Task.FromResult(true);
    }
}
