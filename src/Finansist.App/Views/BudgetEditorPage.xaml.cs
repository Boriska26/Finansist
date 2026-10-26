using Finansist.Core.ViewModels;
namespace Finansist.App.Views;
public partial class BudgetEditorPage : ContentPage
{
    public BudgetEditorViewModel ViewModel { get; }
    private BudgetEditorViewModel viewModel => ViewModel;
    public BudgetEditorPage(BudgetEditorViewModel viewModel) { ViewModel = viewModel; InitializeComponent(); BindingContext = viewModel; }
    protected override async void OnAppearing()
    {
        base.OnAppearing(); viewModel.PropertyChanged += Changed;
        await viewModel.LoadCommand.ExecuteAsync(null);
    }
    protected override void OnDisappearing() { viewModel.PropertyChanged -= Changed; base.OnDisappearing(); }
    private async void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(viewModel.ErrorMessage) && viewModel.HasError) await FormScroll.ScrollToAsync(0, 0, true);
    }
    protected override bool OnBackButtonPressed() { if (!viewModel.IsBusy) _ = viewModel.CancelCommand.ExecuteAsync(null); return true; }
}
