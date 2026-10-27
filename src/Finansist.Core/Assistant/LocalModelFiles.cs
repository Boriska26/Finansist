using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Finansist.Core.Finance;

namespace Finansist.Core.Assistant;

// Only public model weights are downloaded. User requests never enter this service.
public sealed class LocalModelFiles(HttpClient client, string directory, ModelDescription model)
{
    private sealed record VerifiedFile(long Length, DateTime Modified);
    private VerifiedFile? verified;
    public bool HasFiles => File.Exists(ModelPath) || File.Exists(PartPath);
    public string ModelPath => Path.Combine(directory, "shopping-model.gguf");
    public long PartialBytes => File.Exists(PartPath) ? Math.Min(model.Bytes, new FileInfo(PartPath).Length) : 0;
    private string PartPath => ModelPath + ".part";
    public async Task<bool> IsReadyAsync(CancellationToken token)
    {
        if (!File.Exists(ModelPath)) { verified = null; return false; }
        var info = new FileInfo(ModelPath);
        if (info.Length != model.Bytes) { verified = null; return false; }
        if (verified is { } cached && cached.Length == info.Length && cached.Modified == info.LastWriteTimeUtc) return true;
        var valid = await MatchesHash(ModelPath, token);
        verified = valid ? new(info.Length, info.LastWriteTimeUtc) : null;
        return valid;
    }
    private async Task<bool> MatchesHash(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    public async Task DownloadAsync(IProgress<ModelTransfer> progress, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        await using (var file = new FileStream(PartPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 81920, true))
        {
            if (file.Length > model.Bytes) file.SetLength(0);
            file.Position = file.Length;
            var buffer = new byte[81920];
            var reportClock = System.Diagnostics.Stopwatch.StartNew();
            progress.Report(new(file.Position, model.Bytes));
            while (file.Position < model.Bytes)
            {
                token.ThrowIfCancellationRequested();
                var start = file.Position; var end = Math.Min(model.Bytes - 1, start + 4 * 1024 * 1024 - 1);
                using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
                request.Headers.Range = new RangeHeaderValue(start, end);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                long expected;
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range?.From != start || range.To != end || range.Length != model.Bytes)
                        throw new FinanceValidationException("Сервер вернул неверный фрагмент модели. Повторите загрузку.");
                    expected = end - start + 1;
                }
                else if (response.StatusCode == HttpStatusCode.OK)
                {
                    file.SetLength(0); file.Position = 0; expected = model.Bytes;
                }
                else throw new FinanceValidationException("Не удалось скачать модель. Повторите позже.");
                await using var input = await response.Content.ReadAsStreamAsync(token);
                while (expected > 0)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected)), token);
                    if (read == 0) throw new IOException("Model download interrupted.");
                    await file.WriteAsync(buffer.AsMemory(0, read), token); expected -= read;
                    if (reportClock.ElapsedMilliseconds >= 150 || file.Position == model.Bytes)
                    {
                        progress.Report(new(file.Position, model.Bytes)); reportClock.Restart();
                    }
                }
                if (await input.ReadAsync(buffer.AsMemory(0, 1), token) != 0) throw new FinanceValidationException("Размер модели не совпал. Повторите загрузку.");
                await file.FlushAsync(token);
            }
        }
        if (!await MatchesHash(PartPath, token))
        {
            File.Delete(PartPath);
            throw new FinanceValidationException("Контрольная сумма модели не совпала. Файл удалён; скачайте заново.");
        }
        File.Move(PartPath, ModelPath, true);
        var info = new FileInfo(ModelPath); verified = new(info.Length, info.LastWriteTimeUtc);
    }
    public void Delete()
    {
        verified = null;
        if (File.Exists(ModelPath)) File.Delete(ModelPath);
        if (File.Exists(PartPath)) File.Delete(PartPath);
    }
}
