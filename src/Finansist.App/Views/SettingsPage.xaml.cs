using Finansist.Core.ViewModels;
namespace Finansist.App.Views;

public partial class SettingsPage : ContentPage
{
    private readonly AppViewModel viewModel;
    public SettingsPage(AppViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = this.viewModel = viewModel;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!viewModel.IsReady) await viewModel.InitializeCommand.ExecuteAsync(null);
    }
}
