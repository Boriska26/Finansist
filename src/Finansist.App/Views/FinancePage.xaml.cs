using Finansist.Core.ViewModels;
using Finansist.Core.Finance;
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
    private const string AccountDragKey = "FinansistAccountId";
    private void OnAccountDragStarting(object? sender, DragStartingEventArgs args)
    {
        if (!ViewModel.CanReorderAccounts || sender is not DragGestureRecognizer { BindingContext: AccountBalance account })
        {
            args.Cancel = true; return;
        }
        args.Data.Properties[AccountDragKey] = account.Id;
    }
    private void OnAccountDragOver(object? sender, DragEventArgs args)
    {
        args.AcceptedOperation = ViewModel.CanReorderAccounts && args.Data.Properties.ContainsKey(AccountDragKey)
            ? DataPackageOperation.Copy : DataPackageOperation.None;
    }
    private async void OnAccountDrop(object? sender, DropEventArgs args)
    {
        args.Handled = true;
        if (!ViewModel.CanReorderAccounts || sender is not DropGestureRecognizer { BindingContext: AccountBalance target } drop ||
            !args.Data.Properties.TryGetValue(AccountDragKey, out var source) || source is not string sourceId) return;
        var after = drop.Parent is VisualElement view && args.GetPosition(view) is { } position && position.Y > view.Height / 2;
        await ViewModel.MoveAccountCommand.ExecuteAsync(new AccountMove(sourceId, target.Id, after));
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!ViewModel.AppState.IsReady) await ViewModel.AppState.InitializeCommand.ExecuteAsync(null);
        await ViewModel.RefreshCommand.ExecuteAsync(null);
    }
}
