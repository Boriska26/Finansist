using Finansist.App.Views;
using Finansist.Core.Finance;
using Finansist.Core.Importing;
using Finansist.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Finansist.App.Services;

public sealed class ImportNavigation(IServiceProvider services) : IImportNavigation
{
    private bool opening;
    public async Task OpenInboxAsync() => _ = await TryOpenInboxAsync();
    public async Task<bool> TryOpenInboxAsync()
    {
        var shell = Shell.Current;
        if (opening || shell is null || shell.Navigation.ModalStack.Count != 0 ||
            shell.CurrentPage is not (FinancePage or BudgetPage or SettingsPage or ShoppingPage)) return false;
        opening = true;
        try
        {
            await shell.Navigation.PushAsync(new ImportInboxPage(services.GetRequiredService<ImportInboxViewModel>()));
            return true;
        }
        finally { opening = false; }
    }
    public async Task CloseInboxAsync()
    {
        if (Shell.Current.CurrentPage is ImportInboxPage) await Shell.Current.Navigation.PopAsync();
    }
    public async Task ReviewAsync(string candidateId)
    {
        var row = await services.GetRequiredService<IBankImportStore>().ReadImportAsync(candidateId);
        if (row.Status != "pending") throw new FinanceValidationException("Уведомление уже обработано. Обновите очередь.");
        var vm = services.GetRequiredService<OperationEditorViewModel>(); vm.ConfigureImport(row.Candidate);
        await Shell.Current.Navigation.PushAsync(new OperationEditorPage(vm));
    }
    public async Task<NotificationSource?> ChooseSourceAsync(IReadOnlyList<NotificationSource> sources)
    {
        var labels = sources.Select((s, index) => sources.Count > 1 ? $"{index + 1}. {s.Name}" : s.Name).ToArray();
        var selected = await Shell.Current.DisplayActionSheetAsync("Собирать push какого приложения?", "Отмена", null, labels);
        var index = Array.IndexOf(labels, selected);
        return index >= 0 ? sources[index] : null;
    }
    public Task<bool> ConfirmAsync(string title, string message) => Shell.Current.DisplayAlertAsync(title, message, "Да", "Отмена");
}
