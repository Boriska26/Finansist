using Finansist.App.Views;
using Finansist.Core.Finance;
using Finansist.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Finansist.App.Services;

public sealed class FinanceNavigation(IServiceProvider services) : IFinanceNavigation
{
    public Task EditSalaryAsync()
    {
        var vm = services.GetRequiredService<OperationEditorViewModel>(); vm.ConfigureSalary();
        return Shell.Current.Navigation.PushAsync(new OperationEditorPage(vm));
    }
    public Task DistributeIncomeAsync(string incomeId) => services.GetRequiredService<IBudgetNavigation>().OpenAllocationAsync(incomeId);
    public Task EditAccountAsync(Account? account)
    {
        var vm = services.GetRequiredService<AccountEditorViewModel>();
        vm.Configure(account);
        return Shell.Current.Navigation.PushAsync(new AccountEditorPage(vm));
    }

    public Task EditOperationAsync(FinanceOperation? operation)
    {
        var vm = services.GetRequiredService<OperationEditorViewModel>();
        vm.Configure(operation);
        return Shell.Current.Navigation.PushAsync(new OperationEditorPage(vm));
    }

    public async Task BackAsync() => await Shell.Current.Navigation.PopAsync();
    public Task<bool> ConfirmAsync(string title, string message) =>
        Shell.Current.DisplayAlertAsync(title, message, "Да", "Отмена");
}
