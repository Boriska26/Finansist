using System.Globalization;

namespace Finansist.Core.Finance;

public enum OverviewPeriod { Day, Week, Month, Year }
public sealed record OverviewPoint(DateOnly From, DateOnly To, string Label, long IncomeMinor, long ExpenseMinor, long? BalanceMinor);
public sealed record OverviewResult(DateOnly From, DateOnly To, IReadOnlyList<OverviewPoint> Points,
    long IncomeMinor, long ExpenseMinor, long? BalanceMinor)
{
    public long DifferenceMinor => checked(IncomeMinor - ExpenseMinor);
}

public static class FinanceOverview
{
    public static (DateOnly From, DateOnly To) Range(DateOnly anchor, OverviewPeriod period)
    {
        var from = period switch
        {
            OverviewPeriod.Day => anchor,
            OverviewPeriod.Week => anchor.AddDays(-(((int)anchor.DayOfWeek + 6) % 7)),
            OverviewPeriod.Month => new(anchor.Year, anchor.Month, 1),
            OverviewPeriod.Year => new(anchor.Year, 1, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(period))
        };
        var to = period switch
        {
            OverviewPeriod.Day => from,
            OverviewPeriod.Week => from.DayNumber > DateOnly.MaxValue.DayNumber - 6 ? DateOnly.MaxValue : from.AddDays(6),
            OverviewPeriod.Month => new(from.Year, from.Month, DateTime.DaysInMonth(from.Year, from.Month)),
            _ => new DateOnly(from.Year, 12, 31)
        };
        return (from, to);
    }

    public static OverviewResult Calculate(FinanceSnapshot snapshot, DateOnly anchor, OverviewPeriod period, DateOnly today)
    {
        var (from, end) = Range(anchor, period);
        var to = end < today ? end : today;
        var operations = snapshot.Operations.Where(o => o.Date <= today && o.Kind != OperationKind.Transfer).ToArray();
        long Sum(IEnumerable<FinanceOperation> rows, OperationKind kind) => rows.Where(o => o.Kind == kind).Aggregate(0L, (sum, o) => checked(sum + o.AmountMinor));
        long? Balance(DateOnly date)
        {
            if (snapshot.Accounts.Count == 0 || snapshot.BalanceKnownFrom is not { } known || date < known) return null;
            var later = operations.Where(o => o.Date > date).ToArray();
            return checked(snapshot.TotalMinor - Sum(later, OperationKind.Income) + Sum(later, OperationKind.Expense));
        }
        var points = new List<OverviewPoint>();
        for (var cursor = from; cursor <= to;)
        {
            var pointEnd = period == OverviewPeriod.Year ? new DateOnly(cursor.Year, cursor.Month, DateTime.DaysInMonth(cursor.Year, cursor.Month)) : cursor;
            if (pointEnd > to) pointEnd = to;
            var rows = operations.Where(o => o.Date >= cursor && o.Date <= pointEnd).ToArray();
            var label = period == OverviewPeriod.Year ? cursor.ToString("MMM", CultureInfo.GetCultureInfo("ru-RU")) : cursor.ToString("dd.MM", CultureInfo.InvariantCulture);
            points.Add(new(cursor, pointEnd, label, Sum(rows, OperationKind.Income), Sum(rows, OperationKind.Expense), Balance(pointEnd)));
            if (pointEnd == to) break;
            cursor = pointEnd.AddDays(1);
        }
        var included = operations.Where(o => o.Date >= from && o.Date <= to).ToArray();
        return new(from, to, points, Sum(included, OperationKind.Income), Sum(included, OperationKind.Expense), to >= from ? Balance(to) : null);
    }
}
