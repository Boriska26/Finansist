using Finansist.Core.ViewModels;

namespace Finansist.App.Views;

public partial class BankImportSettingsPage : ContentPage
{
    private readonly BankImportSettingsViewModel viewModel;
    public BankImportSettingsPage(BankImportSettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = this.viewModel = viewModel;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await viewModel.RefreshCommand.ExecuteAsync(null);
    }
}
