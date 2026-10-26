using Finansist.Core.ViewModels;
namespace Finansist.App.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel viewModel;
    public SettingsPage(SettingsViewModel viewModel)
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
