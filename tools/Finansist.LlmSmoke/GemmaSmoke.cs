using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Finansist.Core.Assistant;

internal static class GemmaSmoke
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Cancel();
    [DllImport("finansist_llm", EntryPoint = "fin_generate_gemma", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Generate([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt,
        Cancel cancelled, [Out] byte[] output, int capacity, int maxTokens);
    public static void Run(string library, string model)
    {
        NativeLibrary.SetDllImportResolver(typeof(GemmaSmoke).Assembly, (name, _, _) => name == "finansist_llm" ? NativeLibrary.Load(Path.GetFullPath(library)) : IntPtr.Zero);
        string Call(string prompt, int maxTokens)
        {
            var timer = Stopwatch.StartNew(); Cancel cancel = () => timer.Elapsed > TimeSpan.FromMinutes(5) ? 1 : 0;
            var buffer = new byte[65536];
            var code = Generate(Path.GetFullPath(model), prompt, cancel, buffer, buffer.Length, maxTokens); GC.KeepAlive(cancel);
            if (code != 0) throw new InvalidOperationException($"Native code {code}");
            Console.WriteLine($"Native time {timer.Elapsed.TotalSeconds:F2}s");
            return Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
        }
        foreach (var dish in new[] { "Омлет с помидорами", "Гречка по-купечески с мясом" })
        {
            var names = GemmaShoppingProtocol.ParseIngredients(Call(GemmaShoppingProtocol.IngredientsPrompt(dish), 384));
            Console.WriteLine(dish + " ingredients: " + JsonSerializer.Serialize(names, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            var recipe = GemmaShoppingProtocol.ParseRecipe(Call(GemmaShoppingProtocol.RecipePrompt(dish, names), 768));
            Console.WriteLine("Recipe: " + recipe);
        }
        var clock = Stopwatch.StartNew(); Cancel cancelled = () => clock.Elapsed > TimeSpan.FromSeconds(1) ? 1 : 0;
        var output = new byte[65536]; var result = Generate(Path.GetFullPath(model), GemmaShoppingProtocol.IngredientsPrompt("Тестовая отмена"), cancelled, output, output.Length, 384);
        GC.KeepAlive(cancelled);
        if (result != 1) throw new InvalidOperationException("Cancellation failed");
        Console.WriteLine($"PASS Gemma cancellation {clock.Elapsed.TotalSeconds:F2}s");
    }
}
