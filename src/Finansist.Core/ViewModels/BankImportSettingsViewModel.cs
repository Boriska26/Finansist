using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Finance;
using Finansist.Core.Importing;

namespace Finansist.Core.ViewModels;

public partial class BankImportSettingsViewModel(IBankImportStore store, INotificationAccess access, IImportNavigation navigation) : FinanceScreenViewModel
{
    private ImportSettings settings = new();
    [ObservableProperty] private string sourceStatus = "Выберите приложение ВБРР Онлайн.";
    [ObservableProperty] private string accessStatus = "";
    [ObservableProperty] private string toggleLabel = "Включить сбор";
    [ObservableProperty] private bool needsAccess;
    [ObservableProperty] private bool hasSource;
    [RelayCommand] private Task RefreshAsync() => RunAsync(LoadAsync);
    private async Task LoadAsync()
    {
        settings = await store.ReadImportSettingsAsync();
        HasSource = settings.PackageId is not null;
        SourceStatus = settings.SourceName is null ? "Выберите приложение ВБРР Онлайн." : $"Источник: {settings.SourceName}\nСбор {(settings.Enabled ? "включён" : "выключен")}.";
        NeedsAccess = !access.IsGranted;
        AccessStatus = NeedsAccess ? "Доступ Android к уведомлениям пока не разрешён." : "Доступ Android к уведомлениям разрешён.";
        if (access.HasCaptureError) AccessStatus += "\nВо время сбора была ошибка. Проверьте свободное место и доступ; часть push могла не сохраниться.";
        ToggleLabel = settings.Enabled ? "Приостановить сбор" : "Включить сбор";
    }
    [RelayCommand] private Task ChooseSourceAsync() => RunAsync(async () =>
    {
        var apps = await access.GetBankAppsAsync();
        if (apps.Count == 0) throw new FinanceValidationException("ВБРР Онлайн не найден среди установленных приложений. Проверьте установку банка в этом профиле Android.");
        var source = await navigation.ChooseSourceAsync(apps);
        if (source is null) return;
        await store.ConfigureImportAsync(source, true);
        await LoadAsync();
    });
    [RelayCommand] private Task ToggleAsync() => RunAsync(async () =>
    {
        if (settings.PackageId is null) throw new FinanceValidationException("Сначала выберите ВБРР Онлайн.");
        await store.ConfigureImportAsync(new(settings.PackageId, settings.SourceName!), !settings.Enabled);
        await LoadAsync();
    });
    [RelayCommand] private Task GrantAccessAsync() => RunAsync(access.OpenSettingsAsync);
}
