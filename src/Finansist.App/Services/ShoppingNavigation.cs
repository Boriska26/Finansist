using Finansist.App.Views;
using Finansist.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Finansist.App.Services;

public sealed class ShoppingNavigation(IServiceProvider services) : IShoppingNavigation
{
    public Task<string?> AskListNameAsync(string? current) => Shell.Current.DisplayPromptAsync(current is null ? "Новый список" : "Название списка",
        "Как назвать список покупок?", "Сохранить", "Отмена", "Например, на неделю", 80, Keyboard.Text, current ?? "");
    public Task<bool> ConfirmAsync(string title, string message) => Shell.Current.DisplayAlertAsync(title, message, "Удалить", "Отмена");
    public Task OpenListAsync(string id)
    {
        var vm = services.GetRequiredService<ShoppingListViewModel>(); vm.Configure(id);
        return Shell.Current.Navigation.PushAsync(new ShoppingListPage(vm));
    }
    public Task EditProductAsync(string? id)
    {
        var vm = services.GetRequiredService<ProductEditorViewModel>(); vm.Configure(id);
        return Shell.Current.Navigation.PushAsync(new ProductEditorPage(vm));
    }
    public Task EditItemAsync(string listId, string? itemId)
    {
        var vm = services.GetRequiredService<ShoppingItemEditorViewModel>(); vm.Configure(listId, itemId);
        return Shell.Current.Navigation.PushAsync(new ShoppingItemEditorPage(vm));
    }
    public async Task BackAsync() => await Shell.Current.Navigation.PopAsync();
}
