using Finansist.Core.Budgeting;
using Finansist.Core.Finance;

namespace Finansist.Core.Importing;

public sealed record NotificationSource(string PackageId, string Name);
public sealed record ImportSettings(string? PackageId = null, string? SourceName = null, bool Enabled = false,
    DateTimeOffset? EnabledSince = null);
public sealed record BankPush(string PackageId, string Key, DateTimeOffset PostedAt, string Title, string Text,
    bool IsGroupSummary = false);
public sealed record ParsedBankPush(OperationKind Kind, long? AmountMinor, DateOnly Date, string? MessageTime,
    string Description, bool Ambiguous, string Notice);
public sealed record ImportCandidate(string Id, string EventKey, string Fingerprint, BankPush Push,
    ParsedBankPush Parsed, string? DefaultAccountId, bool PossibleRepeat);
public sealed record ImportRow(ImportCandidate Candidate, string Status, string? OperationId);
public sealed record ImportSnapshot(ImportSettings Settings, IReadOnlyList<ImportRow> Pending, int ProcessedCount);
public interface IBankImportStore
{
    Task<ImportSnapshot> ReadImportsAsync();
    Task<ImportSettings> ReadImportSettingsAsync();
    Task<IReadOnlySet<string>> ReadSeenImportIdsAsync();
    Task MarkImportsSeenAsync(IReadOnlyList<string> ids);
    Task ConfigureImportAsync(NotificationSource source, bool enabled);
    Task<bool> ReceivePushAsync(BankPush push);
    Task<ImportRow> ReadImportAsync(string id);
    Task DismissImportAsync(string id);
    Task<string> ConfirmImportAsync(string id, FinanceOperation operation, ExpensePurpose? purpose, string? existingOperationId = null);
}
public interface INotificationAccess
{
    bool IsGranted { get; }
    bool HasCaptureError { get; }
    Task<IReadOnlyList<NotificationSource>> GetBankAppsAsync();
    Task OpenSettingsAsync();
}

public static class ImportMatcher
{
    public static bool Matches(FinanceOperation existing, FinanceOperation proposed)
    {
        if (existing.AmountMinor != proposed.AmountMinor || Math.Abs(existing.Date.DayNumber - proposed.Date.DayNumber) > 1) return false;
        if (existing.Kind == proposed.Kind)
            return existing.AccountId == proposed.AccountId && existing.DestinationId == proposed.DestinationId;
        return existing.Kind == OperationKind.Transfer &&
            (proposed.Kind == OperationKind.Expense && existing.AccountId == proposed.AccountId ||
             proposed.Kind == OperationKind.Income && existing.DestinationId == proposed.AccountId);
    }
}
