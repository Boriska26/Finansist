using Finansist.Core.ViewModels;
namespace Finansist.App.Views;

public partial class ShoppingAssistantPage : ContentPage
{
    public ShoppingAssistantViewModel ViewModel { get; }
    public ShoppingAssistantPage(ShoppingAssistantViewModel viewModel)
    {
        ViewModel = viewModel; InitializeComponent(); BindingContext = viewModel;
    }
    protected override async void OnAppearing() { base.OnAppearing(); await ViewModel.RefreshCommand.ExecuteAsync(null); }
    protected override void OnDisappearing() { ViewModel.CancelWork(); base.OnDisappearing(); }
}
