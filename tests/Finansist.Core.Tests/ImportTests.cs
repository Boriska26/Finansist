using Finansist.Core.Budgeting;
using Finansist.Core.Finance;
using Finansist.Core.Importing;
using Finansist.Core.Storage;
using Finansist.Core.ViewModels;

namespace Finansist.Core.Tests;

public sealed class ImportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "finansist-import-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(directory, "synthetic.db");
    private LocalStore Store() => new(new TestConnectionFactory(DatabasePath));
    private static string Id() => Guid.NewGuid().ToString();
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private readonly Account first = new(Id(), "А — синтетическая карта", 1000000);
    private readonly Account second = new(Id(), "Б — синтетическая карта", 2000000);
    private static readonly NotificationSource Source = new("synthetic.bank.vbrr", "Синтетический банк");
    public ImportTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private static BankPush Push(string text = "Карта*0000 оплата 321.09 р ТЕСТОВЫЙ МАГАЗИН Баланс: 9000.00 р") =>
        new(Source.PackageId, Id(), DateTimeOffset.UtcNow, "Банк — синтетика", text);
    private async Task Setup()
    {
        await Store().SaveAccountAsync(first); await Store().SaveAccountAsync(second);
        await Store().ConfigureImportAsync(Source, true);
    }
    private async Task<ImportCandidate> Capture(BankPush? push = null)
    {
        Assert.True(await Store().ReceivePushAsync(push ?? Push()));
        return (await Store().ReadImportsAsync()).Pending.Last().Candidate;
    }
    private FinanceOperation Operation(ImportCandidate item, OperationKind kind = OperationKind.Expense) =>
        new(item.Id, kind, first.Id, kind == OperationKind.Transfer ? second.Id : null,
            item.Parsed.AmountMinor ?? 100, Today, "Синтетика", "Тестовая операция");

    [Theory]
    [InlineData("Пополнение СБП 4321.09 р 09:12 Баланс: 9000.00 р от ТЕСТ", 432109L, OperationKind.Income, false)]
    [InlineData("Popolnenie 6543.21 R 12:23 Balans: 9000.00 R", 654321L, OperationKind.Income, false)]
    [InlineData("Карта*0000 оплата 321.09 р ТЕСТОВЫЙ МАГАЗИН Баланс: 9000.00 р", 32109L, OperationKind.Expense, false)]
    [InlineData("Karta*0000 oplata 87.65 R TEST SHOP Balans: 9000.00 R", 8765L, OperationKind.Expense, false)]
    [InlineData("Списание СБП 765.43 р 10:01 Баланс: 9000.00 р", 76543L, OperationKind.Expense, true)]
    [InlineData("Spisanie SBP 765.43 R 10:01 Balans: 9000.00 R", 76543L, OperationKind.Expense, true)]
    [InlineData("Оплата 1 234,56 ₽ Баланс 9000,00 ₽", 123456L, OperationKind.Expense, false)]
    public void ParserRecognizesRussianAndLatinWithoutUsingBalance(string text, long amount, OperationKind kind, bool ambiguous)
    {
        var parsed = VbrrPushParser.Parse(Push(text));
        Assert.NotNull(parsed); Assert.Equal(amount, parsed.AmountMinor); Assert.Equal(kind, parsed.Kind); Assert.Equal(ambiguous, parsed.Ambiguous);
        Assert.Equal(Today, parsed.Date);
    }
    [Theory]
    [InlineData("Добро пожаловать в банк")]
    [InlineData("Ваш код входа 000000")]
    [InlineData("Kod podtverzhdeniya 000000. Spisanie SBP 765.43 R")]
    [InlineData("Код подтверждения списания: 000000. Списание СБП 765.43 R")]
    public void ParserIgnoresNonTransactionsAndAuthentication(string text) => Assert.Null(VbrrPushParser.Parse(Push(text)));
    [Fact] public void SummaryAndOversizedPushAreNotParsed()
    {
        Assert.Null(VbrrPushParser.Parse(Push() with { IsGroupSummary = true }));
        Assert.Null(VbrrPushParser.Parse(Push(new string('a', 9000))));
    }
    [Fact] public void MissingAmountDoesNotSubstituteBalance()
    {
        var parsed = VbrrPushParser.Parse(Push("Списание СБП неизвестно Баланс: 9876.54 р"));
        Assert.NotNull(parsed); Assert.Null(parsed.AmountMinor); Assert.True(parsed.Ambiguous);
    }
    [Fact] public void ParserKeepsTimeAsInformationAndDoesNotChangeReceiptDate()
    {
        var push = Push("Popolnenie 6543.21 R 01:23 Balans: 9000.00 R");
        var parsed = VbrrPushParser.Parse(push)!;
        Assert.Equal("01:23", parsed.MessageTime); Assert.Equal(DateOnly.FromDateTime(push.PostedAt.LocalDateTime), parsed.Date);
    }
    [Fact] public void DuplicateTitleDoesNotLookLikeTwoOperations()
    {
        var push = Push("Пополнение 123.45 R") with { Title = "Пополнение 123.45 R" };
        Assert.Equal(12345, VbrrPushParser.Parse(push)!.AmountMinor);
        Assert.Null(VbrrPushParser.Parse(Push("Оплата 123.45 R Баланс 500 R\nОплата 234.56 R Баланс 200 R"))!.AmountMinor);
    }
    [Fact]
    public async Task CollectionIsOffByDefaultAndFiltersSourceAndOldNotifications()
    {
        var push = Push(); Assert.False(await Store().ReceivePushAsync(push));
        await Setup();
        Assert.False(await Store().ReceivePushAsync(push with { PostedAt = DateTimeOffset.UtcNow.AddDays(-1) }));
        Assert.False(await Store().ReceivePushAsync(Push() with { PackageId = "another.synthetic.app" }));
        Assert.Empty((await Store().ReadImportsAsync()).Pending);
        await Store().ConfigureImportAsync(Source, false);
        Assert.False(await Store().ReceivePushAsync(Push()));
    }
    [Fact]
    public async Task CapturePersistsQueueWithoutAffectingLedgerAndRemembersFirstAccount()
    {
        await Setup(); await Store().SaveAccountLayoutAsync(new([second.Id, first.Id], false));
        var before = await Store().ReadFinanceAsync(); var item = await Capture();
        Assert.Equal(second.Id, item.DefaultAccountId);
        await Store().SaveAccountLayoutAsync(new([first.Id, second.Id], false));
        Assert.Equal(second.Id, (await Store().ReadImportAsync(item.Id)).Candidate.DefaultAccountId);
        var after = await Store().ReadFinanceAsync();
        Assert.Equal(before.TotalMinor, after.TotalMinor); Assert.Empty(after.Operations);
        Assert.Single((await Store().ReadImportsAsync()).Pending);
    }
    [Fact]
    public async Task ExactRedeliveryIsIgnoredButSeparateIdenticalNotificationIsKeptForReview()
    {
        await Setup(); var push = Push(); var firstItem = await Capture(push);
        Assert.False(await Store().ReceivePushAsync(push));
        var another = await Capture(push with { Key = Id() });
        Assert.True(another.PossibleRepeat); Assert.NotEqual(firstItem.Id, another.Id);
        Assert.Equal(2, (await Store().ReadImportsAsync()).Pending.Count);
    }
    [Fact]
    public async Task ConfirmWritesEditedAccountAndAmountExactlyOnce()
    {
        await Setup(); var item = await Capture(); var edited = Operation(item) with { AccountId = second.Id, AmountMinor = 45678 };
        await Store().ConfirmImportAsync(item.Id, edited, new(item.Id, true));
        Assert.Empty((await Store().ReadImportsAsync()).Pending);
        var finance = await Store().ReadFinanceAsync(); Assert.Equal(edited, Assert.Single(finance.Operations));
        Assert.Equal(1954322, finance.Accounts.Single(a => a.Id == second.Id).BalanceMinor);
        await Assert.ThrowsAsync<FinanceValidationException>(() => Store().ConfirmImportAsync(item.Id, edited, null));
        Assert.Single((await Store().ReadFinanceAsync()).Operations);
        Assert.Single((await Store().ReadBudgetAsync()).Document.Purposes);
    }
    [Fact]
    public async Task RejectionIsPersistentAndDoesNotCreateOperationOrRequeue()
    {
        await Setup(); var push = Push(); var item = await Capture(push);
        await Store().DismissImportAsync(item.Id);
        Assert.False(await Store().ReceivePushAsync(push));
        Assert.Empty((await Store().ReadImportsAsync()).Pending); Assert.Empty((await Store().ReadFinanceAsync()).Operations);
        await Assert.ThrowsAsync<FinanceValidationException>(() => Store().ConfirmImportAsync(item.Id, Operation(item), null));
    }
    [Fact]
    public async Task ConfirmationFailureRollsBackLedgerBudgetAndQueueTogether()
    {
        await Setup(); var item = await Capture(); var failing = new LocalStore(new FailureFactory(DatabasePath, "COMMIT"));
        await Assert.ThrowsAsync<StorageException>(() => failing.ConfirmImportAsync(item.Id, Operation(item), new(item.Id, true)));
        Assert.Single((await Store().ReadImportsAsync()).Pending); Assert.Empty((await Store().ReadFinanceAsync()).Operations);
        Assert.Empty((await Store().ReadBudgetAsync()).Document.Purposes);
        await Assert.ThrowsAsync<FinanceValidationException>(() => Store().ConfirmImportAsync(item.Id, Operation(item) with { AmountMinor = -1 }, null));
        Assert.Single((await Store().ReadImportsAsync()).Pending);
    }
    [Fact]
    public async Task LinkManualEntryDoesNotDuplicateIt()
    {
        await Setup(); var item = await Capture(); var manual = Operation(item) with { Id = Id() };
        await Store().SaveOperationAsync(manual);
        var before = (await Store().ReadFinanceAsync()).TotalMinor;
        await Store().ConfirmImportAsync(item.Id, Operation(item), null, manual.Id);
        Assert.Single((await Store().ReadFinanceAsync()).Operations); Assert.Equal(before, (await Store().ReadFinanceAsync()).TotalMinor);
        Assert.Equal(manual.Id, (await Store().ReadImportAsync(item.Id)).OperationId);
    }
    [Fact]
    public async Task TwoSidesOfTransferCanLinkToOneTransferWithoutIncomeExpense()
    {
        await Setup(); var outgoing = await Capture(Push("Списание СБП 765.43 р 10:01 Баланс: 9000.00 р"));
        var transfer = Operation(outgoing, OperationKind.Transfer);
        var before = (await Store().ReadFinanceAsync()).TotalMinor;
        await Store().ConfirmImportAsync(outgoing.Id, transfer, null);
        var incoming = await Capture(Push("Пополнение СБП 765.43 р 10:02 Баланс: 9000.00 р"));
        var proposal = Operation(incoming, OperationKind.Income) with { AccountId = second.Id };
        await Store().ConfirmImportAsync(incoming.Id, proposal, null, transfer.Id);
        var after = await Store().ReadFinanceAsync();
        Assert.Equal(OperationKind.Transfer, Assert.Single(after.Operations).Kind); Assert.Equal(before, after.TotalMinor);
        Assert.Empty((await Store().ReadImportsAsync()).Pending);
    }
    [Fact]
    public async Task IncorrectManualLinkIsRejectedAndQueueIsKept()
    {
        await Setup(); var item = await Capture(); var manual = Operation(item) with { Id = Id(), AmountMinor = 1 };
        await Store().SaveOperationAsync(manual);
        await Assert.ThrowsAsync<FinanceValidationException>(() => Store().ConfirmImportAsync(item.Id, Operation(item), null, manual.Id));
        Assert.Single((await Store().ReadImportsAsync()).Pending);
    }
    [Fact]
    public async Task ReviewRequiresAmbiguousTypeAndDoesNotAssumeSalary()
    {
        await Setup(); var item = await Capture(Push("Spisanie SBP 765.43 R")); var navigation = new Navigation();
        var vm = new OperationEditorViewModel(Store(), navigation, Store(), Store()); vm.ConfigureImport(item);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(-1, vm.KindIndex); Assert.Equal(first.Id, vm.SelectedAccount!.Id); Assert.False(vm.DistributeIncome);
        await vm.SaveCommand.ExecuteAsync(null); Assert.True(vm.HasError); Assert.Single((await Store().ReadImportsAsync()).Pending);
        vm.KindIndex = (int)OperationKind.Transfer; vm.DestinationAccount = vm.Accounts.Single(a => a.Id == second.Id);
        await vm.SaveCommand.ExecuteAsync(null); Assert.False(vm.HasError); Assert.True(vm.IsSaved);
        Assert.Equal(OperationKind.Transfer, Assert.Single((await Store().ReadFinanceAsync()).Operations).Kind);
    }
    [Fact]
    public async Task ReviewExplicitlyLinksDuplicateAndOnlyOffersSalaryWhenUserSelectsIt()
    {
        await Setup(); var item = await Capture(Push("Пополнение 123.45 р")); var manual = Operation(item, OperationKind.Income) with { Id = Id() };
        await Store().SaveOperationAsync(manual);
        var navigation = new Navigation(); var vm = new OperationEditorViewModel(Store(), navigation, Store(), Store()); vm.ConfigureImport(item);
        await vm.LoadCommand.ExecuteAsync(null); await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.HasError); Assert.False(vm.IsSaved);
        vm.SelectedExisting = vm.ExistingChoices.Single(c => c.Id == manual.Id);
        await vm.SaveCommand.ExecuteAsync(null); Assert.True(vm.IsSaved); Assert.Single((await Store().ReadFinanceAsync()).Operations);
        Assert.Equal(0, navigation.Distributions);
        var salary = await Capture(Push("Пополнение 234.56 р")); var salaryVm = new OperationEditorViewModel(Store(), navigation, Store(), Store()); salaryVm.ConfigureImport(salary);
        await salaryVm.LoadCommand.ExecuteAsync(null); salaryVm.DistributeIncome = true;
        await salaryVm.SaveCommand.ExecuteAsync(null); Assert.True(salaryVm.IsSaved); Assert.Equal(1, navigation.Distributions);
    }
    [Fact]
    public async Task MigrationFromFourPreservesFinanceBudgetAndAccountOrder()
    {
        await Setup(); await Store().SaveAccountLayoutAsync(new([second.Id, first.Id], false));
        await Store().SaveThemeAsync(ThemePreference.Dark);
        var bill = new BudgetBill(Id(), "Синтетическая аренда", Today, 50000);
        await Store().SaveBillsAsync([bill], 0);
        using (var db = new NativeSqliteConnection(DatabasePath)) { db.Execute("DROP TABLE notification_imports"); db.Execute("PRAGMA user_version = 4"); }
        Assert.Empty((await Store().ReadImportsAsync()).Pending); Assert.Equal(second.Id, (await Store().ReadFinanceAsync()).Accounts[0].Id);
        Assert.Equal(bill, Assert.Single((await Store().ReadBudgetAsync()).Document.Bills));
        Assert.Equal(ThemePreference.Dark, await Store().ReadThemeAsync());
        using var check = new NativeSqliteConnection(DatabasePath); Assert.Equal("5", check.Scalar("PRAGMA user_version"));
    }
    [Fact]
    public async Task FailedMigrationRollsBackAndRetainsSchemaFour()
    {
        await Setup();
        using (var db = new NativeSqliteConnection(DatabasePath)) { db.Execute("DROP TABLE notification_imports"); db.Execute("PRAGMA user_version = 4"); }
        var failing = new LocalStore(new FailureFactory(DatabasePath, "PRAGMA user_version = 5"));
        await Assert.ThrowsAsync<StorageException>(() => failing.ReadImportsAsync());
        using var check = new NativeSqliteConnection(DatabasePath);
        Assert.Equal("4", check.Scalar("PRAGMA user_version")); Assert.Equal("0", check.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='notification_imports'"));
        Assert.Equal("2", check.Scalar("SELECT COUNT(*) FROM accounts"));
    }
    [Fact]
    public async Task SettingsShowsPendingCounterAndOpensNotificationInbox()
    {
        await Setup(); var item = await Capture(); var navigation = new ImportNavigation();
        var appState = new AppViewModel(Store(), new TestTheme());
        var vm = new SettingsViewModel(appState, Store(), navigation);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(appState.IsReady); Assert.Contains("1 на проверку", vm.ImportLabel);
        Assert.Contains(Source.Name, vm.ImportStatus);
        await vm.ImportInboxCommand.ExecuteAsync(null); Assert.Equal(1, navigation.Opens);
        await Store().DismissImportAsync(item.Id); await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Банковские уведомления", vm.ImportLabel);
    }
    private sealed class TestTheme : IThemeService { public void Apply(ThemePreference theme) { } }
    private sealed class ImportNavigation : IImportNavigation
    {
        public int Opens { get; private set; }
        public Task OpenInboxAsync() { Opens++; return Task.CompletedTask; }
        public Task ReviewAsync(string id) => Task.CompletedTask;
        public Task<NotificationSource?> ChooseSourceAsync(IReadOnlyList<NotificationSource> sources) => Task.FromResult<NotificationSource?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
    }

    private sealed class Navigation : IFinanceNavigation
    {
        public int Distributions { get; private set; }
        public Task EditAccountAsync(Account? account) => Task.CompletedTask;
        public Task EditSalaryAsync() => Task.CompletedTask;
        public Task DistributeIncomeAsync(string id) { Distributions++; return Task.CompletedTask; }
        public Task EditOperationAsync(FinanceOperation? operation) => Task.CompletedTask;
        public Task BackAsync() => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
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
}
