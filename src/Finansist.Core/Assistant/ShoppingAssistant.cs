using System.Text.Json;
using System.Text.Json.Serialization;
using Finansist.Core.Finance;
using Finansist.Core.Shopping;

namespace Finansist.Core.Assistant;

public sealed record ModelDescription(string Name, string Url, long Bytes, string Sha256);
public sealed record ModelTransfer(long Received, long Total);
public interface ILocalShoppingModel
{
    ModelDescription Description { get; }
    bool HasFiles { get; }
    Task<bool> IsReadyAsync(CancellationToken token);
    Task DownloadAsync(IProgress<ModelTransfer> progress, CancellationToken token);
    Task DeleteAsync(CancellationToken token);
    Task<string> GenerateAsync(string request, int servings, CancellationToken token);
    Task<string> GenerateRecipeAsync(string dish, IReadOnlyList<string> ingredients, CancellationToken token);
}
public sealed record SuggestedIngredient(string Name, decimal Quantity, ProductUnit Unit);
public sealed record SuggestedShoppingList(string Title, IReadOnlyList<SuggestedIngredient> Items);

public static class ShoppingAssistantProtocol
{
    // JSON grammar is also passed to llama.cpp, preventing extra prose and arbitrary fields.
    public const string Grammar = """
root ::= "{" ws "\"title\":" ws string "," ws "\"items\":" ws "[" ws (item ("," ws item){0,31})? "]" ws "}" ws
item ::= "{" ws "\"name\":" ws string "," ws "\"quantity\":" ws number "," ws "\"unit\":" ws ("\"kg\"" | "\"l\"" | "\"piece\"" | "\"g\"" | "\"ml\"") ws "}" ws
string ::= "\"" char{1,80} "\"" ws
char ::= [^"\\\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F]{4})
number ::= ([1-9] [0-9]{0,4} ("." [0-9]{1,3})? | "0." [0-9]{1,3}) ws
ws ::= [ \t\n\r]*
""";
    public static string Prompt(string request, int servings)
    {
        if (string.IsNullOrWhiteSpace(request) || request.Trim().Length > 500) throw new FinanceValidationException("Опишите блюдо или покупки, до 500 символов.");
        if (servings is < 1 or > 12) throw new FinanceValidationException("Укажите от 1 до 12 порций.");
        return """
<|im_start|>system
Ты составляешь список покупок для приготовления блюда. Вспомни обычный домашний рецепт запрошенного блюда и перечисли полный состав, чтобы по этому списку его действительно можно было приготовить.
Нужны конкретные названия всех ингредиентов, включая основные продукты, масло, соль и специи. Не сокращай состав до нескольких позиций. Предположи, что дома ничего нет: пользователь сам удалит ненужное. Не добавляй другие блюда. Выбери один вариант рецепта, без альтернатив и повторов. Названия на русском, короткие, как в продуктовом магазине. Указывай сырые продукты, а не готовые котлеты или полуфабрикаты, если они не запрошены.
Количество каждого ингредиента рассчитай суммарно на все указанные порции. Учти пожелания пользователя. Перед ответом проверь, что основные ингредиенты блюда не пропущены.
Ответ только JSON: {"title":"Название блюда","items":[{"name":"Название продукта","quantity":100,"unit":"g"}]}. Полный список до 32 позиций. Твёрдые продукты и специи — в граммах (g), жидкости — в миллилитрах (ml), штучные продукты — в штуках (piece). Количество положительное, не меньше 1 г или 1 мл. Никаких цен, объяснений или шагов приготовления. На запрос не о продуктах верни items: []. /no_think
<|im_end|>
<|im_start|>user
""" + $"\n{request.Trim()}\nПорций: {servings}. /no_think\n<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";
    }
    private sealed record Response([property: JsonPropertyName("title")] string? Title, [property: JsonPropertyName("items")] Entry[]? Items);
    private sealed record Entry([property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("quantity")] decimal Quantity, [property: JsonPropertyName("unit")] string? Unit);
    public static SuggestedShoppingList Parse(string json)
    {
        if (json.Length > 24_000) throw new FinanceValidationException("Ответ модели слишком длинный. Уточните запрос.");
        try
        {
            var response = JsonSerializer.Deserialize<Response>(json, new JsonSerializerOptions { MaxDepth = 5, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            if (response?.Title is null || (response.Title.Trim().Length is < 1 or > 80 || response.Title.Any(char.IsControl)) || response.Items is null || response.Items.Length is < 1 or > 32)
                throw new FinanceValidationException("Модель не составила подходящий список. Попробуйте конкретное блюдо или перечислите продукты.");
            var result = new List<SuggestedIngredient>();
            foreach (var entry in response.Items)
            {
                if (entry.Name is null || (entry.Name.Trim().Length is < 1 or > 80 || entry.Name.Any(char.IsControl)) || !ShoppingCalculator.ValidQuantity(entry.Quantity))
                    throw new FinanceValidationException("В предложении модели неверное название или количество. Попробуйте ещё раз.");
                var unit = entry.Unit switch { "kg" or "g" => ProductUnit.Kilogram, "l" or "ml" => ProductUnit.Litre, "piece" => ProductUnit.Piece, _ => throw new FinanceValidationException("Модель указала неизвестную единицу. Попробуйте ещё раз.") };
                var quantity = entry.Unit is "g" or "ml" ? entry.Quantity / 1000m : entry.Quantity;
                if (!ShoppingCalculator.ValidQuantity(quantity)) throw new FinanceValidationException("Количество модели не помещается в выбранные единицы. Повторите запрос.");
                result.Add(new(entry.Name.Trim(), quantity, unit));
            }
            if (result.GroupBy(i => (i.Name.ToUpperInvariant().Replace('Ё', 'Е'), i.Unit)).Any(g => g.Count() > 1))
                throw new FinanceValidationException("Модель повторила ингредиенты. Уточните запрос и попробуйте ещё раз.");
            return new(response.Title.Trim(), result);
        }
        catch (JsonException) { throw new FinanceValidationException("Ответ модели не удалось разобрать. Попробуйте ещё раз."); }
    }
}
