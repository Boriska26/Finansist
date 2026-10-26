using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Importing;

namespace Finansist.Core.ViewModels;

// Shared entry point for the finance footer and foreground checks, never confirms operations.
public partial class ImportReviewViewModel(IBankImportStore store, IImportNavigation navigation) : FinanceScreenViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private int pendingCount;
    public bool HasPending => PendingCount > 0;
    public string ButtonText => $"Проверить операции банка · {PendingCount}";
    private bool autoRequested;
    private async Task LoadAsync()
    {
        do
        {
            var autoOpen = autoRequested;
            autoRequested = false;
            var snapshot = await store.ReadImportsAsync();
            PendingCount = snapshot.Pending.Count;
            if (autoOpen && HasPending)
            {
                var seen = await store.ReadSeenImportIdsAsync();
                if (snapshot.Pending.Any(row => !seen.Contains(row.Candidate.Id)))
                    await navigation.TryOpenInboxAsync();
            }
            // Foreground checks arriving during a footer refresh are queued, not dropped.
        } while (autoRequested);
    }
    [RelayCommand] private Task RefreshAsync() => RunAsync(LoadAsync);
    [RelayCommand] private Task CheckNewAsync()
    {
        autoRequested = true;
        return RunAsync(LoadAsync);
    }
    [RelayCommand] private Task OpenInboxAsync() => RunAsync(navigation.OpenInboxAsync);
}
