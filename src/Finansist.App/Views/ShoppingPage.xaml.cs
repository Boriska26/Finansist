using Finansist.Core.ViewModels;
namespace Finansist.App.Views;

public partial class ShoppingPage : ContentPage
{
    public ShoppingViewModel ViewModel { get; }
    private readonly IShoppingAssistantNavigation assistant;
    public ShoppingPage(ShoppingViewModel viewModel, IShoppingAssistantNavigation assistant)
    {
        this.assistant = assistant;
        ViewModel = viewModel;
        InitializeComponent(); BindingContext = viewModel;
    }
    private async void OnAssistantClicked(object? sender, EventArgs args) => await assistant.OpenAsync();
    protected override async void OnAppearing()
    {
        base.OnAppearing(); await ViewModel.RefreshCommand.ExecuteAsync(null);
    }
}
