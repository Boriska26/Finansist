using Finansist.Core.ViewModels;
namespace Finansist.App.Views;

public partial class ShoppingListPage : ContentPage
{
    public ShoppingListViewModel ViewModel { get; }
    public ShoppingListPage(ShoppingListViewModel viewModel)
    {
        ViewModel = viewModel; InitializeComponent(); BindingContext = viewModel;
    }
    protected override async void OnAppearing() { base.OnAppearing(); await ViewModel.RefreshCommand.ExecuteAsync(null); }
}
