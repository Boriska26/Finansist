using Finansist.Core.ViewModels;
namespace Finansist.App.Views;

public partial class ShoppingPage : ContentPage
{
    public ShoppingViewModel ViewModel { get; }
    public ShoppingPage(ShoppingViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent(); BindingContext = viewModel;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing(); await ViewModel.RefreshCommand.ExecuteAsync(null);
    }
}
