using Finansist.App.Views;
using Finansist.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Finansist.App.Services;

public sealed class BudgetNavigation(IServiceProvider services) : IBudgetNavigation
{
    public Task OpenAllocationAsync(string? incomeId, bool paymentsOnly = false)
    {
        var vm = services.GetRequiredService<BudgetEditorViewModel>(); vm.Configure(incomeId, paymentsOnly);
        return Shell.Current.Navigation.PushAsync(new BudgetEditorPage(vm));
    }
    public Task RecordTransferAsync(string sourceId, string destinationId, long amount)
    {
        var vm = services.GetRequiredService<OperationEditorViewModel>();
        vm.ConfigureCushionTransfer(sourceId, destinationId, amount);
        return Shell.Current.Navigation.PushAsync(new OperationEditorPage(vm));
    }
    public Task AddAccountAsync() => services.GetRequiredService<IFinanceNavigation>().EditAccountAsync(null);
    public async Task BackAsync() => await Shell.Current.Navigation.PopAsync();
    public Task<bool> ConfirmAsync(string title, string message) => Shell.Current.DisplayAlertAsync(title, message, "Да", "Отмена");
}
