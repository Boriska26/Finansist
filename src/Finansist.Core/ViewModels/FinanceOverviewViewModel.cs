using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Finance;

namespace Finansist.Core.ViewModels;

public partial class FinanceOverviewViewModel(Func<DateOnly, DateOnly, Task> openOperations, Action? close = null) : FinanceScreenViewModel
{
    private FinanceSnapshot snapshot = FinanceSnapshot.Empty;
    private DateOnly anchor = DateOnly.FromDateTime(DateTime.Today);
    public IReadOnlyList<string> Periods { get; } = ["День", "Неделя", "Месяц", "Год"];
    [ObservableProperty] private int periodIndex = 2;
    [ObservableProperty] private int selectedIndex = -1;
    [ObservableProperty] private OverviewResult? result;
    public string PeriodText => Result is { } r ? $"{r.From:dd.MM.yyyy} — {r.To:dd.MM.yyyy}" : "";
    public string IncomeText => Money.Format(Result?.IncomeMinor ?? 0);
    public string ExpenseText => Money.Format(Result?.ExpenseMinor ?? 0);
    public string DifferenceText => Money.Format(Result?.DifferenceMinor ?? 0);
    public string BalanceText => Result?.BalanceMinor is { } balance ? Money.Format(balance) : "Нет данных";
    public string BalanceHint => snapshot.BalanceKnownFrom is { } known ? $"История остатка доступна с {known:dd.MM.yyyy}. Начальный остаток не является доходом." : "Добавьте счёт для истории остатка.";
    public string DetailHint => PeriodIndex == 0 ? "У операций хранится только дата: за день показан общий итог." : "Нажмите на график, чтобы посмотреть суммы и операции.";
    public string SelectionText => Selected is { } p ? $"{p.From:dd.MM.yyyy} — {p.To:dd.MM.yyyy}\nДоходы: {Money.Format(p.IncomeMinor)}\nРасходы: {Money.Format(p.ExpenseMinor)}\nОстаток: {(p.BalanceMinor is { } b ? Money.Format(b) : "нет данных")}" : "";
    public bool HasSelection => Selected is not null;
    public bool NoOperations => Result is { IncomeMinor: 0, ExpenseMinor: 0 };
    private OverviewPoint? Selected => Result is { } r && SelectedIndex >= 0 && SelectedIndex < r.Points.Count ? r.Points[SelectedIndex] : null;
    public bool CanGoNext => FinanceOverview.Range(anchor, (OverviewPeriod)PeriodIndex).To < DateOnly.FromDateTime(DateTime.Today);
    public bool CanGoPrevious => FinanceOverview.Range(anchor, (OverviewPeriod)PeriodIndex).From.Year > 1;
    partial void OnPeriodIndexChanged(int value) { if (value is < 0 or > 3) { PeriodIndex = 2; return; } Rebuild(); }
    partial void OnSelectedIndexChanged(int value) { OnPropertyChanged(nameof(SelectionText)); OnPropertyChanged(nameof(HasSelection)); }
    public void SetSnapshot(FinanceSnapshot value) { snapshot = value; Rebuild(); }
    private void Rebuild()
    {
        try
        {
            Result = FinanceOverview.Calculate(snapshot, anchor, (OverviewPeriod)PeriodIndex, DateOnly.FromDateTime(DateTime.Today));
            ErrorMessage = "";
        }
        catch (OverflowException)
        {
            Result = null;
            ErrorMessage = "Суммы слишком велики для расчёта обзора. Проверьте записи.";
        }
        SelectedIndex = (Result?.Points.Count ?? 0) - 1;
        foreach (var name in new[] { nameof(PeriodText), nameof(IncomeText), nameof(ExpenseText), nameof(DifferenceText), nameof(BalanceText), nameof(BalanceHint), nameof(DetailHint), nameof(SelectionText), nameof(HasSelection), nameof(NoOperations), nameof(CanGoNext), nameof(CanGoPrevious) }) OnPropertyChanged(name);
        PreviousCommand.NotifyCanExecuteChanged(); NextCommand.NotifyCanExecuteChanged();
    }
    private void Move(int direction)
    {
        anchor = (OverviewPeriod)PeriodIndex switch
        {
            OverviewPeriod.Day => anchor.AddDays(direction), OverviewPeriod.Week => anchor.AddDays(7 * direction),
            OverviewPeriod.Month => anchor.AddMonths(direction), _ => anchor.AddYears(direction)
        };
        if (anchor > DateOnly.FromDateTime(DateTime.Today)) anchor = DateOnly.FromDateTime(DateTime.Today);
        Rebuild();
    }
    [RelayCommand(CanExecute = nameof(CanGoPrevious))] private void Previous() => Move(-1);
    [RelayCommand(CanExecute = nameof(CanGoNext))] private void Next() => Move(1);
    [RelayCommand] private void Current() { anchor = DateOnly.FromDateTime(DateTime.Today); Rebuild(); }
    [RelayCommand] private void Close() => close?.Invoke();
    [RelayCommand] private Task OpenOperationsAsync() => Selected is { } p ? openOperations(p.From, p.To) : Task.CompletedTask;
}
