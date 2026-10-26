using Finansist.Core.ViewModels;
namespace Finansist.App.Views;

public partial class FinancePage : ContentPage
{
    public FinanceViewModel ViewModel { get; }
    public FinancePage(FinanceViewModel viewModel)
    {
        ViewModel = viewModel; // x:Reference bindings need this during XAML initialization.
        InitializeComponent();
        BindingContext = viewModel;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!ViewModel.AppState.IsReady) await ViewModel.AppState.InitializeCommand.ExecuteAsync(null);
        await ViewModel.RefreshCommand.ExecuteAsync(null);
    }
}
