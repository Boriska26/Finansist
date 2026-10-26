using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Finance;
using Finansist.Core.Importing;

namespace Finansist.Core.ViewModels;

public interface IImportNavigation
{
    Task OpenInboxAsync();
    Task OpenSettingsAsync();
    Task<bool> TryOpenInboxAsync();
    Task CloseInboxAsync();
    Task ReviewAsync(string candidateId);
    Task<NotificationSource?> ChooseSourceAsync(IReadOnlyList<NotificationSource> sources);
    Task<bool> ConfirmAsync(string title, string message);
}
public sealed record PendingImportRow(string Id, string Title, string Details);
public partial class ImportInboxViewModel(IBankImportStore store, IImportNavigation navigation) : FinanceScreenViewModel
{
    public ObservableCollection<PendingImportRow> Rows { get; } = [];
    [ObservableProperty] private string queueStatus = "";
    [ObservableProperty] private bool isEmpty = true;
    [RelayCommand] private Task RefreshAsync() => RunAsync(LoadAsync);
    private async Task LoadAsync()
    {
        var snapshot = await store.ReadImportsAsync();
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
    [RelayCommand] private Task ReviewAsync(PendingImportRow row) => RunAsync(() => navigation.ReviewAsync(row.Id));
    [RelayCommand] private Task DismissAsync(PendingImportRow row) => RunAsync(async () =>
    {
        if (await navigation.ConfirmAsync("Отклонить уведомление?", "В журнал ничего не добавится. Повторная доставка этого же push не вернёт его в очередь."))
        { await store.DismissImportAsync(row.Id); await LoadAsync(); }
    });
}
