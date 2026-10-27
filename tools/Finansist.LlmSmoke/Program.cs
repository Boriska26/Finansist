using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Finansist.Core.Assistant;

if (args.Length != 2) throw new ArgumentException("Arguments: native library path, model path. Only built-in synthetic prompts are used.");
NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) => name == "finansist_llm" ? NativeLibrary.Load(Path.GetFullPath(args[0])) : IntPtr.Zero);
var model = Path.GetFullPath(args[1]);
foreach (var request in new[] { "Жаркое из курицы с картошкой на два дня", "Омлет с помидорами", "Гречка с курицей и овощами" })
{
    var clock = Stopwatch.StartNew();
    Native.Cancel callback = () => clock.Elapsed > TimeSpan.FromMinutes(2) ? 1 : 0;
    var output = new byte[65536];
    var status = Native.Generate(model, ShoppingAssistantProtocol.Prompt(request, 2), ShoppingAssistantProtocol.Grammar, callback, output, output.Length);
    GC.KeepAlive(callback);
    if (status != 0) throw new InvalidOperationException($"Native generation failed: {status}");
    var response = Encoding.UTF8.GetString(output, 0, Array.IndexOf(output, (byte)0));
    var parsed = ShoppingAssistantProtocol.Parse(response);
    if (request.StartsWith("Омлет") && !parsed.Items.Any(i => i.Name.Contains("яй", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Omelet is missing eggs.");
    Console.WriteLine($"PASS real inference: {request}; {clock.Elapsed.TotalSeconds:F2}s; {parsed.Items.Count} ingredients");
    Console.WriteLine(JsonSerializer.Serialize(parsed));
}
var timer = Stopwatch.StartNew();
Native.Cancel cancelled = () => timer.Elapsed > TimeSpan.FromSeconds(1) ? 1 : 0;
var cancelledOutput = new byte[65536];
var result = Native.Generate(model, ShoppingAssistantProtocol.Prompt("Синтетический тест отмены", 2), ShoppingAssistantProtocol.Grammar, cancelled, cancelledOutput, cancelledOutput.Length);
GC.KeepAlive(cancelled);
if (result != 1) throw new InvalidOperationException($"Cancellation failed: {result}");
Console.WriteLine($"PASS real native cancellation: {timer.Elapsed.TotalSeconds:F2}s");

internal static class Native
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Cancel();
    [DllImport("finansist_llm", EntryPoint = "fin_generate", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Generate([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string grammar, Cancel cancel, [Out] byte[] output, int capacity);
}
