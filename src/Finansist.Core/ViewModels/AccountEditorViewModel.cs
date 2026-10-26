using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Finance;

namespace Finansist.Core.ViewModels;

public partial class AccountEditorViewModel(IFinanceStore store, IFinanceNavigation navigation) : FinanceScreenViewModel
{
    private string id = Guid.NewGuid().ToString();
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string openingAmount = "0";
    [ObservableProperty] private bool isExisting;
    [ObservableProperty] private bool isSaved;
    public string Title => IsExisting ? "Изменить счёт" : "Новый счёт";
    public void Configure(Account? account)
    {
        IsExisting = account is not null;
        if (account is not null) { id = account.Id; Name = account.Name; OpeningAmount = Money.Input(account.OpeningMinor); }
        OnPropertyChanged(nameof(Title));
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        if (IsSaved) return;
        await store.SaveAccountAsync(new Account(id, Name, Money.Parse(OpeningAmount, allowNegative: true)));
        IsSaved = true;
        await navigation.BackAsync();
    });

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (!IsExisting || IsSaved) return;
        if (!await navigation.ConfirmAsync("Удалить счёт?", "Счёт и его начальный остаток будут удалены. Счёт с операциями удалить нельзя.")) return;
        await store.DeleteAccountAsync(id);
        IsSaved = true;
        await navigation.BackAsync();
    });
    [RelayCommand]
    private Task CancelAsync() => RunAsync(async () =>
    {
        if (IsSaved || await navigation.ConfirmAsync("Закрыть форму?", "Несохранённые изменения будут потеряны."))
            await navigation.BackAsync();
    });

}
