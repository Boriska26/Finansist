using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Budgeting;
using Finansist.Core.Finance;

namespace Finansist.Core.ViewModels;

public interface IBudgetNavigation
{
    Task OpenAllocationAsync(string? incomeId, bool paymentsOnly = false);
    Task RecordTransferAsync(string sourceId, string destinationId, long amount);
    Task AddAccountAsync();
    Task BackAsync();
    Task<bool> ConfirmAsync(string title, string message);
}
public sealed record IncomeChoice(string Id, string Label);
public sealed record AllocationRow(string Id, string Title, string Details);

public partial class BudgetViewModel(IBudgetStore store, IBudgetNavigation navigation) : FinanceScreenViewModel
{
    private BudgetState? state;
    public ObservableCollection<IncomeChoice> Incomes { get; } = [];
    public ObservableCollection<AllocationRow> History { get; } = [];
    [ObservableProperty] private string necessary = "—";
    [ObservableProperty] private string savings = "—";
    [ObservableProperty] private string joy = "—";
    [ObservableProperty] private string cushion = "—";
    [ObservableProperty] private string warning = "";
    [ObservableProperty] private bool hasPending;
    [ObservableProperty] private bool hasHistory;
    [ObservableProperty] private bool canTransfer;
    [ObservableProperty] private Account? transferSource;
    public ObservableCollection<Account> Sources { get; } = [];
    [RelayCommand] private Task RefreshAsync() => RunAsync(async () =>
    {
        state = await store.ReadBudgetAsync();
        var totals = BudgetCalculator.Totals(state);
        Necessary = Money.Format(totals.NecessaryMinor); Savings = Money.Format(totals.PendingSavingsMinor);
        Joy = Money.Format(Math.Max(0, totals.JoyMinor)); Cushion = Money.Format(totals.CushionMinor);
        Warning = totals.JoyMinor < 0 ? $"Свободные деньги закончились. Перерасход или нехватка для оставленных сумм: {Money.Format(-totals.JoyMinor)}. Проверьте расходы и следующие платежи." : "";
        var sourceId = TransferSource?.Id;
        Sources.Clear();
        foreach (var account in state.Finance.Accounts.Where(a => a.Id != state.Document.CushionAccountId)) Sources.Add(account.Account);
        TransferSource = Sources.FirstOrDefault(a => a.Id == sourceId) ?? Sources.FirstOrDefault();
        CanTransfer = totals.PendingSavingsMinor > 0 && state.Document.CushionAccountId is not null;
        Incomes.Clear();
        foreach (var income in state.Finance.Operations.Where(o => o.Kind == OperationKind.Income && o.AccountId != state.Document.CushionAccountId && !state.Document.Allocations.Any(a => a.IncomeId == o.Id)))
            Incomes.Add(new(income.Id, $"{income.Date:dd.MM.yyyy} · {Money.Format(income.AmountMinor)} · {income.Category}"));
        HasPending = Incomes.Count > 0;
        History.Clear();
        foreach (var item in state.Document.Allocations.AsEnumerable().Reverse())
            History.Add(new(item.IncomeId, $"{item.IncomeDate:dd.MM.yyyy} · распределено {Money.Format(item.AvailableMinor)}", $"До {item.Until:dd.MM.yyyy}\nНеобходимое {Money.Format(item.NecessaryMinor)}\nВ подушку {Money.Format(item.SavingsMinor)}\nНа себя {Money.Format(item.JoyMinor)}"));
        HasHistory = History.Count > 0;
    });
    [RelayCommand] private Task AllocateAsync(IncomeChoice income) => RunAsync(() => navigation.OpenAllocationAsync(income.Id));
    [RelayCommand] private Task PaymentsAsync() => RunAsync(() => navigation.OpenAllocationAsync(null, true));
    [RelayCommand] private Task TransferAsync() => RunAsync(async () =>
    {
        var fresh = await store.ReadBudgetAsync();
        var pending = BudgetCalculator.Totals(fresh).PendingSavingsMinor;
        if (pending <= 0 || fresh.Document.CushionAccountId is null || TransferSource is null)
            throw new FinanceValidationException("Выберите счёт, с которого переводите деньги в подушку.");
        await navigation.RecordTransferAsync(TransferSource.Id, fresh.Document.CushionAccountId, pending);
    });
    [RelayCommand] private async Task CancelAllocationAsync(AllocationRow row)
    {
        await RunAsync(async () =>
        {
            if (await navigation.ConfirmAsync("Отменить распределение?", "Доход снова можно будет распределить. Реальные расходы и переводы сохранятся. Остатки бюджета будут пересчитаны."))
                await store.CancelAllocationAsync(row.Id);
        });
        if (!HasError) await RefreshAsync();
    }
}

public partial class BudgetBillRow : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string? SeriesId { get; init; }
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string amount = "0";
    [ObservableProperty] private DateTime? due = DateTime.Today;
    [ObservableProperty] private bool monthly;
    [ObservableProperty] private bool settled;
    public string Status { get; init; } = "Укажите сумму, которую нужно оставить. Ноль — не учитывать.";
    public BudgetBill ToBill() => new(Id, Name.Trim(), DateOnly.FromDateTime(Due ?? throw new FinanceValidationException("Укажите дату платежа.")),
        Money.Parse(Amount, true), Monthly, SeriesId ?? (Monthly ? Id : null), Settled);
}

public partial class BudgetEditorViewModel(IBudgetStore store, IBudgetNavigation navigation) : FinanceScreenViewModel
{
    private string? incomeId;
    private BudgetState? state;
    private BudgetProposal? proposal;
    private bool saved;
    public bool PaymentsOnly { get; private set; }
    public bool IsAllocation => !PaymentsOnly;
    public string Title => PaymentsOnly ? "Обязательные расходы" : "Распределим поступление";
    public string SaveLabel => PaymentsOnly ? "Сохранить платежи" : "Подтвердить три суммы";
    public ObservableCollection<BudgetBillRow> Bills { get; } = [];
    public bool NoBills => Bills.Count == 0;
    [ObservableProperty] private string paymentNotice = "";
    public ObservableCollection<Account> Accounts { get; } = [];
    [ObservableProperty] private Account? cushionAccount;
    [ObservableProperty] private DateTime? until = DateTime.Today.AddDays(14);
    [ObservableProperty] private string incomeText = "";
    [ObservableProperty] private bool isLoaded;
    [ObservableProperty] private bool hasProposal;
    [ObservableProperty] private string necessary = "—";
    [ObservableProperty] private string savings = "—";
    [ObservableProperty] private string joy = "—";
    [ObservableProperty] private string explanation = "";
    public DateTime Today => DateTime.Today;
    public DateTime LastDate => DateTime.Today.AddMonths(3);
    partial void OnCushionAccountChanged(Account? value) => Invalidate();
    partial void OnUntilChanged(DateTime? value) { Invalidate(); if (IsLoaded) AddOccurrences(); }
    private void Invalidate() { HasProposal = false; proposal = null; }
    public void Configure(string? id, bool paymentsOnly = false) { incomeId = id; PaymentsOnly = paymentsOnly; }
    [RelayCommand] private Task LoadAsync() => RunAsync(async () =>
    {
        var fresh = await store.ReadBudgetAsync();
        if (IsLoaded)
        {
            if (state!.Revision != fresh.Revision) throw new FinanceValidationException("Бюджет изменился. Закройте и откройте этот экран заново.");
            state = fresh; LoadAccounts(); Invalidate(); return;
        }
        state = fresh;
        if (!PaymentsOnly)
        {
            var income = state.Finance.Operations.SingleOrDefault(o => o.Id == incomeId);
            BudgetCalculator.Require(income is { Kind: OperationKind.Income }, "Поступление не найдено. Откройте бюджет заново.");
            IncomeText = $"Получено {Money.Format(income!.AmountMinor)} · {income.Date:dd.MM.yyyy}\nСумму будущей зарплаты знать не нужно.";
        }
        LoadAccounts();
        foreach (var bill in state.Document.Bills.Where(b => !b.Archived).OrderBy(b => b.Due))
        {
            // Paid history is kept in the document, but does not clutter the next salary form.
            if (!PaymentsOnly && (bill.Settled || BudgetCalculator.Paid(state, bill.Id) >= bill.AmountMinor) &&
                (!bill.Monthly || state.Document.Bills.Any(b => b.SeriesId == bill.SeriesId && b.Due > bill.Due))) continue;
            AddRow(bill, $"Уже оплачено по журналу: {Money.Format(BudgetCalculator.Paid(state, bill.Id))}\nРанее выделено: {Money.Format(BudgetCalculator.Funded(state.Document, bill.Id))}. Повторно не резервируем.");
        }
        if (Bills.Count == 0 && state.Document.Bills.Count == 0)
        {
            AddRow(new(Guid.NewGuid().ToString(), "Продукты и транспорт", DateOnly.FromDateTime(Until!.Value), 0));
            AddRow(new(Guid.NewGuid().ToString(), "Аренда", DateOnly.FromDateTime(DateTime.Today), 0, true));
        }
        var groceries = state.Document.Bills.Where(b => b.Name == "Продукты и транспорт" && !b.Monthly && !b.Archived).OrderBy(b => b.Due).LastOrDefault();
        if (!PaymentsOnly && groceries is not null && groceries.Due < DateOnly.FromDateTime(DateTime.Today) &&
            !Bills.Any(b => b.Name == groceries.Name && b.Due == Until))
            AddRow(groceries with { Id = Guid.NewGuid().ToString(), Due = DateOnly.FromDateTime(Until!.Value), Settled = false },
                "Расходы на новый период. Поправьте примерную сумму.");
        IsLoaded = true; AddOccurrences(); OnPropertyChanged(nameof(NoBills));
    });
    private void LoadAccounts()
    {
        if (state is null) return;
        var selectedId = CushionAccount?.Id ?? state.Document.CushionAccountId;
        var incomeAccount = state.Finance.Operations.FirstOrDefault(o => o.Id == incomeId)?.AccountId;
        Accounts.Clear();
        foreach (var account in state.Finance.Accounts.Where(a => a.Id != incomeAccount)) Accounts.Add(account.Account);
        CushionAccount = Accounts.FirstOrDefault(a => a.Id == selectedId);
    }
    private void AddRow(BudgetBill bill, string? status = null)
    {
        var row = new BudgetBillRow { Id = bill.Id, SeriesId = bill.SeriesId, Name = bill.Name, Amount = Money.Input(bill.AmountMinor),
            Due = bill.Due.ToDateTime(TimeOnly.MinValue), Monthly = bill.Monthly, Settled = bill.Settled,
            Status = status ?? "Ноль — в этот период деньги не нужны." };
        row.PropertyChanged += (_, args) =>
        {
            Invalidate();
            if (args.PropertyName == nameof(BudgetBillRow.Monthly))
            {
                var series = row.SeriesId ?? row.Id;
                foreach (var other in Bills.Where(b => (b.SeriesId ?? b.Id) == series && b != row))
                    other.Monthly = row.Monthly;
            }
        };
        Bills.Add(row); Invalidate(); OnPropertyChanged(nameof(NoBills));
    }
    private void AddOccurrences()
    {
        if (state is null || Until is null || PaymentsOnly) return;
        var end = DateOnly.FromDateTime(Until.Value);
        foreach (var group in state.Document.Bills.Where(b => b.SeriesId is not null && !b.Archived).GroupBy(b => b.SeriesId))
        {
            var latest = group.OrderBy(b => b.Due).Last();
            if (!(Bills.FirstOrDefault(b => b.Id == latest.Id)?.Monthly ?? latest.Monthly)) continue;
            var anchor = group.Min(b => b.Due);
            var month = (latest.Due.Year - anchor.Year) * 12 + latest.Due.Month - anchor.Month + 1;
            for (; month < 2400; month++)
            {
                var next = anchor.AddMonths(month);
                if (next > end) break;
                if (Bills.Any(b => b.SeriesId == latest.SeriesId && b.Due == next.ToDateTime(TimeOnly.MinValue))) continue;
                AddRow(latest with { Id = Guid.NewGuid().ToString(), Due = next, Settled = false }, "Сумма прошлого месяца. Подтвердите или поправьте её.");
            }
        }
    }
    private List<BudgetBill> DraftBills()
    {
        if (state is null) throw new FinanceValidationException("Сначала загрузите платежи.");
        var edited = Bills.Select(b => b.ToBill()).ToList();
        // Zero-value blank rows do not create fictitious obligations.
        edited.RemoveAll(b => b.AmountMinor == 0 && !state.Document.Bills.Any(old => old.Id == b.Id));
        return [..state.Document.Bills.Where(old => !edited.Any(b => b.Id == old.Id)), ..edited];
    }
    [RelayCommand] private Task DeleteBillAsync(BudgetBillRow row) => RunAsync(async () =>
    {
        if (state is null || saved || !Bills.Contains(row)) return;
        var series = row.SeriesId ?? row.Id;
        var persisted = state.Document.Bills.FirstOrDefault(b => b.Id == row.Id || b.SeriesId == series);
        if (!await navigation.ConfirmAsync("Удалить платёж и его повторения?",
            "Он исчезнет из обязательных платежей. Неиспользованный резерв освободится. Реальные расходы останутся в журнале.")) return;
        if (persisted is not null) state = await store.DeleteBillAsync(persisted.Id, state.Revision);
        foreach (var item in Bills.Where(b => b.Id == row.Id || (b.SeriesId ?? b.Id) == series).ToArray())
            Bills.Remove(item);
        Invalidate(); OnPropertyChanged(nameof(NoBills));
        PaymentNotice = "Платёж удалён. Расходы в журнале сохранены.";
    });
    [RelayCommand] private void AddBill() => AddRow(new(Guid.NewGuid().ToString(), "Другой платёж", DateOnly.FromDateTime(DateTime.Today), 0));
    [RelayCommand] private void AddInstallment() => AddRow(new(Guid.NewGuid().ToString(), "Рассрочка", DateOnly.FromDateTime(DateTime.Today), 0, true));
    [RelayCommand] private Task AddAccountAsync() => RunAsync(navigation.AddAccountAsync);
    [RelayCommand] private Task CalculateAsync() => RunAsync(() =>
    {
        Invalidate();
        if (state is null || CushionAccount is null || Until is null) throw new FinanceValidationException("Выберите отдельный счёт подушки и дату следующего поступления.");
        proposal = BudgetCalculator.Propose(state, incomeId!, CushionAccount.Id, DateOnly.FromDateTime(Until.Value), DraftBills());
        Necessary = Money.Format(proposal.NecessaryMinor); Savings = Money.Format(proposal.SavingsMinor); Joy = Money.Format(proposal.JoyMinor);
        Explanation = proposal.Explanation; HasProposal = true; return Task.CompletedTask;
    });
    [RelayCommand] private Task SaveAsync() => RunAsync(async () =>
    {
        if (saved) return;
        if (state is null) throw new FinanceValidationException("Сначала загрузите экран.");
        if (PaymentsOnly) await store.SaveBillsAsync(DraftBills(), state.Revision);
        else
        {
            if (proposal is null || !HasProposal || CushionAccount is null || Until is null)
                throw new FinanceValidationException("Нажмите «Показать три суммы» и проверьте предложение.");
            if (!await navigation.ConfirmAsync("Сохранить распределение?", $"На необходимое: {Necessary}\nВ подушку: {Savings}\nНа себя: {Joy}\n\n{(proposal.ShortageMinor > 0 ? "На обязательные расходы денег не хватает. " : "")}Перевода денег пока не будет.")) return;
            await store.AllocateAsync(incomeId!, CushionAccount.Id, DateOnly.FromDateTime(Until.Value), DraftBills(), proposal.Fingerprint);
        }
        saved = true; await navigation.BackAsync();
    });
    [RelayCommand] private Task CancelAsync() => RunAsync(async () =>
    {
        if (saved || await navigation.ConfirmAsync("Закрыть без сохранения?", "Доход останется в журнале. К распределению можно вернуться во вкладке «Бюджет».")) await navigation.BackAsync();
    });
}
