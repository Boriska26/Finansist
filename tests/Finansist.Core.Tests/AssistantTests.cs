using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Finansist.Core.Assistant;
using Finansist.Core.Finance;
using Finansist.Core.Shopping;
using Finansist.Core.Storage;
using Finansist.Core.ViewModels;

namespace Finansist.Core.Tests;

public sealed class AssistantTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "finansist-assistant-tests", Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(directory, "synthetic.db");
    private LocalStore Store() => new(new TestConnectionFactory(Db));
    private const string Output = """{"title":"Тестовый ужин","items":[{"name":"Картошка","quantity":500,"unit":"g"}]}""";
    public AssistantTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    [Fact]
    public void ProtocolNormalizesAmountsToUiUnitsAndRejectsExtraCapabilities()
    {
        var result = ShoppingAssistantProtocol.Parse(Output);
        Assert.Equal(.5m, Assert.Single(result.Items).Quantity); Assert.Equal(ProductUnit.Kilogram, result.Items[0].Unit);
        var liquids = ShoppingAssistantProtocol.Parse(Output.Replace("500", "250").Replace("\"g\"", "\"ml\""));
        Assert.Equal(.25m, liquids.Items[0].Quantity); Assert.Equal(ProductUnit.Litre, liquids.Items[0].Unit);
        Assert.Throws<FinanceValidationException>(() => ShoppingAssistantProtocol.Parse(Output.Replace("\"items\":", "\"sql\":\"DELETE FROM accounts\",\"items\":")));
        Assert.Throws<FinanceValidationException>(() => ShoppingAssistantProtocol.Parse(Output.Replace("500", "0")));
        Assert.Throws<FinanceValidationException>(() => ShoppingAssistantProtocol.Parse(Output.Replace("\"g\"", "\"unknown\"")));
        Assert.Throws<FinanceValidationException>(() => ShoppingAssistantProtocol.Parse("{\"title\":\"x\",\"items\":[]}"));
        Assert.Throws<FinanceValidationException>(() => ShoppingAssistantProtocol.Parse("not json"));
    }
    [Fact]
    public void ProtocolRejectsDuplicatesControlCharactersAndTinyUnrepresentableAmounts()
    {
        var item = """{"name":"Картошка","quantity":500,"unit":"g"}""";
        Assert.Throws<FinanceValidationException>(() => ShoppingAssistantProtocol.Parse("{\"title\":\"x\",\"items\":[" + item + "," + item + "]}"));
        Assert.Throws<FinanceValidationException>(() => ShoppingAssistantProtocol.Parse(Output.Replace("Картошка", "Картошка\\nSQL")));
        Assert.Throws<FinanceValidationException>(() => ShoppingAssistantProtocol.Parse(Output.Replace("500", "0.5")));
        Assert.Throws<FinanceValidationException>(() => ShoppingAssistantProtocol.Prompt("", 2));
        Assert.Throws<FinanceValidationException>(() => ShoppingAssistantProtocol.Prompt("Тестовый суп", 0));
        Assert.Throws<FinanceValidationException>(() => ShoppingAssistantProtocol.Prompt(new string('x', 501), 2));
    }
    private async Task<Product> AddPotato()
    {
        var product = new Product(Guid.NewGuid().ToString(), "Картошка", ProductUnit.Kilogram, 3900, null);
        await Store().SaveProductAsync(product, 0);
        return (await Store().ReadShoppingAsync()).Document.Products[0];
    }
    [Fact]
    public async Task GenerationOnlyCreatesDraftAndExplicitSaveCreatesOneListWithoutFinancialChanges()
    {
        var store = Store(); var product = await AddPotato(); var before = JsonSerializer.Serialize(await store.ReadBudgetAsync());
        var model = new Model(); var navigation = new Navigation();
        var vm = new ShoppingAssistantViewModel(store, store, model, navigation);
        await vm.RefreshCommand.ExecuteAsync(null); vm.Request = "Синтетический ужин";
        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.True(vm.HasDraft); Assert.Equal(product.Id, Assert.Single(vm.Rows).Product?.Id);
        Assert.Equal("0,5", vm.Rows[0].Quantity); Assert.Empty((await store.ReadShoppingAsync()).Document.Lists);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.Saved); Assert.Single((await store.ReadShoppingAsync()).Document.Lists);
        await vm.SaveCommand.ExecuteAsync(null); Assert.Single((await store.ReadShoppingAsync()).Document.Lists);
        Assert.Equal(before, JsonSerializer.Serialize(await store.ReadBudgetAsync()));
        vm.PrepareForOpen(); Assert.False(vm.HasDraft); Assert.False(vm.Saved);
    }
    [Fact]
    public async Task MissingIngredientCanBeCreatedThroughRealCatalogEditorAndAttachedToDraft()
    {
        var store = Store(); var nav = new Navigation();
        var vm = new ShoppingAssistantViewModel(store, store, new Model(), nav);
        await vm.RefreshCommand.ExecuteAsync(null); vm.Request = "Тестовый ужин"; await vm.GenerateCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows); Assert.True(row.NeedsProduct);
        await vm.SaveCommand.ExecuteAsync(null); Assert.True(vm.HasError); Assert.Empty((await store.ReadShoppingAsync()).Document.Lists);
        await vm.AddProductCommand.ExecuteAsync(row);
        Assert.Equal("Картошка", nav.AddedName); Assert.Equal(ProductUnit.Kilogram, nav.AddedUnit); Assert.NotNull(nav.AddedCallback);
        var editor = new ProductEditorViewModel(store, nav);
        editor.ConfigureSuggestion(nav.AddedName!, nav.AddedUnit, nav.AddedCallback!);
        await editor.LoadCommand.ExecuteAsync(null); editor.Price = "39"; await editor.SaveCommand.ExecuteAsync(null);
        Assert.False(editor.HasError); Assert.True(row.HasProduct);
        await vm.RefreshCommand.ExecuteAsync(null); Assert.Contains("19,50", vm.EstimateText);
        await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError);
        Assert.Single((await store.ReadShoppingAsync()).Document.Products); Assert.Single((await store.ReadShoppingAsync()).Document.Lists);
        Assert.Empty((await store.ReadFinanceAsync()).Operations);
    }
    [Fact]
    public async Task AmbiguousNamesRequireChoiceAndChangingUnitRequiresNewQuantity()
    {
        var store = Store();
        await store.SaveProductAsync(new(Guid.NewGuid().ToString(), "Картошка тестовая", ProductUnit.Kilogram, 5000, null), 0);
        var vm = new ShoppingAssistantViewModel(store, store, new Model(), new Navigation());
        await vm.RefreshCommand.ExecuteAsync(null); vm.Request = "Тестовый ужин"; await vm.GenerateCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Rows); Assert.True(row.NeedsProduct); Assert.True(row.HasCandidates);
        row.SelectedCandidate = row.Candidates[0]; Assert.True(row.HasProduct);
        row.PurposeIndex = 1; await vm.RefreshCommand.ExecuteAsync(null); Assert.Equal(1, row.PurposeIndex);
        row.Assign(new(Guid.NewGuid().ToString(), "Тестовая упаковка", ProductUnit.Piece, 100, null));
        Assert.Equal("", row.Quantity); Assert.Contains("Единица", row.Notice);
    }
    [Fact]
    public void CatalogUnitChangesAndManualUnitChangesRequireFreshQuantityButPreservePurpose()
    {
        var product = new Product(Guid.NewGuid().ToString(), "Тестовый продукт", ProductUnit.Kilogram, 100, DateOnly.FromDateTime(DateTime.Today));
        var row = new AssistantIngredientRow(new("Тестовый продукт", .5m, ProductUnit.Kilogram));
        row.UpdateCatalog([product]); row.PurposeIndex = 1;
        row.UpdateCatalog([product with { Unit = ProductUnit.Piece }]);
        Assert.Equal((int)ProductUnit.Piece, row.UnitIndex); Assert.Equal("", row.Quantity); Assert.Equal(1, row.PurposeIndex);
        row.Quantity = "2"; row.UnitIndex = -1; Assert.Equal("2", row.Quantity); Assert.Equal((int)ProductUnit.Piece, row.UnitIndex);
        row.UnitIndex = (int)ProductUnit.Litre; Assert.Equal("", row.Quantity); Assert.True(row.NeedsProduct);
    }
    [Fact]
    public async Task CancellationAndInvalidOutputPreservePreviousDraftAndNeverSave()
    {
        var store = Store(); await AddPotato(); var model = new Model();
        var vm = new ShoppingAssistantViewModel(store, store, model, new Navigation());
        await vm.RefreshCommand.ExecuteAsync(null); vm.Request = "Тестовый ужин"; await vm.GenerateCommand.ExecuteAsync(null);
        var previous = vm.Rows[0]; model.OutputText = "{}"; await vm.GenerateCommand.ExecuteAsync(null);
        Assert.True(vm.HasError); Assert.Same(previous, Assert.Single(vm.Rows));
        model.WaitForCancel = true;
        var task = vm.GenerateCommand.ExecuteAsync(null); await model.Started.Task;
        vm.CancelWork(); await task;
        Assert.False(vm.IsBusy); Assert.Contains("Отменено", vm.Status); Assert.Same(previous, vm.Rows[0]);
        Assert.Empty((await store.ReadShoppingAsync()).Document.Lists);
    }
    [Fact]
    public async Task DeletingDraftRowsAndModelLeavesCatalogAndMoneyUntouched()
    {
        var store = Store(); await AddPotato(); var model = new Model(); var vm = new ShoppingAssistantViewModel(store, store, model, new Navigation());
        await vm.RefreshCommand.ExecuteAsync(null); vm.Request = "Тестовый ужин"; await vm.GenerateCommand.ExecuteAsync(null);
        vm.RemoveCommand.Execute(vm.Rows[0]); Assert.Empty(vm.Rows); Assert.False(vm.HasDraft);
        await vm.DeleteModelCommand.ExecuteAsync(null); Assert.False(vm.ModelReady); Assert.False(vm.HasModelFiles);
        await vm.DownloadCommand.ExecuteAsync(null); Assert.True(vm.ModelReady);
        Assert.Single((await store.ReadShoppingAsync()).Document.Products); Assert.Empty((await store.ReadShoppingAsync()).Document.Lists);
        Assert.Empty((await store.ReadFinanceAsync()).Operations);
    }
    [Fact]
    public async Task ProposedListWriteIsAtomicAndRejectsStaleOrMissingProductReferences()
    {
        var store = Store(); var product = await AddPotato(); var snapshot = await store.ReadShoppingAsync();
        var list = new ShoppingList(Guid.NewGuid().ToString(), "Синтетика", [new(Guid.NewGuid().ToString(), product.Id, 1, ShoppingPurpose.Necessary), new(Guid.NewGuid().ToString(), "missing", 1, ShoppingPurpose.Joy)]);
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveProposedListAsync(list, snapshot.Revision));
        Assert.Empty((await store.ReadShoppingAsync()).Document.Lists);
        list = list with { Items = [list.Items[0]] };
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveProposedListAsync(list, 0));
        var saved = await store.SaveProposedListAsync(list, snapshot.Revision);
        await Assert.ThrowsAsync<FinanceValidationException>(() => store.SaveProposedListAsync(list, saved.Revision));
        Assert.Single((await store.ReadShoppingAsync()).Document.Lists);
    }
    [Fact]
    public async Task ModelFilesResumeValidateChecksumAndDeleteIndependentlyOfUserDatabase()
    {
        var data = Encoding.UTF8.GetBytes("synthetic-model-weights"); var handler = new Server(data);
        var description = new ModelDescription("Тестовая", "https://synthetic.test/model", data.Length, Convert.ToHexString(SHA256.HashData(data)));
        var files = new LocalModelFiles(new HttpClient(handler), directory, description);
        Assert.False(await files.IsReadyAsync(default));
        await File.WriteAllBytesAsync(files.ModelPath + ".part", data[..3]);
        await files.DownloadAsync(new ImmediateProgress(), default);
        Assert.Equal(3, handler.Starts[0]); Assert.True(await files.IsReadyAsync(default));
        await File.WriteAllTextAsync(Db, "synthetic unrelated data");
        files.Delete(); Assert.False(files.HasFiles); Assert.True(File.Exists(Db));
    }
    [Fact]
    public async Task WrongModelHashAndWrongRangeNeverBecomeReady()
    {
        var data = Encoding.UTF8.GetBytes("synthetic-weights");
        var badHash = new ModelDescription("Тестовая", "https://synthetic.test/model", data.Length, new string('0', 64));
        var files = new LocalModelFiles(new HttpClient(new Server(data)), directory, badHash);
        await Assert.ThrowsAsync<FinanceValidationException>(() => files.DownloadAsync(new ImmediateProgress(), default));
        Assert.False(await files.IsReadyAsync(default)); Assert.False(files.HasFiles);
        files = new(new HttpClient(new Server(data) { WrongRange = true }), directory, badHash);
        await Assert.ThrowsAsync<FinanceValidationException>(() => files.DownloadAsync(new ImmediateProgress(), default));
        Assert.False(await files.IsReadyAsync(default));
    }
    [Fact]
    public async Task ModelServerWithoutRangesRestartsSafelyAndCancellationPreservesPartialFile()
    {
        var data = Encoding.UTF8.GetBytes("synthetic-model"); var server = new Server(data) { FullResponse = true };
        var files = new LocalModelFiles(new HttpClient(server), directory, new("Тестовая", "https://synthetic.test/model", data.Length, Convert.ToHexString(SHA256.HashData(data))));
        await File.WriteAllBytesAsync(files.ModelPath + ".part", data[..3]);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.DownloadAsync(new ImmediateProgress(), cancelled.Token));
        Assert.Equal(3, new FileInfo(files.ModelPath + ".part").Length);
        await files.DownloadAsync(new ImmediateProgress(), default); Assert.True(await files.IsReadyAsync(default));
        Assert.Equal(data, await File.ReadAllBytesAsync(files.ModelPath));
    }
    private sealed class ImmediateProgress : IProgress<ModelTransfer> { public void Report(ModelTransfer value) { } }
    private sealed class Server(byte[] data) : HttpMessageHandler
    {
        public List<long> Starts { get; } = [];
        public bool FullResponse { get; init; }
        public bool WrongRange { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = request.Headers.Range!.Ranges.Single(); var from = range.From!.Value; var to = range.To!.Value; Starts.Add(from);
            var response = new HttpResponseMessage(FullResponse ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(FullResponse ? data : data[(int)from..((int)to + 1)]) };
            if (!FullResponse) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(WrongRange ? from + 1 : from, WrongRange ? to + 1 : to, data.Length + (WrongRange ? 1 : 0));
            return Task.FromResult(response);
        }
    }
    private sealed class Model : ILocalShoppingModel
    {
        public ModelDescription Description => new("Тестовая модель", "https://synthetic.test/model", 100, "test");
        public bool HasFiles { get; private set; } = true;
        public string OutputText { get; set; } = Output;
        public bool WaitForCancel { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> IsReadyAsync(CancellationToken token) => Task.FromResult(HasFiles);
        public Task DownloadAsync(IProgress<ModelTransfer> progress, CancellationToken token) { HasFiles = true; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken token) { HasFiles = false; return Task.CompletedTask; }
        public async Task<string> GenerateAsync(string request, int servings, CancellationToken token)
        {
            if (WaitForCancel) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return OutputText;
        }
    }
    private sealed class Navigation : IShoppingAssistantNavigation, IShoppingNavigation
    {
        public string? AddedName { get; private set; }
        public ProductUnit AddedUnit { get; private set; }
        public Action<Product>? AddedCallback { get; private set; }
        public Task AddProductAsync(string name, ProductUnit unit, Action<Product> saved) { AddedName = name; AddedUnit = unit; AddedCallback = saved; return Task.CompletedTask; }
        public Task OpenAsync() => Task.CompletedTask;
        public Task PickProductAsync(Action<Product?> selected) => Task.CompletedTask;
        public Task OpenSavedListAsync(string id) => Task.CompletedTask;
        public Task<bool> ConfirmDeleteModelAsync() => Task.FromResult(true);
        public Task<string?> AskListNameAsync(string? current) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task OpenListAsync(string id) => Task.CompletedTask;
        public Task EditProductAsync(string? id) => Task.CompletedTask;
        public Task EditItemAsync(string listId, string? itemId) => Task.CompletedTask;
        public Task BackAsync() => Task.CompletedTask;
    }
}
