using Finansist.Core.ViewModels;
namespace Finansist.App.Views;
public partial class ShoppingItemEditorPage : ContentPage
{
    private readonly ShoppingItemEditorViewModel viewModel;
    public ShoppingItemEditorPage(ShoppingItemEditorViewModel viewModel) { InitializeComponent(); BindingContext = this.viewModel = viewModel; }
    protected override async void OnAppearing() { base.OnAppearing(); await viewModel.LoadCommand.ExecuteAsync(null); }
}
