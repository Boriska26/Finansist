using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Finance;
using Finansist.Core.Budgeting;
using Finansist.Core.Importing;

namespace Finansist.Core.ViewModels;

public sealed record ExistingImportChoice(string Id, string Label);

public sealed record ExpenseChoice(string? BillId, bool Necessary, string Label);

public partial class OperationEditorViewModel(IFinanceStore store, IFinanceNavigation navigation, IBudgetStore? budgetStore = null, IBankImportStore? importStore = null) : FinanceScreenViewModel
{
    private string id = Guid.NewGuid().ToString();
    private FinanceOperation? original;
    private ImportCandidate? imported;
    private IReadOnlyList<FinanceOperation> possibleMatches = [];
    public bool IsImported => imported is not null;
    public string SaveLabel => IsImported ? "Подтвердить" : "Сохранить";
    public string ImportText => imported is null ? "" : imported.Push.Title + "\n" + imported.Push.Text;
    public string ImportNotice => imported is null ? "" : imported.Parsed.Notice +
        (imported.Parsed.MessageTime is { } time ? $"\nВремя внутри сообщения: {time}" : "") +
        (imported.PossibleRepeat ? "\nПохожий push уже встречался. Проверьте совпадение с журналом." : "");
    public ObservableCollection<ExistingImportChoice> ExistingChoices { get; } = [];
    [ObservableProperty] private ExistingImportChoice? selectedExisting;
    [ObservableProperty] private string matchNotice = "Проверьте совпадения перед подтверждением.";
    public void ConfigureImport(ImportCandidate candidate)
    {
        imported = candidate; id = candidate.Id;
        KindIndex = candidate.Parsed.Ambiguous ? -1 : (int)candidate.Parsed.Kind;
        Amount = candidate.Parsed.AmountMinor is { } amount ? Money.Input(amount) : "";
        Date = candidate.Parsed.Date.ToDateTime(TimeOnly.MinValue); Note = candidate.Parsed.Description;
        Category = "Прочее";
    }
    private void InvalidateMatches()
    {
        if (!IsImported) return;
        ExistingChoices.Clear(); SelectedExisting = null; possibleMatches = [];
        MatchNotice = "Параметры изменились. Проверьте совпадения ещё раз.";
    }
    partial void OnSelectedAccountChanged(Account? value) => InvalidateMatches();
    partial void OnDestinationAccountChanged(Account? value) => InvalidateMatches();
    partial void OnAmountChanged(string value) => InvalidateMatches();
    partial void OnDateChanged(DateTime? value) => InvalidateMatches();
    private FinanceOperation DraftOperation()
    {
        if (!IsLoaded || SelectedAccount is null) throw new FinanceValidationException("Выберите счёт. Если список не загрузился, нажмите «Повторить загрузку».");
        if (!Enum.IsDefined((OperationKind)KindIndex)) throw new FinanceValidationException("Выберите тип: расход, доход или перевод между своими счетами.");
        if (Date is null || Date.Value.Date > DateTime.Today) throw new FinanceValidationException("Проверьте дату операции: она не должна быть позже сегодня.");
        if (IsTransfer && (DestinationAccount is null || DestinationAccount.Id == SelectedAccount.Id)) throw new FinanceValidationException("Выберите другой счёт получателя перевода.");
        return new(id, (OperationKind)KindIndex, SelectedAccount.Id, IsTransfer ? DestinationAccount?.Id : null,
            Money.Parse(Amount), DateOnly.FromDateTime(Date.Value), Category, Note);
    }
    [RelayCommand] private Task FindMatchesAsync() => RunAsync(async () => { await FindMatchesCoreAsync(DraftOperation()); });
    private async Task FindMatchesCoreAsync(FinanceOperation operation)
    {
        var selected = SelectedExisting?.Id;
        var snapshot = await store.ReadFinanceAsync();
        possibleMatches = snapshot.Operations.Where(o => ImportMatcher.Matches(o, operation)).ToArray();
        ExistingChoices.Clear(); ExistingChoices.Add(new("", "Создать отдельную новую операцию"));
        var names = snapshot.Accounts.ToDictionary(a => a.Id, a => a.Name);
        foreach (var item in possibleMatches)
        {
            var accounts = names[item.AccountId] + (item.DestinationId is { } to ? " → " + names[to] : "");
            var kind = item.Kind == OperationKind.Transfer ? "Перевод" : item.Kind == OperationKind.Income ? "Доход" : "Расход";
            ExistingChoices.Add(new(item.Id, $"Уже в журнале: {kind} · {Money.Format(item.AmountMinor)} · {item.Date:dd.MM.yyyy} · {accounts}"));
        }
        SelectedExisting = selected is not null ? ExistingChoices.FirstOrDefault(c => c.Id == selected) : possibleMatches.Count == 0 ? ExistingChoices[0] : null;
        MatchNotice = possibleMatches.Count == 0 ? "Совпадений по сумме, счёту и близкой дате не найдено." : "Есть похожие записи. Выберите существующую, чтобы не добавить дубль, или явно создайте отдельную.";
    }
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
    public string Title => IsImported ? "Проверка уведомления" : IsExisting ? "Изменить операцию" : "Новая операция";
    partial void OnKindIndexChanged(int value)
    {
        InvalidateMatches();
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
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == (imported?.DefaultAccountId ?? original?.AccountId)) ?? Accounts.FirstOrDefault();
        DestinationAccount = IsImported ? null : Accounts.FirstOrDefault(a => a.Id == original?.DestinationId) ?? Accounts.Skip(1).FirstOrDefault();
        if (budgetStore is not null)
        {
            var budget = await budgetStore.ReadBudgetAsync();
            ExpenseChoices.Clear();
            ExpenseChoices.Add(new(null, false, "На себя"));
            ExpenseChoices.Add(new(null, true, "Необходимое: продукты, транспорт и прочее"));
            var purpose = budget.Document.Purposes.FirstOrDefault(p => p.OperationId == id);
            foreach (var bill in budget.Document.Bills.Where(b => !b.Settled && !b.Archived || b.Id == purpose?.BillId).OrderBy(b => b.Due))
                ExpenseChoices.Add(new(bill.Id, true, $"{bill.Name} · {bill.Due:dd.MM.yyyy}{(bill.Archived ? " (удалён)" : "")}"));
            ExpenseChoice = ExpenseChoices.FirstOrDefault(c => c.BillId == purpose?.BillId && c.Necessary == (purpose?.Necessary ?? false)) ?? ExpenseChoices[0];
        }
        IsLoaded = true;
    });

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        if (IsSaved) return;
        var operation = DraftOperation();
        string savedId = id;
        string? existingId = null;
        var purpose = IsExpense && ExpenseChoice is { } choice ? new ExpensePurpose(id, choice.Necessary, choice.BillId) : null;
        if (IsImported)
        {
            if (importStore is null) throw new FinanceValidationException("Импорт недоступен. Откройте приложение заново.");
            await FindMatchesCoreAsync(operation);
            if (SelectedExisting is null) throw new FinanceValidationException("Найдены похожие операции. Выберите запись из журнала или создание отдельной операции.");
            existingId = string.IsNullOrEmpty(SelectedExisting.Id) ? null : SelectedExisting.Id;
            var message = existingId is not null ? "Уведомление будет связано с выбранной записью. Повторного зачисления или списания не будет." :
                $"Записать {Money.Format(operation.AmountMinor)} на выбранные счета?" + (possibleMatches.Count > 0 ? "\nЕсть похожие записи: подтвердите, что это отдельная операция." : "");
            if (!await navigation.ConfirmAsync("Подтвердить уведомление?", message)) return;
            savedId = await importStore.ConfirmImportAsync(id, operation, purpose, existingId);
        }
        else if (budgetStore is null) await store.SaveOperationAsync(operation);
        else await budgetStore.SaveBudgetOperationAsync(operation, purpose);
        IsSaved = true;
        await navigation.BackAsync();
        if (IsIncome && DistributeIncome && existingId is null) await navigation.DistributeIncomeAsync(savedId);
    });

    [RelayCommand] private Task DismissImportedAsync() => RunAsync(async () =>
    {
        if (!IsImported || importStore is null || IsSaved) return;
        if (!await navigation.ConfirmAsync("Отклонить уведомление?", "Запись в журнал не добавится.")) return;
        await importStore.DismissImportAsync(id); IsSaved = true; await navigation.BackAsync();
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
