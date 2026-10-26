using Finansist.Core.ViewModels;
namespace Finansist.App.Views;
public partial class ImportInboxPage : ContentPage
{
    public ImportInboxViewModel ViewModel { get; }
    public ImportInboxPage(ImportInboxViewModel viewModel) { ViewModel = viewModel; InitializeComponent(); BindingContext = viewModel; }
    protected override async void OnAppearing() { base.OnAppearing(); await ViewModel.RefreshCommand.ExecuteAsync(null); }
}
