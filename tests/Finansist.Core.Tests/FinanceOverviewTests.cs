using Finansist.Core.Finance;
using Finansist.Core.ViewModels;

namespace Finansist.Core.Tests;

public sealed class FinanceOverviewTests
{
    private static readonly DateOnly Today = new(2025, 3, 15);
    private static readonly Account Card = new("card", "Тестовая карта", 100_000);
    private static readonly Account Cushion = new("cushion", "Тестовая подушка", 0);
    private static FinanceOperation Op(OperationKind kind, long amount, DateOnly date) => new(Guid.NewGuid().ToString(), kind, Card.Id,
        kind == OperationKind.Transfer ? Cushion.Id : null, amount, date, "Синтетика", "");
    private static FinanceSnapshot Snapshot(params FinanceOperation[] operations) => Ledger.Calculate([Card, Cushion], operations) with { BalanceKnownFrom = new DateOnly(2025, 1, 1) };

    [Fact]
    public void TotalsExcludeTransfersAndOpeningBalanceAndRecoverDailyClosingBalances()
    {
        var snapshot = Snapshot(Op(OperationKind.Income, 20_000, Today.AddDays(-2)), Op(OperationKind.Expense, 5_000, Today.AddDays(-1)), Op(OperationKind.Transfer, 8_000, Today));
        var result = FinanceOverview.Calculate(snapshot, Today, OverviewPeriod.Month, Today);
        Assert.Equal(20_000, result.IncomeMinor); Assert.Equal(5_000, result.ExpenseMinor); Assert.Equal(15_000, result.DifferenceMinor);
        Assert.Equal(115_000, result.BalanceMinor);
        Assert.Equal(100_000, result.Points[0].BalanceMinor);
        Assert.Equal(120_000, result.Points[12].BalanceMinor);
        Assert.Equal(115_000, result.Points[13].BalanceMinor);
        Assert.Equal(115_000, result.Points[14].BalanceMinor);
        Assert.Equal(0, result.Points[14].IncomeMinor); Assert.Equal(0, result.Points[14].ExpenseMinor);
        Assert.Equal(snapshot.TotalMinor, result.BalanceMinor);
    }

    [Fact]
    public void MissingBaselineProducesGapsInsteadOfInventedBalances()
    {
        var snapshot = Snapshot(Op(OperationKind.Income, 100, Today.AddDays(-2))) with { BalanceKnownFrom = Today };
        var result = FinanceOverview.Calculate(snapshot, Today, OverviewPeriod.Month, Today);
        Assert.All(result.Points.Take(14), p => Assert.Null(p.BalanceMinor));
        Assert.Equal(snapshot.TotalMinor, result.Points.Last().BalanceMinor);
        var past = FinanceOverview.Calculate(snapshot, Today.AddMonths(-1), OverviewPeriod.Month, Today);
        Assert.Null(past.BalanceMinor);
        var unknown = FinanceOverview.Calculate(snapshot with { BalanceKnownFrom = null }, Today, OverviewPeriod.Day, Today);
        Assert.Null(unknown.BalanceMinor);
    }

    [Fact]
    public void WeekStartsMondayAndIncludesBothBoundaryDatesAcrossYear()
    {
        var anchor = new DateOnly(2025, 1, 1);
        var from = new DateOnly(2024, 12, 30); var to = new DateOnly(2025, 1, 5);
        var result = FinanceOverview.Calculate(Snapshot(Op(OperationKind.Income, 10, from), Op(OperationKind.Expense, 4, to), Op(OperationKind.Income, 90, from.AddDays(-1)), Op(OperationKind.Expense, 80, to.AddDays(1))), anchor, OverviewPeriod.Week, Today);
        Assert.Equal(from, result.From); Assert.Equal(to, result.To); Assert.Equal(7, result.Points.Count);
        Assert.Equal(10, result.IncomeMinor); Assert.Equal(4, result.ExpenseMinor);
        Assert.Equal(100_096, result.BalanceMinor); // Later expense must be added back to recover this closing balance.
    }

    [Fact]
    public void LeapFebruaryAndYearUseCalendarBoundariesAndMonthlyBuckets()
    {
        var day = new DateOnly(2024, 2, 29);
        var snapshot = Snapshot(Op(OperationKind.Expense, 700, day)) with { BalanceKnownFrom = new DateOnly(2024, 1, 1) };
        var month = FinanceOverview.Calculate(snapshot, day, OverviewPeriod.Month, Today);
        Assert.Equal(29, month.Points.Count); Assert.Equal(700, month.ExpenseMinor);
        var year = FinanceOverview.Calculate(snapshot, day, OverviewPeriod.Year, Today);
        Assert.Equal(12, year.Points.Count); Assert.Equal(day, year.Points[1].To); Assert.Equal(700, year.Points[1].ExpenseMinor);
        Assert.Equal(new DateOnly(2024, 12, 31), year.To);
    }

    [Fact]
    public void CurrentPeriodEndsTodayAndSingleDayHasOneAggregate()
    {
        var snapshot = Snapshot(Op(OperationKind.Income, 100, Today), Op(OperationKind.Expense, 200, Today));
        var month = FinanceOverview.Calculate(snapshot, Today, OverviewPeriod.Month, Today);
        Assert.Equal(Today, month.To); Assert.Equal(15, month.Points.Count);
        var day = FinanceOverview.Calculate(snapshot, Today, OverviewPeriod.Day, Today);
        Assert.Single(day.Points); Assert.Equal(-100, day.DifferenceMinor);
        Assert.Equal(99_900, day.BalanceMinor);
    }

    [Fact]
    public void EmptyLedgerHasZeroFlowsAndUnknownBalance_NegativeBalancesArePreserved()
    {
        var empty = FinanceOverview.Calculate(FinanceSnapshot.Empty, Today, OverviewPeriod.Day, Today);
        Assert.Equal(0, empty.IncomeMinor); Assert.Equal(0, empty.ExpenseMinor); Assert.Null(empty.BalanceMinor);
        var negative = FinanceOverview.Calculate(Snapshot(Op(OperationKind.Expense, 150_000, Today)), Today, OverviewPeriod.Day, Today);
        Assert.Equal(-50_000, negative.BalanceMinor);
    }

    [Fact]
    public void EditedAndRemovedOperationsRecalculateInsteadOfAccumulating()
    {
        var operation = Op(OperationKind.Income, 200, Today);
        var first = FinanceOverview.Calculate(Snapshot(operation), Today, OverviewPeriod.Day, Today);
        var edited = FinanceOverview.Calculate(Snapshot(operation with { AmountMinor = 300 }), Today, OverviewPeriod.Day, Today);
        var deleted = FinanceOverview.Calculate(Snapshot(), Today, OverviewPeriod.Day, Today);
        Assert.Equal(200, first.IncomeMinor); Assert.Equal(300, edited.IncomeMinor); Assert.Equal(0, deleted.IncomeMinor);
        Assert.Equal(100_000, deleted.BalanceMinor);
    }

    [Fact]
    public void OverflowIsRejected()
    {
        var snapshot = new FinanceSnapshot([], [Op(OperationKind.Income, long.MaxValue, Today), Op(OperationKind.Income, 1, Today)]);
        Assert.Throws<OverflowException>(() => FinanceOverview.Calculate(snapshot, Today, OverviewPeriod.Day, Today));
    }

    [Fact]
    public void ViewModelShowsCalculationErrorsAndRecoversWithoutCrashing()
    {
        var vm = new FinanceOverviewViewModel((_, _) => Task.CompletedTask);
        var today = DateOnly.FromDateTime(DateTime.Today);
        vm.SetSnapshot(new FinanceSnapshot([], [Op(OperationKind.Income, long.MaxValue, today), Op(OperationKind.Income, 1, today)]));
        Assert.True(vm.HasError); Assert.Null(vm.Result); Assert.False(vm.HasSelection);
        vm.SetSnapshot(FinanceSnapshot.Empty);
        Assert.False(vm.HasError); Assert.NotNull(vm.Result);
        vm.PeriodIndex = -1; Assert.Equal(2, vm.PeriodIndex);
    }

    [Fact]
    public async Task ViewModelNavigatesSelectsDatesAndReturnsToCurrentPeriod()
    {
        DateOnly? openedFrom = null, openedTo = null;
        var vm = new FinanceOverviewViewModel((from, to) => { openedFrom = from; openedTo = to; return Task.CompletedTask; });
        var today = DateOnly.FromDateTime(DateTime.Today);
        vm.SetSnapshot(Snapshot() with { BalanceKnownFrom = today });
        Assert.False(vm.CanGoNext); Assert.True(vm.HasSelection);
        vm.PeriodIndex = 0;
        Assert.Single(vm.Result!.Points);
        vm.PreviousCommand.Execute(null); Assert.Equal(today.AddDays(-1), vm.Result!.From); Assert.True(vm.CanGoNext);
        await vm.OpenOperationsCommand.ExecuteAsync(null);
        Assert.Equal(today.AddDays(-1), openedFrom); Assert.Equal(openedFrom, openedTo);
        vm.CurrentCommand.Execute(null); Assert.Equal(today, vm.Result!.To); Assert.False(vm.CanGoNext);
        vm.SelectedIndex = -1; Assert.False(vm.HasSelection);
    }
}
