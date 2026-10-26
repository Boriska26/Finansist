using Finansist.Core.ViewModels;
using Finansist.Core.Finance;
namespace Finansist.App.Views;

public partial class FinancePage : ContentPage
{
    public FinanceViewModel ViewModel { get; }
    public ImportReviewViewModel Imports { get; }
    public FinancePage(FinanceViewModel viewModel, ImportReviewViewModel imports)
    {
        Imports = imports;
        ViewModel = viewModel; // x:Reference bindings need this during XAML initialization.
        InitializeComponent();
        BindingContext = viewModel;
    }
    private const string AccountDragKey = "FinansistAccountId";
    // MAUI 10 sends the source/target View, while older releases can send the recognizer.
    private static VisualElement? GestureView(object? sender) => sender switch
    {
        VisualElement view => view,
        GestureRecognizer { Parent: VisualElement view } => view,
        _ => null
    };
    private void OnAccountDragStarting(object? sender, DragStartingEventArgs args)
    {
        if (!ViewModel.CanReorderAccounts || GestureView(sender)?.BindingContext is not AccountBalance account)
        {
            args.Cancel = true; return;
        }
        args.Data.Properties[AccountDragKey] = account.Id;
        args.Data.Text = "Переместить счёт";
    }
    private void OnAccountDragOver(object? sender, DragEventArgs args)
    {
        args.AcceptedOperation = ViewModel.CanReorderAccounts && args.Data.Properties.ContainsKey(AccountDragKey)
            ? DataPackageOperation.Copy : DataPackageOperation.None;
    }
    private async void OnAccountDrop(object? sender, DropEventArgs args)
    {
        args.Handled = true;
        var view = GestureView(sender);
        if (!ViewModel.CanReorderAccounts || view?.BindingContext is not AccountBalance target ||
            !args.Data.Properties.TryGetValue(AccountDragKey, out var source) || source is not string sourceId) return;
        var after = args.GetPosition(view) is { } position && position.Y > view.Height / 2;
        await ViewModel.MoveAccountCommand.ExecuteAsync(new AccountMove(sourceId, target.Id, after));
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!ViewModel.AppState.IsReady) await ViewModel.AppState.InitializeCommand.ExecuteAsync(null);
        await ViewModel.RefreshCommand.ExecuteAsync(null);
        await Imports.RefreshCommand.ExecuteAsync(null);
    }
}
