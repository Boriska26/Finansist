using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Finansist.Core.Assistant;
using Finansist.Core.Finance;

namespace Finansist.App.Services;

public sealed class LocalShoppingModel : ILocalShoppingModel
{
    public ModelDescription Description => GemmaShoppingProtocol.Model;
    private readonly LocalModelFiles files;
    public bool HasFiles => files.HasFiles;
    private readonly SemaphoreSlim gate = new(1, 1);
    public LocalShoppingModel()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        files = new(client, Path.Combine(FileSystem.AppDataDirectory, "llm"), Description, replaceLegacyFiles: true);
    }
    public Task<bool> IsReadyAsync(CancellationToken token) => files.IsReadyAsync(token);
    public async Task DownloadAsync(IProgress<ModelTransfer> progress, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            files.DeleteLegacyFiles();
            using var disk = new Android.OS.StatFs(FileSystem.AppDataDirectory);
            if (disk.AvailableBytes < Description.Bytes - files.PartialBytes + 64_000_000)
                throw new FinanceValidationException("Для загрузки модели нужно около 2,6 ГБ свободного места. Частичная загрузка сохранится.");
            Directory.CreateDirectory(Path.GetDirectoryName(files.ModelPath)!);
            foreach (var name in new[] { "Notice", "gemma-terms.txt" })
            {
                using var source = await FileSystem.OpenAppPackageFileAsync(name);
                using var target = File.Create(Path.Combine(Path.GetDirectoryName(files.ModelPath)!, name));
                await source.CopyToAsync(target, token);
            }
            await files.DownloadAsync(progress, token);
        }
        finally { gate.Release(); }
    }
    public async Task DeleteAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { files.Delete(); }
        finally { gate.Release(); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CancelCallback();
    [DllImport("finansist_llm", CallingConvention = CallingConvention.Cdecl, EntryPoint = "fin_generate_gemma")]
    private static extern int GenerateNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt, CancelCallback cancelled,
        [Out] byte[] output, int capacity, int maxTokens);
    public Task<string> GenerateAsync(string request, int servings, CancellationToken token) =>
        GenerateTextAsync(GemmaShoppingProtocol.IngredientsPrompt(request), 384, token);
    public Task<string> GenerateRecipeAsync(string dish, IReadOnlyList<string> ingredients, CancellationToken token) =>
        GenerateTextAsync(GemmaShoppingProtocol.RecipePrompt(dish, ingredients), 768, token);
    private async Task<string> GenerateTextAsync(string prompt, int maxTokens, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!await files.IsReadyAsync(token)) throw new FinanceValidationException("Модель отсутствует или повреждена. Скачайте её заново.");
            return await Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                CancelCallback cancelled = () => token.IsCancellationRequested || clock.Elapsed > TimeSpan.FromMinutes(5) ? 1 : 0;
                var output = new byte[65536];
                int result;
                try { result = GenerateNative(files.ModelPath, prompt, cancelled, output, output.Length, maxTokens); }
                catch (DllNotFoundException) { throw new FinanceValidationException("Локальный движок недоступен для этого устройства. Установите полный APK."); }
                catch (EntryPointNotFoundException) { throw new FinanceValidationException("Версия локального движка не совпала. Установите обновление APK."); }
                GC.KeepAlive(cancelled);
                token.ThrowIfCancellationRequested();
                if (result == 1) throw new FinanceValidationException("Обработка заняла слишком долго. Попробуйте более короткий запрос.");
                if (result == 2) throw new FinanceValidationException("Модель не удалось открыть. Попробуйте удалить и скачать её заново.");
                if (result == 4) throw new FinanceValidationException("Предложение не поместилось в ответ. Попробуйте более простой запрос.");
                if (result != 0) throw new FinanceValidationException("Локальная модель не смогла обработать запрос. Попробуйте ещё раз.");
                var length = Array.IndexOf(output, (byte)0);
                return Encoding.UTF8.GetString(output, 0, length < 0 ? output.Length : length);
            }, token);
        }
        finally { gate.Release(); }
    }
}
