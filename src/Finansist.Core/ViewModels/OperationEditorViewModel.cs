using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Finance;
using Finansist.Core.Budgeting;

namespace Finansist.Core.ViewModels;

public sealed record ExpenseChoice(string? BillId, bool Necessary, string Label);

public partial class OperationEditorViewModel(IFinanceStore store, IFinanceNavigation navigation, IBudgetStore? budgetStore = null) : FinanceScreenViewModel
{
    private string id = Guid.NewGuid().ToString();
    private FinanceOperation? original;
    public ObservableCollection<ExpenseChoice> ExpenseChoices { get; } = [];
    [ObservableProperty] private ExpenseChoice? expenseChoice;
    [ObservableProperty] private bool distributeIncome;
    public bool IsIncome => KindIndex == (int)OperationKind.Income;
    public bool IsExpense => KindIndex == (int)OperationKind.Expense;
    public bool HasBudgetChoices => IsExpense && budgetStore is not null;
    public void ConfigureSalary() { Configure(null); KindIndex = (int)OperationKind.Income; Category = "Зарплата / аванс"; DistributeIncome = true; }
    public void ConfigureCushionTransfer(string from, string to, long amount)
    {
        Configure(new FinanceOperation(id, OperationKind.Transfer, from, to, amount, DateOnly.FromDateTime(DateTime.Today), "Перевод", "В подушку"));
        IsExisting = false; OnPropertyChanged(nameof(Title));
    }
    [ObservableProperty] private int kindIndex;
    [ObservableProperty] private Account? selectedAccount;
    [ObservableProperty] private Account? destinationAccount;
    [ObservableProperty] private string amount = "";
    [ObservableProperty] private DateTime? date = DateTime.Today;
    [ObservableProperty] private string category = "Прочее";
    [ObservableProperty] private string note = "";
    [ObservableProperty] private bool isExisting;
    [ObservableProperty] private bool isLoaded;
    [ObservableProperty] private bool isSaved;
    public ObservableCollection<Account> Accounts { get; } = [];
    public IReadOnlyList<string> Kinds { get; } = ["Расход", "Доход", "Перевод"];
    public DateTime Today => DateTime.Today;
    public bool IsTransfer => KindIndex == (int)OperationKind.Transfer;
    public bool IsNotTransfer => !IsTransfer;
    public string AccountLabel => IsTransfer ? "Со счёта" : "Счёт";
    public string Title => IsExisting ? "Изменить операцию" : "Новая операция";
    partial void OnKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsIncome)); OnPropertyChanged(nameof(IsExpense)); OnPropertyChanged(nameof(HasBudgetChoices)); OnPropertyChanged(nameof(IsTransfer)); OnPropertyChanged(nameof(IsNotTransfer)); OnPropertyChanged(nameof(AccountLabel));
    }
    public void Configure(FinanceOperation? operation)
    {
        original = operation;
        IsExisting = operation is not null;
        if (operation is not null)
        {
            id = operation.Id; KindIndex = (int)operation.Kind; Amount = Money.Input(operation.AmountMinor);
            Date = operation.Date.ToDateTime(TimeOnly.MinValue); Category = operation.Category; Note = operation.Note;
        }
        OnPropertyChanged(nameof(Title));
    }

    [RelayCommand]
    private Task LoadAsync() => RunAsync(async () =>
    {
        if (IsLoaded) return;
        var snapshot = await store.ReadFinanceAsync();
        Accounts.Clear();
        foreach (var account in snapshot.Accounts) Accounts.Add(account.Account);
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == original?.AccountId) ?? Accounts.FirstOrDefault();
        DestinationAccount = Accounts.FirstOrDefault(a => a.Id == original?.DestinationId) ?? Accounts.Skip(1).FirstOrDefault();
        if (budgetStore is not null)
        {
            var budget = await budgetStore.ReadBudgetAsync();
            ExpenseChoices.Clear();
            ExpenseChoices.Add(new(null, false, "На себя"));
            ExpenseChoices.Add(new(null, true, "Необходимое: продукты, транспорт и прочее"));
            foreach (var bill in budget.Document.Bills.Where(b => !b.Settled).OrderBy(b => b.Due))
                ExpenseChoices.Add(new(bill.Id, true, $"{bill.Name} · {bill.Due:dd.MM.yyyy}"));
            var purpose = budget.Document.Purposes.FirstOrDefault(p => p.OperationId == id);
            ExpenseChoice = ExpenseChoices.FirstOrDefault(c => c.BillId == purpose?.BillId && c.Necessary == (purpose?.Necessary ?? false)) ?? ExpenseChoices[0];
        }
        IsLoaded = true;
    });

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        if (IsSaved) return;
        if (!IsLoaded || SelectedAccount is null) throw new FinanceValidationException("Выберите счёт. Если список не загрузился, нажмите «Повторить загрузку».");
        if (Date is null) throw new FinanceValidationException("Выберите дату операции.");
        var operation = new FinanceOperation(id, (OperationKind)KindIndex, SelectedAccount.Id,
            IsTransfer ? DestinationAccount?.Id : null, Money.Parse(Amount), DateOnly.FromDateTime(Date.Value), Category, Note);
        if (budgetStore is null) await store.SaveOperationAsync(operation);
        else await budgetStore.SaveBudgetOperationAsync(operation, IsExpense && ExpenseChoice is { } choice
            ? new ExpensePurpose(id, choice.Necessary, choice.BillId) : null);
        IsSaved = true;
        await navigation.BackAsync();
        if (IsIncome && DistributeIncome) await navigation.DistributeIncomeAsync(id);
    });

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (!IsExisting || IsSaved) return;
        if (!await navigation.ConfirmAsync("Удалить операцию?", "Операция будет удалена, а остатки счетов пересчитаны. Отменить удаление нельзя.")) return;
        await store.DeleteOperationAsync(id);
        IsSaved = true;
        await navigation.BackAsync();
    });
    [RelayCommand]
    private Task CancelAsync() => RunAsync(async () =>
    {
        if (IsSaved || await navigation.ConfirmAsync("Закрыть форму?", "Несохранённые изменения будут потеряны."))
            await navigation.BackAsync();
    });

}
