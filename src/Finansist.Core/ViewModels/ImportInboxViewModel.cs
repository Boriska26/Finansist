using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Finance;
using Finansist.Core.Importing;

namespace Finansist.Core.ViewModels;

public interface IImportNavigation
{
    Task OpenInboxAsync();
    Task<bool> TryOpenInboxAsync();
    Task CloseInboxAsync();
    Task ReviewAsync(string candidateId);
    Task<NotificationSource?> ChooseSourceAsync(IReadOnlyList<NotificationSource> sources);
    Task<bool> ConfirmAsync(string title, string message);
}
public sealed record PendingImportRow(string Id, string Title, string Details);
public partial class ImportInboxViewModel(IBankImportStore store, INotificationAccess access, IImportNavigation navigation) : FinanceScreenViewModel
{
    private ImportSettings settings = new();
    public ObservableCollection<PendingImportRow> Rows { get; } = [];
    [ObservableProperty] private string sourceStatus = "Выберите приложение ВБРР Онлайн.";
    [ObservableProperty] private string accessStatus = "";
    [ObservableProperty] private string toggleLabel = "Включить сбор";
    [ObservableProperty] private string queueStatus = "";
    [ObservableProperty] private bool isEmpty = true;
    [ObservableProperty] private bool needsAccess;
    [ObservableProperty] private bool hasSource;
    [RelayCommand] private Task RefreshAsync() => RunAsync(LoadAsync);
    private async Task LoadAsync()
    {
        var snapshot = await store.ReadImportsAsync(); settings = snapshot.Settings;
        HasSource = settings.PackageId is not null;
        SourceStatus = settings.SourceName is null ? "Выберите приложение ВБРР Онлайн." : $"Источник: {settings.SourceName}\nСбор {(settings.Enabled ? "включён" : "выключен")}.";
        NeedsAccess = !access.IsGranted;
        AccessStatus = NeedsAccess ? "Доступ Android к уведомлениям пока не разрешён." : "Доступ Android к уведомлениям разрешён.";
        if (access.HasCaptureError) AccessStatus += "\nВо время сбора была ошибка. Проверьте свободное место и доступ; часть push могла не сохраниться.";
        ToggleLabel = settings.Enabled ? "Приостановить сбор" : "Включить сбор";
        Rows.Clear();
        foreach (var row in snapshot.Pending)
        {
            var item = row.Candidate;
            var amount = item.Parsed.AmountMinor is { } value ? Money.Format(value) : "сумма требует уточнения";
            Rows.Add(new(item.Id, item.Parsed.Description + " · " + amount,
                $"{item.Push.PostedAt.ToLocalTime():dd.MM.yyyy HH:mm}\n{(item.Parsed.Ambiguous ? "Нужно выбрать: расход или перевод." : "Ожидает проверки.")}{(item.PossibleRepeat ? "\nПохожее уведомление уже встречалось — проверьте дубль." : "")}"));
        }
        IsEmpty = Rows.Count == 0;
        QueueStatus = $"Ожидают проверки: {Rows.Count}. Обработано: {snapshot.ProcessedCount}.";
        await store.MarkImportsSeenAsync(snapshot.Pending.Select(row => row.Candidate.Id).ToArray());
    }
    [RelayCommand] private Task LaterAsync() => RunAsync(navigation.CloseInboxAsync);
    [RelayCommand] private Task ChooseSourceAsync() => RunAsync(async () =>
    {
        var apps = await access.GetBankAppsAsync();
        if (apps.Count == 0) throw new FinanceValidationException("ВБРР Онлайн не найден среди установленных приложений. Проверьте установку банка в этом профиле Android.");
        var source = await navigation.ChooseSourceAsync(apps);
        if (source is null) return;
        await store.ConfigureImportAsync(source, true); await LoadAsync();
    });
    [RelayCommand] private Task ToggleAsync() => RunAsync(async () =>
    {
        if (settings.PackageId is null) throw new FinanceValidationException("Сначала выберите ВБРР Онлайн.");
        await store.ConfigureImportAsync(new(settings.PackageId, settings.SourceName!), !settings.Enabled); await LoadAsync();
    });
    [RelayCommand] private Task GrantAccessAsync() => RunAsync(access.OpenSettingsAsync);
    [RelayCommand] private Task ReviewAsync(PendingImportRow row) => RunAsync(() => navigation.ReviewAsync(row.Id));
    [RelayCommand] private Task DismissAsync(PendingImportRow row) => RunAsync(async () =>
    {
        if (await navigation.ConfirmAsync("Отклонить уведомление?", "В журнал ничего не добавится. Повторная доставка этого же push не вернёт его в очередь."))
        { await store.DismissImportAsync(row.Id); await LoadAsync(); }
    });
}
