using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Finansist.Core.Assistant;
using Finansist.Core.Finance;

if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--instruct-2507"))
    throw new ArgumentException("Arguments: native library path, model path, optional --instruct-2507. Only fixed synthetic prompts are used.");
var instruct2507 = args.Length == 3;
string Prompt(string request, int servings)
{
    var prompt = ShoppingAssistantProtocol.Prompt(request, servings);
    // This candidate has a different published template; the production 1.7B prompt stays unchanged.
    return instruct2507 ? prompt.Replace(" /no_think", "", StringComparison.Ordinal).Replace("<think>\n\n</think>\n\n", "", StringComparison.Ordinal) : prompt;
}
Console.WriteLine(instruct2507 ? "Profile: experimental Qwen3-4B-Instruct-2507" : "Profile: production Qwen3-1.7B");
NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) => name == "finansist_llm" ? NativeLibrary.Load(Path.GetFullPath(args[0])) : IntPtr.Zero);
var model = Path.GetFullPath(args[1]);
var failures = 0;
var cases = new[]
{
    new RecipeCase("Борщ", [["свек"], ["капуст"], ["карто"]]),
    new RecipeCase("Гречка по-купечески", [["греч"], ["свин", "говяд", "куриц", "мяс", "фарш", "индей"], ["лук"], ["морков"]]),
    new RecipeCase("Плов", [["рис"], ["свин", "говяд", "куриц", "мяс", "баран", "индей"], ["морков"], ["лук"]]),
    new RecipeCase("Омлет с помидорами", [["яй"], ["помид", "томат"]]),
    new RecipeCase("Вегетарианский борщ без мяса и мясного бульона", [["свек"], ["капуст"]], ["мяс", "говяд", "свин", "куриц", "баран"]),
    new RecipeCase("Омлет с помидорами без молока и сливок", [["яй"], ["помид", "томат"]], ["молок", "сливки"])
};
foreach (var test in cases)
{
    var request = test.Request;
    var clock = Stopwatch.StartNew();
    Native.Cancel callback = () => clock.Elapsed > TimeSpan.FromMinutes(5) ? 1 : 0;
    var output = new byte[65536];
    var status = Native.Generate(model, Prompt(request, 5), ShoppingAssistantProtocol.Grammar, callback, output, output.Length);
    GC.KeepAlive(callback);
    if (status != 0) throw new InvalidOperationException($"Native generation failed: {status}");
    var response = Encoding.UTF8.GetString(output, 0, Array.IndexOf(output, (byte)0));
    try
    {
        var parsed = ShoppingAssistantProtocol.Parse(response);
        Console.WriteLine($"PASS structure: {request}; {clock.Elapsed.TotalSeconds:F2}s; {parsed.Items.Count} ingredients (recipe quality requires review)");
        Console.WriteLine(JsonSerializer.Serialize(parsed, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var names = parsed.Items.Select(i => i.Name.ToLowerInvariant().Replace('ё', 'е')).ToArray();
        var missing = test.Required.Where(group => !group.Any(stem => names.Any(n => n.Contains(stem, StringComparison.Ordinal)))).Select(group => string.Join("/", group)).ToArray();
        var forbidden = (test.Forbidden ?? []).Where(stem => names.Any(n => n.Contains(stem, StringComparison.Ordinal))).ToArray();
        var eggsUnitWrong = parsed.Items.Any(i => i.Name.Contains("яй", StringComparison.OrdinalIgnoreCase) && i.Unit != Finansist.Core.Shopping.ProductUnit.Piece);
        if (missing.Length > 0 || forbidden.Length > 0 || eggsUnitWrong)
        {
            failures++;
            Console.WriteLine($"FAIL recipe basics: missing={string.Join(',', missing)}, forbidden={string.Join(',', forbidden)}, eggsUnitWrong={eggsUnitWrong}");
        }
        else Console.WriteLine("PASS recipe basics (limited checks; quantities and full recipe still require review)");
    }
    catch (FinanceValidationException e)
    {
        failures++;
        Console.WriteLine($"FAIL structure: {request}; {e.Message}");
        Console.WriteLine(response); // Only the fixed synthetic requests above are used in this tool.
    }
}
var timer = Stopwatch.StartNew();
Native.Cancel cancelled = () => timer.Elapsed > TimeSpan.FromSeconds(1) ? 1 : 0;
var cancelledOutput = new byte[65536];
var result = Native.Generate(model, Prompt("Синтетический тест отмены", 2), ShoppingAssistantProtocol.Grammar, cancelled, cancelledOutput, cancelledOutput.Length);
GC.KeepAlive(cancelled);
if (result != 1) throw new InvalidOperationException($"Cancellation failed: {result}");
Console.WriteLine($"PASS real native cancellation: {timer.Elapsed.TotalSeconds:F2}s");

if (failures > 0) Environment.ExitCode = 1;

internal static class Native
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Cancel();
    [DllImport("finansist_llm", EntryPoint = "fin_generate", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Generate([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string grammar, Cancel cancel, [Out] byte[] output, int capacity);
}

internal sealed record RecipeCase(string Request, string[][] Required, string[]? Forbidden = null);
