using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Finansist.Core.Assistant;
using Finansist.Core.Finance;

if (args.Length != 2) throw new ArgumentException("Arguments: native library path, model path. Only built-in synthetic prompts are used.");
NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) => name == "finansist_llm" ? NativeLibrary.Load(Path.GetFullPath(args[0])) : IntPtr.Zero);
var model = Path.GetFullPath(args[1]);
var failures = 0;
foreach (var request in new[] { "Борщ", "Гречка по-купечески", "Плов", "Омлет с помидорами" })
{
    var clock = Stopwatch.StartNew();
    Native.Cancel callback = () => clock.Elapsed > TimeSpan.FromMinutes(5) ? 1 : 0;
    var output = new byte[65536];
    var status = Native.Generate(model, ShoppingAssistantProtocol.Prompt(request, 5), ShoppingAssistantProtocol.Grammar, callback, output, output.Length);
    GC.KeepAlive(callback);
    if (status != 0) throw new InvalidOperationException($"Native generation failed: {status}");
    var response = Encoding.UTF8.GetString(output, 0, Array.IndexOf(output, (byte)0));
    try
    {
        var parsed = ShoppingAssistantProtocol.Parse(response);
        Console.WriteLine($"PASS structure: {request}; {clock.Elapsed.TotalSeconds:F2}s; {parsed.Items.Count} ingredients (recipe quality requires review)");
        Console.WriteLine(JsonSerializer.Serialize(parsed, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
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
var result = Native.Generate(model, ShoppingAssistantProtocol.Prompt("Синтетический тест отмены", 2), ShoppingAssistantProtocol.Grammar, cancelled, cancelledOutput, cancelledOutput.Length);
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
