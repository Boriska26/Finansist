using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Importing;

namespace Finansist.Core.ViewModels;

public partial class SettingsViewModel(AppViewModel appState, IBankImportStore imports, IImportNavigation navigation) : FinanceScreenViewModel
{
    public AppViewModel AppState { get; } = appState;
    [ObservableProperty] private string importLabel = "Банковские уведомления";
    [ObservableProperty] private string importStatus = "Настройте сбор и проверяйте найденные операции здесь.";
    [RelayCommand] private Task RefreshAsync() => RunAsync(async () =>
    {
        if (!AppState.IsReady) await AppState.InitializeCommand.ExecuteAsync(null);
        var snapshot = await imports.ReadImportsAsync();
        ImportLabel = snapshot.Pending.Count > 0 ? $"Банковские уведомления · {snapshot.Pending.Count} на проверку" : "Банковские уведомления";
        ImportStatus = snapshot.Settings.SourceName is { } source ? $"{source} · сбор {(snapshot.Settings.Enabled ? "включён" : "выключен")}" : "Выберите ВБРР Онлайн и разрешите доступ к уведомлениям.";
    });
    [RelayCommand] private Task ImportInboxAsync() => RunAsync(navigation.OpenInboxAsync);
}
