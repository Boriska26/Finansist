using Finansist.Core.Budgeting;
using Finansist.Core.Finance;
using Finansist.Core.Storage;
using Finansist.Core.ViewModels;

namespace Finansist.Core.Tests;

public sealed class BudgetTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "finansist-budget-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(directory, "synthetic.db");
    private LocalStore Store() => new(new TestConnectionFactory(DatabasePath));
    private static string Id() => Guid.NewGuid().ToString();
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private readonly Account card = new(Id(), "Тестовая карта", 0);
    private readonly Account cushion = new(Id(), "Тестовая подушка", 10000000);
    public BudgetTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private async Task<FinanceOperation> Setup(long income = 4000000)
    {
        await Store().SaveAccountAsync(card); await Store().SaveAccountAsync(cushion);
        return await Income(income);
    }
    private async Task<FinanceOperation> Income(long amount)
    {
        var op = new FinanceOperation(Id(), OperationKind.Income, card.Id, null, amount, Today, "Зарплата", "Синтетика");
        await Store().SaveOperationAsync(op); return op;
    }
    private static BudgetBill Bill(long amount) => new(Id(), "Тестовая аренда", Today, amount);
    private BudgetProposal Propose(BudgetState state, FinanceOperation op, params BudgetBill[] bills) =>
        BudgetCalculator.Propose(state, op.Id, cushion.Id, Today.AddDays(14), bills);
    private async Task<BudgetState> Allocate(FinanceOperation op, params BudgetBill[] bills)
    {
        var state = await Store().ReadBudgetAsync(); var proposal = Propose(state, op, bills);
        return await Store().AllocateAsync(op.Id, cushion.Id, Today.AddDays(14), bills, proposal.Fingerprint);
    }

    [Theory]
    [InlineData(0L, 2000000L, 800000L, 1200000L, 0L)]
    [InlineData(2400000L, 2400000L, 800000L, 800000L, 0L)]
    [InlineData(3700000L, 3700000L, 300000L, 0L, 0L)]
    [InlineData(5000000L, 4000000L, 0L, 0L, 1000000L)]
    public async Task Distribution_ProtectsCushionThenReportsShortage(long obligations, long necessary, long savings, long joy, long shortage)
    {
        var income = await Setup(); var before = await Store().ReadBudgetAsync();
        var result = Propose(before, income, Bill(obligations));
        Assert.Equal(necessary, result.NecessaryMinor); Assert.Equal(savings, result.SavingsMinor);
        Assert.Equal(joy, result.JoyMinor); Assert.Equal(shortage, result.ShortageMinor);
        Assert.Equal(income.AmountMinor, result.NecessaryMinor + result.SavingsMinor + result.JoyMinor);
        Assert.Equal(10000000, before.Finance.Accounts.Single(a => a.Id == cushion.Id).BalanceMinor);
        Assert.Empty((await Store().ReadBudgetAsync()).Document.Allocations);
    }

    [Fact]
    public async Task Kopecks_AreConserved()
    {
        var income = await Setup(101); var result = Propose(await Store().ReadBudgetAsync(), income);
        Assert.Equal(50, result.NecessaryMinor); Assert.Equal(20, result.SavingsMinor); Assert.Equal(31, result.JoyMinor);
    }

    [Fact]
    public async Task Confirmation_PersistsWithoutCreatingTransfers_AndRejectsDuplicate()
    {
        var income = await Setup(); var bill = Bill(2400000); var before = await Store().ReadBudgetAsync();
        var proposal = Propose(before, income, bill);
        var after = await Store().AllocateAsync(income.Id, cushion.Id, Today.AddDays(14), [bill], proposal.Fingerprint);
        Assert.Single(after.Document.Allocations); Assert.Single(after.Finance.Operations);
        Assert.Equal(before.Finance.TotalMinor, after.Finance.TotalMinor);
        Assert.Equal(800000, BudgetCalculator.Totals(after).PendingSavingsMinor);
        Assert.Single((await Store().ReadBudgetAsync()).Document.Allocations);
        await Assert.ThrowsAsync<FinanceValidationException>(() => Store().AllocateAsync(income.Id, cushion.Id, Today.AddDays(14), [bill], proposal.Fingerprint));
        Assert.Single((await Store().ReadBudgetAsync()).Document.Allocations);
    }

    [Fact]
    public async Task SecondIncome_DoesNotFundSameRentTwice()
    {
        var income = await Setup(); var bill = Bill(2400000); await Allocate(income, bill);
        var advance = await Income(2000000); var state = await Store().ReadBudgetAsync();
        var proposal = Propose(state, advance, bill);
        Assert.Empty(proposal.Funding);
        Assert.Equal(1000000, proposal.NecessaryMinor); Assert.Equal(400000, proposal.SavingsMinor); Assert.Equal(600000, proposal.JoyMinor);
    }

    [Fact]
    public async Task PartlyFundedRent_OnlyReservesMissingPartAfterNextIncome()
    {
        var income = await Setup(); var bill = Bill(5000000); await Allocate(income, bill);
        var advance = await Income(2000000); var proposal = Propose(await Store().ReadBudgetAsync(), advance, bill);
        Assert.Equal(1000000, Assert.Single(proposal.Funding).AmountMinor);
        Assert.Equal(1000000, proposal.NecessaryMinor); Assert.Equal(400000, proposal.SavingsMinor);
    }

    [Fact]
    public async Task PaidRent_IsConsumedOnce_AndNeverReservedAgain()
    {
        var income = await Setup(); var bill = Bill(2400000); await Allocate(income, bill);
        var expense = new FinanceOperation(Id(), OperationKind.Expense, card.Id, null, 2400000, Today, "Аренда", "Синтетика");
        var state = await Store().SaveBudgetOperationAsync(expense, new(expense.Id, true, bill.Id));
        Assert.Equal(0, BudgetCalculator.Totals(state).NecessaryMinor);
        Assert.Equal(800000, BudgetCalculator.Totals(state).JoyMinor);
        var advance = await Income(2000000); state = await Store().ReadBudgetAsync();
        Assert.Empty(Propose(state, advance, bill).Funding);
        await Store().DeleteOperationAsync(expense.Id);
        state = await Store().ReadBudgetAsync();
        Assert.Empty(state.Document.Purposes);
        Assert.Equal(2400000, BudgetCalculator.Totals(state).NecessaryMinor);
    }

    [Fact]
    public async Task ManualCushionTransferCountsOnce_AndWithdrawalIsNotIncome()
    {
        var income = await Setup(); await Allocate(income);
        var transfer = new FinanceOperation(Id(), OperationKind.Transfer, card.Id, cushion.Id, 300000, Today, "Перевод", "Синтетика");
        await Store().SaveOperationAsync(transfer);
        var totals = BudgetCalculator.Totals(await Store().ReadBudgetAsync());
        Assert.Equal(500000, totals.PendingSavingsMinor); Assert.Equal(10300000, totals.CushionMinor);
        await Store().SaveOperationAsync(transfer);
        Assert.Equal(500000, BudgetCalculator.Totals(await Store().ReadBudgetAsync()).PendingSavingsMinor);
        await Store().SaveOperationAsync(transfer with { Id = Id(), AccountId = cushion.Id, DestinationId = card.Id, AmountMinor = 100000 });
        totals = BudgetCalculator.Totals(await Store().ReadBudgetAsync());
        Assert.Equal(500000, totals.PendingSavingsMinor); Assert.Equal(10200000, totals.CushionMinor);
        await Store().DeleteOperationAsync(transfer.Id);
        Assert.Equal(800000, BudgetCalculator.Totals(await Store().ReadBudgetAsync()).PendingSavingsMinor);
    }

    [Fact]
    public async Task ExistingTransferBeforeAllocationDoesNotPretendToCompleteNewSaving()
    {
        var income = await Setup();
        await Store().SaveOperationAsync(new(Id(), OperationKind.Transfer, card.Id, cushion.Id, 1000000, Today, "Перевод", "Синтетика"));
        var state = await Allocate(income);
        Assert.Equal(600000, BudgetCalculator.Totals(state).PendingSavingsMinor);
        Assert.Equal(3000000, Assert.Single(state.Document.Allocations).AvailableMinor);
    }

    [Fact]
    public async Task JoyOverspendingDoesNotAppearAsFreeCash()
    {
        var income = await Setup(); await Allocate(income);
        await Store().SaveOperationAsync(new(Id(), OperationKind.Expense, card.Id, null, 1500000, Today, "На себя", "Синтетика"));
        var totals = BudgetCalculator.Totals(await Store().ReadBudgetAsync());
        Assert.Equal(-300000, totals.JoyMinor); Assert.Equal(0, totals.FreeCashMinor);
        Assert.Equal(10000000, totals.CushionMinor);
    }

    [Fact]
    public async Task AlreadySpentIncomeIsLimitedByAvailableCash_NotCushion()
    {
        var income = await Setup();
        await Store().SaveOperationAsync(new(Id(), OperationKind.Expense, card.Id, null, 3500000, Today, "Тест", "Синтетика"));
        var proposal = Propose(await Store().ReadBudgetAsync(), income);
        Assert.Equal(500000, proposal.AvailableMinor);
    }

    [Fact]
    public async Task ChangedLedgerOrBillsRejectsStaleProposal()
    {
        var income = await Setup(); var bill = Bill(2400000);
        var proposal = Propose(await Store().ReadBudgetAsync(), income, bill);
        await Store().SaveAccountAsync(card with { OpeningMinor = 100 });
        await Assert.ThrowsAsync<FinanceValidationException>(() => Store().AllocateAsync(income.Id, cushion.Id, Today.AddDays(14), [bill], proposal.Fingerprint));
        proposal = Propose(await Store().ReadBudgetAsync(), income, bill);
        await Assert.ThrowsAsync<FinanceValidationException>(() => Store().AllocateAsync(income.Id, cushion.Id, Today.AddDays(14), [bill with { AmountMinor = 2500000 }], proposal.Fingerprint));
        Assert.Empty((await Store().ReadBudgetAsync()).Document.Allocations);
    }

    [Fact]
    public async Task AllocatedIncomeCannotBeChangedOrDeletedUntilAllocationCancelled()
    {
        var income = await Setup(); await Allocate(income);
        await Assert.ThrowsAsync<FinanceValidationException>(() => Store().SaveOperationAsync(income with { AmountMinor = 100 }));
        await Assert.ThrowsAsync<FinanceValidationException>(() => Store().DeleteOperationAsync(income.Id));
        await Store().CancelAllocationAsync(income.Id);
        Assert.Single((await Store().ReadBudgetAsync()).Finance.Operations);
        await Store().DeleteOperationAsync(income.Id);
        Assert.Empty((await Store().ReadBudgetAsync()).Finance.Operations);
    }

    [Fact]
    public async Task ClosedObligationReleasesUnusedReservationAndStopsFutureRequest()
    {
        var income = await Setup(); var bill = Bill(2400000); var state = await Allocate(income, bill);
        state = await Store().SaveBillsAsync([bill with { Settled = true, Monthly = false }], state.Revision);
        var totals = BudgetCalculator.Totals(state);
        Assert.Equal(0, totals.NecessaryMinor); Assert.Equal(3200000, totals.JoyMinor);
        Assert.Equal(0, BudgetCalculator.Needed(state, state.Document.Bills[0]));
    }

    [Fact]
    public async Task ExpensePurposeAndOperationAreAtomicOnValidationFailure()
    {
        var income = await Setup(); var bill = Bill(100); await Allocate(income, bill);
        var op = new FinanceOperation(Id(), OperationKind.Expense, card.Id, null, -1, Today, "Тест", "Синтетика");
        var revision = (await Store().ReadBudgetAsync()).Revision;
        await Assert.ThrowsAsync<FinanceValidationException>(() => Store().SaveBudgetOperationAsync(op, new(op.Id, true, bill.Id)));
        var state = await Store().ReadBudgetAsync();
        Assert.Equal(revision, state.Revision); Assert.Empty(state.Document.Purposes); Assert.Single(state.Finance.Operations);
    }

    [Fact]
    public async Task NewSchemaKeepsOldInactivePlanAndFinance()
    {
        await Setup();
        using (var db = new NativeSqliteConnection(DatabasePath))
        {
            db.Execute("DROP TABLE salary_budget"); db.Execute("PRAGMA user_version = 3");
            db.Execute("CREATE TABLE planning_state (id INTEGER PRIMARY KEY, payload TEXT)");
            db.Execute("INSERT INTO planning_state VALUES (1, 'synthetic old plan')");
        }
        var state = await Store().ReadBudgetAsync();
        Assert.Single(state.Finance.Operations); Assert.Empty(state.Document.Allocations);
        using var check = new NativeSqliteConnection(DatabasePath);
        Assert.Equal("4", check.Scalar("PRAGMA user_version"));
        Assert.Equal("synthetic old plan", check.Scalar("SELECT payload FROM planning_state WHERE id=1"));
    }

    [Fact]
    public async Task EditorInvalidatesProposalAfterChangingExpenseAndCanResumeSavedIncome()
    {
        var income = await Setup(); var vm = new BudgetEditorViewModel(Store(), new Navigation()); vm.Configure(income.Id);
        await vm.LoadCommand.ExecuteAsync(null); vm.CushionAccount = vm.Accounts.Single(a => a.Id == cushion.Id);
        await vm.CalculateCommand.ExecuteAsync(null); Assert.True(vm.HasProposal);
        vm.Bills[0].Amount = "12000"; Assert.False(vm.HasProposal);
        await vm.SaveCommand.ExecuteAsync(null); Assert.True(vm.HasError);
        Assert.Empty((await Store().ReadBudgetAsync()).Document.Allocations);
        await vm.CalculateCommand.ExecuteAsync(null); await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.HasError); Assert.Single((await Store().ReadBudgetAsync()).Document.Allocations);
        await vm.SaveCommand.ExecuteAsync(null); Assert.Single((await Store().ReadBudgetAsync()).Document.Allocations);
    }

    [Fact]
    public async Task MonthlyBillsClampToFebruaryThenReturnToOriginalDay_WithoutDuplicates()
    {
        var income = await Setup(); var start = new DateOnly(Today.Year, 1, 31); var series = Id();
        var bill = new BudgetBill(Id(), "Рассрочка", start, 100, true, series, true);
        await Store().SaveBillsAsync([bill], 0);
        var vm = new BudgetEditorViewModel(Store(), new Navigation()); vm.Configure(income.Id); vm.Until = new DateTime(Today.Year, 4, 1);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Contains(vm.Bills, b => b.Due == new DateTime(Today.Year, 2, DateTime.IsLeapYear(Today.Year) ? 29 : 28));
        Assert.Contains(vm.Bills, b => b.Due == new DateTime(Today.Year, 3, 31));
        var count = vm.Bills.Count;
        vm.Until = vm.Until!.Value.AddDays(1);
        Assert.Equal(vm.Bills.Select(b => (b.SeriesId, b.Due)).Distinct().Count(), vm.Bills.Count);
        Assert.True(vm.Bills.Count >= count);
    }

    [Fact]
    public async Task FailedMigrationRollsBackNewTableAndKeepsVersionThreeData()
    {
        await Setup();
        using (var db = new NativeSqliteConnection(DatabasePath))
        {
            db.Execute("DROP TABLE salary_budget"); db.Execute("PRAGMA user_version = 3");
        }
        var failing = new LocalStore(new FailureFactory(DatabasePath, "PRAGMA user_version = 4"));
        await Assert.ThrowsAsync<StorageException>(() => failing.ReadBudgetAsync());
        using var check = new NativeSqliteConnection(DatabasePath);
        Assert.Equal("3", check.Scalar("PRAGMA user_version"));
        Assert.Equal("0", check.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='salary_budget'"));
        Assert.Equal("1", check.Scalar("SELECT COUNT(*) FROM operations"));
    }

    [Fact]
    public async Task FailedConfirmationCommitLeavesIncomeUnallocated()
    {
        var income = await Setup(); var state = await Store().ReadBudgetAsync();
        var proposal = Propose(state, income);
        var failing = new LocalStore(new FailureFactory(DatabasePath, "COMMIT"));
        await Assert.ThrowsAsync<StorageException>(() => failing.AllocateAsync(income.Id, cushion.Id, Today.AddDays(14), [], proposal.Fingerprint));
        state = await Store().ReadBudgetAsync();
        Assert.Empty(state.Document.Allocations); Assert.Single(state.Finance.Operations);
    }

    private sealed class FailureFactory(string path, string failedSql) : ISqliteConnectionFactory
    {
        public ISqliteConnection Open() => new FailureConnection(new NativeSqliteConnection(path), failedSql);
    }
    private sealed class FailureConnection(ISqliteConnection inner, string failedSql) : ISqliteConnection
    {
        public void Execute(string sql, params string[] arguments)
        {
            if (sql == failedSql) throw new IOException("Synthetic failure");
            inner.Execute(sql, arguments);
        }
        public string? Scalar(string sql, params string[] arguments) => inner.Scalar(sql, arguments);
        public IReadOnlyList<string?[]> Query(string sql, params string[] arguments) => inner.Query(sql, arguments);
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task GeneralNecessarySpendingCannotConsumeSpecificRentReservation()
    {
        var income = await Setup(); var bill = Bill(2400000); await Allocate(income, bill);
        var food = new FinanceOperation(Id(), OperationKind.Expense, card.Id, null, 500000, Today, "Продукты", "Синтетика");
        var state = await Store().SaveBudgetOperationAsync(food, new(food.Id, true));
        var totals = BudgetCalculator.Totals(state);
        Assert.Equal(2400000, totals.NecessaryMinor);
        Assert.Equal(300000, totals.JoyMinor);
        Assert.Equal(0, totals.FreeCashMinor);
    }

    [Fact]
    public async Task NewSalaryAfterOverspendingIsAllocatedOnceWithoutPhantomFreeCash()
    {
        var income = await Setup(); await Allocate(income);
        await Store().SaveOperationAsync(new(Id(), OperationKind.Expense, card.Id, null, 1500000, Today, "На себя", "Синтетика"));
        var next = await Income(2000000);
        var proposal = Propose(await Store().ReadBudgetAsync(), next);
        Assert.Equal(2000000, proposal.AvailableMinor);
        var state = await Allocate(next);
        var totals = BudgetCalculator.Totals(state);
        Assert.Equal(0, totals.FreeCashMinor);
        Assert.Equal(300000, totals.JoyMinor);
    }

    [Fact]
    public async Task PaymentBeforeFirstAllocationDoesNotCauseDuplicateReserveOnSecondSalary()
    {
        var income = await Setup(); var bill = Bill(2400000);
        await Store().SaveBillsAsync([bill], 0);
        var payment = new FinanceOperation(Id(), OperationKind.Expense, card.Id, null, 600000, Today, "Аренда", "Синтетика");
        await Store().SaveBudgetOperationAsync(payment, new(payment.Id, true, bill.Id));
        var state = await Allocate(income, bill);
        Assert.Equal(0, BudgetCalculator.Needed(state, bill));
        var food = payment with { Id = Id(), AmountMinor = 500000, Category = "Продукты" };
        state = await Store().SaveBudgetOperationAsync(food, new(food.Id, true));
        Assert.Equal(1800000, BudgetCalculator.Totals(state).NecessaryMinor);
        var next = await Income(2000000);
        Assert.Empty(Propose(await Store().ReadBudgetAsync(), next, bill).Funding);
        Assert.Equal(0, BudgetCalculator.Totals(await Allocate(next, bill)).FreeCashMinor);
    }

    [Fact]
    public async Task ExtraPaymentBeyondReservedFundsDoesNotGetFundedAgainAfterNewSalary()
    {
        var income = await Setup(); var bill = Bill(5000000); await Allocate(income, bill);
        var payment = new FinanceOperation(Id(), OperationKind.Expense, card.Id, null, 4500000, Today, "Аренда", "Синтетика");
        await Store().SaveBudgetOperationAsync(payment, new(payment.Id, true, bill.Id));
        var next = await Income(2000000);
        var proposal = Propose(await Store().ReadBudgetAsync(), next, bill);
        Assert.Equal(500000, Assert.Single(proposal.Funding).AmountMinor);
        var state = await Allocate(next, bill);
        Assert.Equal(0, BudgetCalculator.Needed(state, bill));
        Assert.Equal(500000, BudgetCalculator.Totals(state).NecessaryMinor);
        Assert.Equal(0, BudgetCalculator.Totals(state).FreeCashMinor);
    }

    [Fact]
    public async Task DeleteBillStopsSeriesReleasesUnusedReserveAndKeepsRealExpense()
    {
        var income = await Setup(); var series = Id();
        var bill = Bill(2400000) with { SeriesId = series, Monthly = true };
        var future = bill with { Id = Id(), Due = Today.AddMonths(1) };
        var unrelated = Bill(0);
        await Allocate(income, bill, future, unrelated);
        var payment = new FinanceOperation(Id(), OperationKind.Expense, card.Id, null, 600000, Today, "Аренда", "Синтетика");
        var before = await Store().SaveBudgetOperationAsync(payment, new(payment.Id, true, bill.Id));
        var after = await Store().DeleteBillAsync(bill.Id, before.Revision);
        Assert.All(after.Document.Bills.Where(b => b.SeriesId == series), b =>
        {
            Assert.True(b.Archived); Assert.True(b.Settled); Assert.False(b.Monthly);
        });
        Assert.False(after.Document.Bills.Single(b => b.Id == unrelated.Id).Archived);
        Assert.Equal(before.Finance.TotalMinor, after.Finance.TotalMinor);
        Assert.Equal(payment, after.Finance.Operations.Single(o => o.Id == payment.Id));
        Assert.Equal(bill.Id, Assert.Single(after.Document.Purposes).BillId);
        Assert.Equal(0, BudgetCalculator.Totals(after).NecessaryMinor);
        Assert.Equal(2600000, BudgetCalculator.Totals(after).JoyMinor);
        Assert.Equal(800000, BudgetCalculator.Totals(after).PendingSavingsMinor);
        var vm = new BudgetEditorViewModel(Store(), new Navigation()); vm.Configure(null, true);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Bills, b => b.SeriesId == series);
    }

    [Fact]
    public async Task DeleteBillRejectsStaleEditorAndRollsBackFailedCommit()
    {
        var income = await Setup(); var bill = Bill(100000); var state = await Allocate(income, bill);
        await Assert.ThrowsAsync<FinanceValidationException>(() => Store().DeleteBillAsync(bill.Id, state.Revision - 1));
        var failing = new LocalStore(new FailureFactory(DatabasePath, "COMMIT"));
        await Assert.ThrowsAsync<StorageException>(() => failing.DeleteBillAsync(bill.Id, state.Revision));
        var after = await Store().ReadBudgetAsync();
        Assert.Equal(state.Revision, after.Revision);
        Assert.False(Assert.Single(after.Document.Bills).Archived);
        Assert.Equal(state.Finance.TotalMinor, after.Finance.TotalMinor);
    }

    [Fact]
    public async Task DeleteFromEditorRequiresConfirmationAndPersistsWithoutSavingOtherFields()
    {
        await Setup(); var first = Bill(100); var other = Bill(200);
        await Store().SaveBillsAsync([first, other], 0);
        var navigation = new Navigation { Confirmation = false };
        var vm = new BudgetEditorViewModel(Store(), navigation); vm.Configure(null, true);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.Bills.Single(b => b.Id == other.Id).Name = "Несохранённое название";
        var row = vm.Bills.Single(b => b.Id == first.Id);
        await vm.DeleteBillCommand.ExecuteAsync(row);
        Assert.Equal(2, vm.Bills.Count);
        Assert.False((await Store().ReadBudgetAsync()).Document.Bills.Single(b => b.Id == first.Id).Archived);
        navigation.Confirmation = true;
        await vm.DeleteBillCommand.ExecuteAsync(row);
        Assert.False(vm.HasError); Assert.Single(vm.Bills);
        Assert.Equal("Несохранённое название", vm.Bills[0].Name);
        var state = await Store().ReadBudgetAsync();
        Assert.True(state.Document.Bills.Single(b => b.Id == first.Id).Archived);
        Assert.Equal(other.Name, state.Document.Bills.Single(b => b.Id == other.Id).Name);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Несохранённое название", (await Store().ReadBudgetAsync()).Document.Bills.Single(b => b.Id == other.Id).Name);
    }

    [Fact]
    public async Task DeletedPaymentKeepsPurposeWhenEditingOldExpenseAndRejectsNewLinks()
    {
        var income = await Setup(); var bill = Bill(2400000); await Allocate(income, bill);
        var expense = new FinanceOperation(Id(), OperationKind.Expense, card.Id, null, 600000, Today, "Аренда", "Синтетика");
        var state = await Store().SaveBudgetOperationAsync(expense, new(expense.Id, true, bill.Id));
        await Store().DeleteBillAsync(bill.Id, state.Revision);
        var vm = new OperationEditorViewModel(Store(), new FinanceNavigation(), Store()); vm.Configure(expense);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(bill.Id, vm.ExpenseChoice!.BillId);
        vm.Note = "Исправленная синтетическая заметка";
        await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError);
        state = await Store().ReadBudgetAsync();
        Assert.Equal(bill.Id, Assert.Single(state.Document.Purposes).BillId);
        var another = expense with { Id = Id() };
        await Assert.ThrowsAsync<FinanceValidationException>(() => Store().SaveBudgetOperationAsync(another, new(another.Id, true, bill.Id)));
        Assert.Equal(2, (await Store().ReadBudgetAsync()).Finance.Operations.Count);
    }

    [Fact]
    public async Task DeletedLastBillDoesNotReappearOrGenerateNextMonth()
    {
        var income = await Setup(); var bill = Bill(500000) with { Monthly = true }; bill = bill with { SeriesId = bill.Id };
        var state = await Store().SaveBillsAsync([bill], 0);
        await Store().DeleteBillAsync(bill.Id, state.Revision);
        var vm = new BudgetEditorViewModel(Store(), new Navigation()); vm.Configure(income.Id);
        await vm.LoadCommand.ExecuteAsync(null); vm.Until = DateTime.Today.AddMonths(2);
        Assert.Empty(vm.Bills); Assert.True(vm.NoBills);
        vm.CushionAccount = vm.Accounts.Single(a => a.Id == cushion.Id);
        await vm.CalculateCommand.ExecuteAsync(null); Assert.True(vm.HasProposal);
        await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError);
        Assert.Empty(Assert.Single((await Store().ReadBudgetAsync()).Document.Allocations).Funding);
    }

    [Fact]
    public async Task UnsavedBlankRowCanBeDeletedEvenWithInvalidAmount()
    {
        await Setup(); var vm = new BudgetEditorViewModel(Store(), new Navigation()); vm.Configure(null, true);
        await vm.LoadCommand.ExecuteAsync(null);
        var row = vm.Bills[0]; row.Amount = "ошибка";
        var revision = (await Store().ReadBudgetAsync()).Revision;
        await vm.DeleteBillCommand.ExecuteAsync(row);
        Assert.False(vm.HasError); Assert.DoesNotContain(row, vm.Bills);
        Assert.Equal(revision, (await Store().ReadBudgetAsync()).Revision);
    }

    [Fact]
    public void OldBillJsonDefaultsToActivePayment()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Bill(100)))!.AsObject();
        json.Remove("Archived");
        var restored = System.Text.Json.JsonSerializer.Deserialize<BudgetBill>(json.ToJsonString())!;
        Assert.False(restored.Archived);
    }

    private sealed class FinanceNavigation : IFinanceNavigation
    {
        public Task EditAccountAsync(Account? account) => Task.CompletedTask;
        public Task EditSalaryAsync() => Task.CompletedTask;
        public Task DistributeIncomeAsync(string id) => Task.CompletedTask;
        public Task EditOperationAsync(FinanceOperation? operation) => Task.CompletedTask;
        public Task BackAsync() => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
    }

    private sealed class Navigation : IBudgetNavigation
    {
        public bool Confirmation { get; set; } = true;
        public Task OpenAllocationAsync(string? incomeId, bool paymentsOnly = false) => Task.CompletedTask;
        public Task RecordTransferAsync(string sourceId, string destinationId, long amount) => Task.CompletedTask;
        public Task AddAccountAsync() => Task.CompletedTask;
        public Task BackAsync() => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirmation);
    }
}
