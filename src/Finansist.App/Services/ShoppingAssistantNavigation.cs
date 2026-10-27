using Finansist.App.Views;
using Finansist.Core.Shopping;
using Finansist.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Finansist.App.Services;

public sealed class ShoppingAssistantNavigation(IServiceProvider services, IShoppingNavigation shopping) : IShoppingAssistantNavigation
{
    private bool opening;
    public async Task OpenAsync()
    {
        if (opening || Shell.Current.CurrentPage is not ShoppingPage) return;
        opening = true;
        try
        {
        var vm = services.GetRequiredService<ShoppingAssistantViewModel>(); vm.PrepareForOpen();
        await Shell.Current.Navigation.PushAsync(new ShoppingAssistantPage(vm));
        }
        finally { opening = false; }
    }
    public Task PickProductAsync(Action<Product?> selected) => shopping.PickProductAsync(selected);
    public Task AddProductAsync(string name, ProductUnit unit, Action<Product> saved)
    {
        var vm = services.GetRequiredService<ProductEditorViewModel>(); vm.ConfigureSuggestion(name, unit, saved);
        return Shell.Current.Navigation.PushAsync(new ProductEditorPage(vm));
    }
    public async Task OpenSavedListAsync(string id)
    {
        if (Shell.Current.CurrentPage is ShoppingAssistantPage) await Shell.Current.Navigation.PopAsync();
        await shopping.OpenListAsync(id);
    }
    public Task<bool> ConfirmDeleteModelAsync() => Shell.Current.DisplayAlertAsync("Удалить модель?", "Освободится место. Товары, списки и финансовые данные сохранятся.", "Удалить", "Отмена");
}
