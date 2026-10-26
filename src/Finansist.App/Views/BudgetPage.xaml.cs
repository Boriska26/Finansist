using Finansist.Core.ViewModels;
namespace Finansist.App.Views;
public partial class BudgetPage : ContentPage
{
    public BudgetViewModel ViewModel { get; }
    public BudgetPage(BudgetViewModel viewModel) { ViewModel = viewModel; InitializeComponent(); BindingContext = viewModel; }
    protected override async void OnAppearing() { base.OnAppearing(); await ViewModel.RefreshCommand.ExecuteAsync(null); }
}
