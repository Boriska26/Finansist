using System.Text;
using System.Text.RegularExpressions;
using Finansist.Core.Finance;

namespace Finansist.Core.Assistant;

public sealed record CookingRecipe(string Text, string Dish, string[] Ingredients)
{
    public bool Matches(string dish, IEnumerable<string> ingredients) =>
        GemmaShoppingProtocol.Key(Dish, Ingredients) == GemmaShoppingProtocol.Key(dish, ingredients);
}

public static class GemmaShoppingProtocol
{
    public static ModelDescription Model { get; } = new("Gemma 3 4B · Q4_K_M",
        "https://huggingface.co/unsloth/gemma-3-4b-it-GGUF/resolve/5a3566e716d80f709ed7b79817eaf7733d2a1fce/gemma-3-4b-it-Q4_K_M.gguf",
        2489894016, "04a43a22e8d2003deda5acc262f68ec1005fa76c735a9962a8c77042a74a7d19");
    private static string Chat(string content) => $"<start_of_turn>user\n{content.Replace("<", "‹", StringComparison.Ordinal).Replace(">", "›", StringComparison.Ordinal)}<end_of_turn>\n<start_of_turn>model\n";
    public static string IngredientsPrompt(string request)
    {
        if (string.IsNullOrWhiteSpace(request) || request.Trim().Length > 500 || request.Any(char.IsControl))
            throw new FinanceValidationException("Опишите блюдо или продукты одной строкой, до 500 символов.");
        return Chat(request.Trim() + " Напиши только названия ингредиентов, без количества, цен, рецепта и пояснений. Каждый ингредиент с новой строки.");
    }
    public static string Normalize(string value) => value.Trim().ToLowerInvariant().Replace('ё', 'е');
    public static string Key(string dish, IEnumerable<string> names) =>
        System.Text.Json.JsonSerializer.Serialize(new { dish = Normalize(dish), names = names.Select(Normalize).Distinct().Order(StringComparer.Ordinal).ToArray() });
    public static void ValidateSource(string dish, IReadOnlyList<string> names)
    {
        if (string.IsNullOrWhiteSpace(dish) || dish.Trim().Length > 80 || dish.Any(char.IsControl))
            throw new FinanceValidationException("Название блюда — от 1 до 80 символов.");
        if (names.Count is < 1 or > 32 || names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 80 || n.Any(char.IsControl)))
            throw new FinanceValidationException("Для рецепта нужно от 1 до 32 ингредиентов с названиями до 80 символов.");
    }
    public static string RecipePrompt(string dish, IReadOnlyList<string> names)
    {
        ValidateSource(dish, names);
        return Chat($"Блюдо: {dish.Trim()}.\nИнгредиенты:\n" + string.Join("\n", names.Select(n => "- " + n.Trim())) +
            "\nНапиши короткую инструкцию приготовления на русском, 4–8 пронумерованных шагов. Используй только перечисленные ингредиенты, в том числе те, что уже есть дома. Не добавляй продукты и не составляй список покупок. Количества не заданы: не придумывай их. Если для блюда не хватает важного ингредиента, сообщи об этом. Без вступления и без повторного перечисления ингредиентов.");
    }
    public static IReadOnlyList<string> ParseIngredients(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 12_000 || text.Contains('{') || text.Contains('}') || text.Contains("<start_of_turn>", StringComparison.Ordinal))
            throw new FinanceValidationException("Модель не вернула список названий. Уточните блюдо и повторите.");
        var result = new List<string>();
        foreach (var line in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var clean = Regex.Replace(line.Trim(), @"^(?:[-*•]|[0-9]+[.)])\s*", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Trim().Trim('*', '`').Trim();
            if (clean.Length == 0 || clean.EndsWith(':') || clean.StartsWith("Вот список", StringComparison.OrdinalIgnoreCase)) continue;
            // Commas inside alternatives such as «зелень (укроп, петрушка)» stay within one item.
            var part = new StringBuilder(); int depth = 0;
            void Add()
            {
                var name = part.ToString().Trim().TrimEnd('.', ';').Trim(); part.Clear();
                if (name.Length == 0) return;
                if (name.Length > 80 || name.Any(char.IsControl)) throw new FinanceValidationException("Название ингредиента получилось слишком длинным. Уточните запрос.");
                if (!result.Any(n => Normalize(n) == Normalize(name))) result.Add(name);
            }
            foreach (var c in clean)
            {
                if (c == '(') depth++;
                if (c == ')') depth = Math.Max(0, depth - 1);
                if ((c == ',' || c == ';') && depth == 0) Add(); else part.Append(c);
            }
            Add();
        }
        if (result.Count is < 1 or > 32) throw new FinanceValidationException("Нужно от 1 до 32 ингредиентов. Попробуйте более конкретное блюдо.");
        return result;
    }
    public static string ParseRecipe(string text)
    {
        var value = text.Trim();
        if (value.Length is < 12 or > 6000 || value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t') || value.Contains("<start_of_turn>", StringComparison.Ordinal))
            throw new FinanceValidationException("Инструкцию не удалось получить целиком. Повторите запрос.");
        return value;
    }
    public static void ValidateRecipe(CookingRecipe recipe)
    {
        ValidateSource(recipe.Dish, recipe.Ingredients);
        _ = ParseRecipe(recipe.Text);
    }
}
