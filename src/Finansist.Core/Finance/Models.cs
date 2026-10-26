using System.Globalization;

namespace Finansist.Core.Finance;

public enum OperationKind { Expense, Income, Transfer }

public sealed record Account(string Id, string Name, long OpeningMinor)
{
    public string DisplayName => Name;
}

public sealed record FinanceOperation(string Id, OperationKind Kind, string AccountId,
    string? DestinationId, long AmountMinor, DateOnly Date, string Category, string Note);

public sealed record AccountBalance(Account Account, long BalanceMinor)
{
    public string Id => Account.Id;
    public string Name => Account.Name;
    public string BalanceText => Money.Format(BalanceMinor);
    public string OpeningText => $"Начальный остаток: {Money.Format(Account.OpeningMinor)}";
}

public sealed record FinanceSnapshot(IReadOnlyList<AccountBalance> Accounts, IReadOnlyList<FinanceOperation> Operations)
{
    public long TotalMinor => Accounts.Aggregate(0L, (sum, account) => checked(sum + account.BalanceMinor));
    public static FinanceSnapshot Empty { get; } = new([], []);
}

public sealed class FinanceValidationException(string message) : Exception(message);

public static class Money
{
    public const long MaximumMinor = 100_000_000_000_000;
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    public static string Format(long minor) => (minor / 100m).ToString("N2", Russian) + " ₽";
    public static string Input(long minor) => (minor / 100m).ToString("0.00", Russian);

    public static long Parse(string? input, bool allowNegative = false)
    {
        var text = input?.Trim().Replace(',', '.') ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"^-?[0-9]+(?:\.[0-9]{1,2})?$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ||
            !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount) ||
            amount > MaximumMinor / 100m || amount < -MaximumMinor / 100m)
            throw new FinanceValidationException("Введите сумму цифрами, не более двух знаков после запятой (до 1 трлн ₽).");
        if (!allowNegative && amount <= 0)
            throw new FinanceValidationException("Сумма операции должна быть больше нуля.");
        return (long)(amount * 100m);
    }
}

public static class Ledger
{
    public static FinanceSnapshot Calculate(IReadOnlyList<Account> accounts, IReadOnlyList<FinanceOperation> operations)
    {
        var balances = accounts.ToDictionary(account => account.Id, account => account.OpeningMinor);
        foreach (var operation in operations)
        {
            var sign = operation.Kind == OperationKind.Income ? 1L : -1L;
            balances[operation.AccountId] = checked(balances[operation.AccountId] + sign * operation.AmountMinor);
            if (operation.Kind == OperationKind.Transfer)
                balances[operation.DestinationId!] = checked(balances[operation.DestinationId!] + operation.AmountMinor);
        }
        var snapshot = new FinanceSnapshot(accounts.Select(account => new AccountBalance(account, balances[account.Id])).ToArray(), operations);
        _ = snapshot.TotalMinor; // Reject totals outside Int64 before committing a mutation.
        return snapshot;
    }
}
