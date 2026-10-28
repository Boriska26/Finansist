using System.Text.Json;
using Finansist.Core.Budgeting;
using Finansist.Core.Assistant;
using Finansist.Core.Finance;
using Finansist.Core.Shopping;

namespace Finansist.Core.Storage;

public sealed partial class LocalStore
{
    private static ShoppingSnapshot ReadShopping(ISqliteConnection db)
    {
        var row = db.Query("SELECT revision, payload FROM shopping_state WHERE id = 1").SingleOrDefault();
        if (row is null) return new(0, new());
        var doc = JsonSerializer.Deserialize<ShoppingDocument>(row[1]!) ?? throw new StorageException("Не удалось прочитать покупки.");
        ShoppingCalculator.Validate(doc);
        return new(long.Parse(row[0]!, Invariant), doc);
    }
    public Task<ShoppingSnapshot> ReadShoppingAsync() => RunAsync(ReadShopping);
    private static ShoppingSnapshot ChangeShopping(ISqliteConnection db, long revision, Func<ShoppingDocument, ShoppingDocument> change)
    {
        var snapshot = ReadShopping(db);
        BudgetCalculator.Require(snapshot.Revision == revision, "Покупки изменились на другом экране. Откройте форму заново; введённые данные пока сохранены на экране.");
        var doc = change(snapshot.Document);
        ShoppingCalculator.Validate(doc);
        db.Execute("INSERT INTO shopping_state (id, revision, payload) VALUES (1, 1, ?) ON CONFLICT(id) DO UPDATE SET revision = revision + 1, payload = excluded.payload", JsonSerializer.Serialize(doc));
        return ReadShopping(db);
    }
    private static Product PrepareProduct(Product product, Product? old) => product with
    {
        Name = product.Name.Trim(),
        PriceDate = product.PriceMinor is null ? null : old is not null && old.PriceMinor == product.PriceMinor && old.PriceQuantity == product.PriceQuantity && old.Unit == product.Unit ? old.PriceDate : DateOnly.FromDateTime(DateTime.Today)
    };
    public Task<ShoppingSnapshot> SaveProductAsync(Product product, long revision) => RunAsync(db => ChangeShopping(db, revision, doc =>
    {
        var old = doc.Products.SingleOrDefault(p => p.Id == product.Id);
        BudgetCalculator.Require(old is null || old.Unit == product.Unit || !doc.Lists.Any(l => l.Items.Any(i => i.ProductId == product.Id)),
            "Этот товар уже используется в списках. Для другой единицы создайте отдельный товар.");
        return doc with { Products = [..doc.Products.Where(p => p.Id != product.Id), PrepareProduct(product, old)] };
    }));
    public Task<ShoppingSnapshot> DeleteProductAsync(string id, long revision) => RunAsync(db => ChangeShopping(db, revision, doc =>
    {
        BudgetCalculator.Require(!doc.Lists.Any(l => l.Items.Any(i => i.ProductId == id)), "Товар есть в списках. Сначала удалите его позиции из списков.");
        return doc with { Products = doc.Products.Where(p => p.Id != id).ToList() };
    }));
    public Task<ShoppingSnapshot> SaveListAsync(string id, string name, long revision) => RunAsync(db => ChangeShopping(db, revision, doc =>
    {
        var old = doc.Lists.SingleOrDefault(l => l.Id == id);
        return doc with { Lists = [..doc.Lists.Where(l => l.Id != id), new(id, name.Trim(), old?.Items ?? [], old?.Recipe)] };
    }));
    public Task<ShoppingSnapshot> SaveCookingRecipeAsync(string listId, CookingRecipe recipe, long revision) => RunAsync(db => ChangeShopping(db, revision, doc =>
    {
        var list = doc.Lists.SingleOrDefault(l => l.Id == listId) ?? throw new FinanceValidationException("Список удалён.");
        GemmaShoppingProtocol.ValidateRecipe(recipe);
        var names = list.Items.Select(i => doc.Products.Single(p => p.Id == i.ProductId).Name);
        BudgetCalculator.Require(recipe.Matches(list.Name, names), "Состав изменился. Обновите список и составьте рецепт заново.");
        return doc with { Lists = doc.Lists.Select(l => l.Id == listId ? l with { Recipe = recipe } : l).ToList() };
    }));
    public Task<ShoppingSnapshot> DeleteListAsync(string id, long revision) => RunAsync(db => ChangeShopping(db, revision,
        doc => doc with { Lists = doc.Lists.Where(l => l.Id != id).ToList() }));
    public Task<ShoppingSnapshot> SaveProposedListAsync(ShoppingList list, long revision) => RunAsync(db => ChangeShopping(db, revision, doc =>
    {
        BudgetCalculator.Require(!doc.Lists.Any(l => l.Id == list.Id), "Этот список уже сохранён.");
        return doc with { Lists = [..doc.Lists, list with { Name = list.Name.Trim() }] };
    }));
    public Task<ShoppingSnapshot> SaveShoppingItemAsync(string listId, ShoppingItem item, Product? newProduct, long revision) => RunAsync(db => ChangeShopping(db, revision, doc =>
    {
        var list = doc.Lists.SingleOrDefault(l => l.Id == listId) ?? throw new FinanceValidationException("Список уже удалён. Вернитесь к покупкам.");
        if (newProduct is not null)
        {
            BudgetCalculator.Require(newProduct.Id == item.ProductId && !doc.Products.Any(p => p.Id == newProduct.Id), "Новый товар уже изменился. Откройте форму заново.");
            doc = doc with { Products = [..doc.Products, PrepareProduct(newProduct, null)] };
        }
        var updated = list with { Items = list.Items.Any(i => i.Id == item.Id)
            ? list.Items.Select(i => i.Id == item.Id ? item : i).ToList() : [..list.Items, item] };
        return doc with { Lists = doc.Lists.Select(l => l.Id == listId ? updated : l).ToList() };
    }));
    public Task<ShoppingSnapshot> DeleteShoppingItemAsync(string listId, string itemId, long revision) => RunAsync(db => ChangeShopping(db, revision, doc =>
    {
        BudgetCalculator.Require(doc.Lists.Any(l => l.Id == listId), "Список уже удалён.");
        return doc with { Lists = doc.Lists.Select(l => l.Id == listId ? l with { Items = l.Items.Where(i => i.Id != itemId).ToList() } : l).ToList() };
    }));
}
