using Finansist.Core.ViewModels;

namespace Finansist.App.Views;

public partial class AccountEditorPage : ContentPage
{
    private readonly AccountEditorViewModel viewModel;
    public AccountEditorPage(AccountEditorViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = this.viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    protected override void OnDisappearing()
    {
        viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnDisappearing();
    }

    private async void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(viewModel.ErrorMessage) && viewModel.HasError)
            await FormScroll.ScrollToAsync(0, 0, true);
    }

    protected override bool OnBackButtonPressed()
    {
        if (!viewModel.IsBusy) _ = viewModel.CancelCommand.ExecuteAsync(null);
        return true;
    }
}
