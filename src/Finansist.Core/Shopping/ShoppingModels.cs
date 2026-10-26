using System.Globalization;
using Finansist.Core.Budgeting;
using Finansist.Core.Finance;

namespace Finansist.Core.Shopping;

public enum ProductUnit { Piece, Kilogram, Litre }
public enum ShoppingPurpose { Necessary, Joy }
public enum ShoppingItemState { Needed, Bought, AtHome }
public sealed record Product(string Id, string Name, ProductUnit Unit, long? PriceMinor, DateOnly? PriceDate, ShoppingPurpose Purpose = ShoppingPurpose.Necessary);
public sealed record ShoppingItem(string Id, string ProductId, decimal Quantity, ShoppingPurpose Purpose, ShoppingItemState State = ShoppingItemState.Needed);
public sealed record ShoppingList(string Id, string Name, List<ShoppingItem> Items);
public sealed record ShoppingDocument
{
    public List<Product> Products { get; init; } = [];
    public List<ShoppingList> Lists { get; init; } = [];
}
public sealed record ShoppingSnapshot(long Revision, ShoppingDocument Document);
public interface IShoppingStore
{
    Task<ShoppingSnapshot> ReadShoppingAsync();
    Task<ShoppingSnapshot> SaveProductAsync(Product product, long revision);
    Task<ShoppingSnapshot> DeleteProductAsync(string id, long revision);
    Task<ShoppingSnapshot> SaveListAsync(string id, string name, long revision);
    Task<ShoppingSnapshot> DeleteListAsync(string id, long revision);
    Task<ShoppingSnapshot> SaveShoppingItemAsync(string listId, ShoppingItem item, Product? newProduct, long revision);
    Task<ShoppingSnapshot> DeleteShoppingItemAsync(string listId, string itemId, long revision);
}
public sealed record ShoppingEstimate(long NecessaryMinor, long JoyMinor, int UnknownNecessary, int UnknownJoy)
{
    public long TotalMinor => checked(NecessaryMinor + JoyMinor);
    public int UnknownCount => UnknownNecessary + UnknownJoy;
}
public sealed record ShoppingBudgetPreview(bool HasBudget, long NecessaryAvailable, long JoyAvailable,
    long NecessaryAfter, long JoyAfter, long ReservedBills);

public static class ShoppingCalculator
{
    public static string UnitText(ProductUnit unit) => unit switch { ProductUnit.Piece => "шт.", ProductUnit.Kilogram => "кг", _ => "л" };
    public static string PurposeText(ShoppingPurpose purpose) => purpose == ShoppingPurpose.Necessary ? "Необходимое" : "Для себя";
    public static decimal ParseQuantity(string? text)
    {
        var input = text?.Trim().Replace(',', '.') ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(input, @"^[0-9]+(?:\.[0-9]{1,3})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ||
            !decimal.TryParse(input, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0 || quantity > 100_000)
            throw new FinanceValidationException("Количество — больше нуля, до 100 000, не более трёх знаков после запятой.");
        return quantity;
    }
    public static long? ParsePrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var price = Money.Parse(text, true);
        if (price < 0) throw new FinanceValidationException("Цена не может быть отрицательной.");
        return price;
    }
    public static long? Cost(Product product, decimal quantity) => product.PriceMinor is { } price
        ? checked((long)decimal.Round(price * quantity, 0, MidpointRounding.AwayFromZero)) : null;
    public static ShoppingEstimate Estimate(ShoppingDocument document, ShoppingList list)
    {
        var products = document.Products.ToDictionary(p => p.Id);
        long necessary = 0, joy = 0; int unknownNecessary = 0, unknownJoy = 0;
        foreach (var item in list.Items.Where(i => i.State == ShoppingItemState.Needed))
        {
            var cost = Cost(products[item.ProductId], item.Quantity);
            if (item.Purpose == ShoppingPurpose.Necessary)
            {
                if (cost is { } value) necessary = checked(necessary + value); else unknownNecessary++;
            }
            else { if (cost is { } value) joy = checked(joy + value); else unknownJoy++; }
        }
        var result = new ShoppingEstimate(necessary, joy, unknownNecessary, unknownJoy);
        _ = result.TotalMinor;
        return result;
    }
    public static ShoppingBudgetPreview Preview(ShoppingEstimate estimate, BudgetState state)
    {
        var totals = BudgetCalculator.Totals(state);
        var reserved = BudgetCalculator.ReservedBills(state);
        var necessary = checked(totals.NecessaryMinor - reserved);
        return new(state.Document.Allocations.Count > 0, necessary, totals.JoyMinor,
            checked(necessary - estimate.NecessaryMinor), checked(totals.JoyMinor - estimate.JoyMinor), reserved);
    }
    public static void Validate(ShoppingDocument doc)
    {
        static void Require(bool valid, string message) => BudgetCalculator.Require(valid, message);
        static void Id(string id) => Require(Guid.TryParse(id, out _), "Не удалось определить запись покупки. Откройте экран заново.");
        Require(doc.Products.Select(p => p.Id).Distinct().Count() == doc.Products.Count, "Товар добавлен дважды.");
        Require(doc.Products.Select(p => p.Name.Trim().ToUpperInvariant()).Distinct().Count() == doc.Products.Count, "Товар с таким названием уже есть в каталоге.");
        foreach (var p in doc.Products)
        {
            Id(p.Id);
            Require(p.Name.Trim().Length is >= 1 and <= 80, "Название товара — от 1 до 80 символов.");
            Require(Enum.IsDefined(p.Unit) && Enum.IsDefined(p.Purpose), "Выберите единицу и назначение товара.");
            Require(p.PriceMinor is null || p.PriceMinor >= 0 && p.PriceMinor <= Money.MaximumMinor, "Цена — от нуля до 1 трлн ₽ или пустое поле.");
            Require(p.PriceMinor.HasValue == p.PriceDate.HasValue && (p.PriceDate is null || p.PriceDate <= DateOnly.FromDateTime(DateTime.Today)), "Проверьте дату известной цены.");
        }
        Require(doc.Lists.Select(l => l.Id).Distinct().Count() == doc.Lists.Count, "Список добавлен дважды.");
        foreach (var list in doc.Lists)
        {
            Id(list.Id); Require(list.Name.Trim().Length is >= 1 and <= 80, "Название списка — от 1 до 80 символов.");
            Require(list.Items.Select(i => i.Id).Distinct().Count() == list.Items.Count, "Позиция добавлена дважды.");
            foreach (var item in list.Items)
            {
                Id(item.Id);
                Require(doc.Products.Any(p => p.Id == item.ProductId), "Товар больше не найден. Обновите список.");
                Require(item.Quantity > 0 && item.Quantity <= 100_000 && decimal.Round(item.Quantity, 3) == item.Quantity, "Проверьте количество товара.");
                Require(Enum.IsDefined(item.Purpose) && Enum.IsDefined(item.State), "Выберите назначение и состояние покупки.");
            }
            _ = Estimate(doc, list);
        }
    }
}
