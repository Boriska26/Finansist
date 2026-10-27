using Finansist.Core.ViewModels;
namespace Finansist.App.Views;

public partial class ProductPickerPage : ContentPage
{
    public ProductPickerViewModel ViewModel { get; }
    public ProductPickerPage(ProductPickerViewModel viewModel)
    {
        ViewModel = viewModel; InitializeComponent(); BindingContext = viewModel;
    }
    protected override async void OnAppearing() { base.OnAppearing(); await ViewModel.RefreshCommand.ExecuteAsync(null); }
}
