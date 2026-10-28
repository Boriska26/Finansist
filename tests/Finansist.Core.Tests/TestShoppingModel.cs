using Finansist.Core.Assistant;
namespace Finansist.Core.Tests;
internal sealed class TestShoppingModel : ILocalShoppingModel
{
    public ModelDescription Description => new("Синтетическая", "https://synthetic.test/model", 1, "test");
    public bool HasFiles => false;
    public Task<bool> IsReadyAsync(CancellationToken token) => Task.FromResult(false);
    public Task DownloadAsync(IProgress<ModelTransfer> progress, CancellationToken token) => Task.CompletedTask;
    public Task DeleteAsync(CancellationToken token) => Task.CompletedTask;
    public Task<string> GenerateAsync(string request, int servings, CancellationToken token) => throw new InvalidOperationException();
    public Task<string> GenerateRecipeAsync(string dish, IReadOnlyList<string> ingredients, CancellationToken token) => throw new InvalidOperationException();
}
