using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Importing;

namespace Finansist.Core.ViewModels;

public partial class SettingsViewModel(AppViewModel appState, IBankImportStore imports, IImportNavigation navigation) : FinanceScreenViewModel
{
    public AppViewModel AppState { get; } = appState;
    public string ImportLabel => "Настроить сбор";
    [ObservableProperty] private string importStatus = "Выберите банк и разрешите доступ к его уведомлениям.";
    [RelayCommand] private Task RefreshAsync() => RunAsync(async () =>
    {
        if (!AppState.IsReady) await AppState.InitializeCommand.ExecuteAsync(null);
        var settings = await imports.ReadImportSettingsAsync();
        ImportStatus = settings.SourceName is { } source ? $"{source} · сбор {(settings.Enabled ? "включён" : "выключен")}" : "Выберите ВБРР Онлайн и разрешите доступ к уведомлениям.";
    });
    [RelayCommand] private Task ImportSettingsAsync() => RunAsync(navigation.OpenSettingsAsync);
}
