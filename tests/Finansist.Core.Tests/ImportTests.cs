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
    public async Task SettingsOpensCollectionSetupAndLeavesPendingImportsUnseen()
    {
        await Setup(); var item = await Capture(); var navigation = new ImportNavigation();
        var appState = new AppViewModel(Store(), new TestTheme());
        var vm = new SettingsViewModel(appState, Store(), navigation);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(appState.IsReady); Assert.Equal("Настроить сбор", vm.ImportLabel);
        Assert.Contains(Source.Name, vm.ImportStatus);
        await vm.ImportSettingsCommand.ExecuteAsync(null); Assert.Equal(1, navigation.SettingsOpens); Assert.Equal(0, navigation.Opens);
        Assert.Empty(await Store().ReadSeenImportIdsAsync());
        await Store().DismissImportAsync(item.Id); await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Настроить сбор", vm.ImportLabel);
    }
    [Fact]
    public async Task PausingCollectorKeepsPendingOperationsAvailableForReview()
    {
        await Setup(); var item = await Capture(); var nav = new ImportNavigation();
        var settings = new BankImportSettingsViewModel(Store(), new Access(), nav);
        await settings.RefreshCommand.ExecuteAsync(null);
        Assert.True(settings.HasSource); Assert.False(settings.NeedsAccess);
        Assert.Equal("Приостановить сбор", settings.ToggleLabel);
        await settings.ToggleCommand.ExecuteAsync(null);
        Assert.False((await Store().ReadImportSettingsAsync()).Enabled);
        Assert.Equal("Включить сбор", settings.ToggleLabel);
        Assert.Empty(await Store().ReadSeenImportIdsAsync());
        var queue = new ImportInboxViewModel(Store(), nav);
        await queue.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(item.Id, Assert.Single(queue.Rows).Id);
        Assert.Contains(item.Id, await Store().ReadSeenImportIdsAsync());
        Assert.Empty((await Store().ReadFinanceAsync()).Operations);
        await settings.ToggleCommand.ExecuteAsync(null);
        Assert.True((await Store().ReadImportSettingsAsync()).Enabled);
        Assert.Single((await Store().ReadImportsAsync()).Pending);
    }

    [Fact]
    public async Task CollectionAndMainSettingsReadOnlyConfigurationAndNeverAccessQueue()
    {
        await Setup(); await Capture(); var nav = new ImportNavigation();
        var store = new PausingStore(Store()) { ForbidQueueReads = true };
        var settings = new BankImportSettingsViewModel(store, new Access(), nav);
        await settings.RefreshCommand.ExecuteAsync(null);
        var main = new SettingsViewModel(new AppViewModel(Store(), new TestTheme()), store, nav);
        await main.RefreshCommand.ExecuteAsync(null);
        Assert.False(main.HasError); Assert.False(settings.HasError);
        Assert.Contains(Source.Name, main.ImportStatus); Assert.Contains(Source.Name, settings.SourceStatus);
        Assert.Empty(await Store().ReadSeenImportIdsAsync());
        Assert.Equal(0, nav.Opens);
    }

    [Fact]
    public async Task AccessStatusRefreshesAfterReturningFromAndroidSettings()
    {
        await Setup(); var access = new Access { IsGranted = false, HasCaptureError = true };
        var vm = new BankImportSettingsViewModel(Store(), access, new ImportNavigation());
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.NeedsAccess); Assert.Contains("ошибка", vm.AccessStatus);
        await vm.GrantAccessCommand.ExecuteAsync(null); Assert.Equal(1, access.SettingsRequests);
        access.IsGranted = true; access.HasCaptureError = false;
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.NeedsAccess); Assert.Contains("разрешён", vm.AccessStatus); Assert.DoesNotContain("ошибка", vm.AccessStatus);
    }

    [Fact]
    public async Task SourceSelectionHandlesMissingAppsCancellationAndSuccessfulSetup()
    {
        var access = new Access(); var nav = new ImportNavigation();
        var vm = new BankImportSettingsViewModel(Store(), access, nav);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.HasSource);
        await vm.ToggleCommand.ExecuteAsync(null); Assert.True(vm.HasError);
        await vm.ChooseSourceCommand.ExecuteAsync(null); Assert.True(vm.HasError);
        Assert.Null((await Store().ReadImportSettingsAsync()).PackageId);
        access.Apps = [Source];
        await vm.ChooseSourceCommand.ExecuteAsync(null);
        Assert.False(vm.HasError); Assert.False(vm.HasSource);
        nav.SelectedSource = Source;
        await vm.ChooseSourceCommand.ExecuteAsync(null);
        Assert.True(vm.HasSource); Assert.False(vm.HasError);
        Assert.Equal(Source.PackageId, (await Store().ReadImportSettingsAsync()).PackageId);
        Assert.True((await Store().ReadImportSettingsAsync()).Enabled);
        Assert.Empty((await Store().ReadImportsAsync()).Pending);
        Assert.Empty((await Store().ReadFinanceAsync()).Accounts);
    }

    [Fact]
    public async Task NewImportsOpenOnceAndLaterLeavesPendingAcrossReopen()
    {
        await Setup(); var item = await Capture(); var nav = new ImportNavigation();
        var review = new ImportReviewViewModel(Store(), nav);
        await review.CheckNewCommand.ExecuteAsync(null);
        Assert.Equal(1, nav.Opens); Assert.True(review.HasPending); Assert.Contains("1", review.ButtonText);
        // Navigation alone never acknowledges data: only the displayed inbox does.
        Assert.Empty(await Store().ReadSeenImportIdsAsync());
        var inbox = new ImportInboxViewModel(Store(), nav);
        await inbox.RefreshCommand.ExecuteAsync(null);
        await inbox.LaterCommand.ExecuteAsync(null);
        Assert.Equal(1, nav.Closes); Assert.Contains(item.Id, await Store().ReadSeenImportIdsAsync());
        await new ImportReviewViewModel(Store(), nav).CheckNewCommand.ExecuteAsync(null);
        Assert.Equal(1, nav.Opens);
        Assert.Single((await Store().ReadImportsAsync()).Pending);
        Assert.Empty((await Store().ReadFinanceAsync()).Operations);
        await Capture();
        await review.CheckNewCommand.ExecuteAsync(null); Assert.Equal(2, nav.Opens); Assert.Equal(2, review.PendingCount);
    }

    [Fact]
    public async Task FooterRefreshDoesNotAutoNavigateAndProcessedQueueHidesButton()
    {
        await Setup(); var item = await Capture(); var nav = new ImportNavigation();
        var review = new ImportReviewViewModel(Store(), nav);
        await review.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(0, nav.Opens); Assert.True(review.HasPending);
        await review.OpenInboxCommand.ExecuteAsync(null); Assert.Equal(1, nav.Opens);
        await Store().DismissImportAsync(item.Id);
        await review.RefreshCommand.ExecuteAsync(null);
        Assert.False(review.HasPending); Assert.Equal(0, review.PendingCount);
        await review.CheckNewCommand.ExecuteAsync(null); Assert.Equal(1, nav.Opens);
    }

    [Fact]
    public async Task BusyEditorKeepsImportsUnseenForNextForegroundCheck()
    {
        await Setup(); await Capture(); var nav = new ImportNavigation { CanOpen = false };
        var review = new ImportReviewViewModel(Store(), nav);
        await review.CheckNewCommand.ExecuteAsync(null);
        Assert.Equal(0, nav.Opens); Assert.Empty(await Store().ReadSeenImportIdsAsync());
        nav.CanOpen = true;
        await review.CheckNewCommand.ExecuteAsync(null); Assert.Equal(1, nav.Opens);
    }

    [Fact]
    public async Task MarkingLoadedRowsDoesNotHidePushReceivedAfterSnapshot()
    {
        await Setup(); var firstItem = await Capture(); var secondItem = await Capture();
        await Store().MarkImportsSeenAsync([firstItem.Id, Guid.NewGuid().ToString()]);
        var seen = await Store().ReadSeenImportIdsAsync();
        Assert.Single(seen); Assert.Contains(firstItem.Id, seen); Assert.DoesNotContain(secondItem.Id, seen);
        await Store().DismissImportAsync(firstItem.Id);
        await Store().MarkImportsSeenAsync([]);
        Assert.Empty(await Store().ReadSeenImportIdsAsync());
        Assert.Single((await Store().ReadImportsAsync()).Pending);
    }

    [Fact]
    public async Task AutoCheckArrivingDuringFooterRefreshIsNotLost()
    {
        await Setup(); await Capture(); var nav = new ImportNavigation(); var store = new PausingStore(Store());
        var review = new ImportReviewViewModel(store, nav);
        var refresh = review.RefreshCommand.ExecuteAsync(null);
        await store.Started.Task;
        await review.CheckNewCommand.ExecuteAsync(null);
        store.Release.SetResult(); await refresh;
        Assert.Equal(1, nav.Opens); Assert.Equal(1, review.PendingCount);
    }

    [Fact]
    public async Task ForegroundStorageErrorIsShownWithoutMarkingOrOpeningQueue()
    {
        await Setup(); await Capture();
        var nav = new ImportNavigation();
        var review = new ImportReviewViewModel(new LocalStore(new FailureFactory(DatabasePath, "BEGIN IMMEDIATE")), nav);
        await review.CheckNewCommand.ExecuteAsync(null);
        Assert.True(review.HasError); Assert.False(review.IsBusy); Assert.Equal(0, nav.Opens);
        Assert.Empty(await Store().ReadSeenImportIdsAsync());
    }

    private sealed class Access : INotificationAccess
    {
        public bool IsGranted { get; set; } = true;
        public bool HasCaptureError { get; set; }
        public int SettingsRequests { get; private set; }
        public IReadOnlyList<NotificationSource> Apps { get; set; } = [];
        public Task<IReadOnlyList<NotificationSource>> GetBankAppsAsync() => Task.FromResult(Apps);
        public Task OpenSettingsAsync() { SettingsRequests++; return Task.CompletedTask; }
    }
    private sealed class PausingStore(IBankImportStore inner) : IBankImportStore
    {
        private bool pause = true;
        public bool ForbidQueueReads { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ImportSnapshot> ReadImportsAsync()
        {
            if (ForbidQueueReads) throw new InvalidOperationException("Synthetic: configuration must not read the queue.");
            if (pause) { pause = false; Started.SetResult(); await Release.Task; }
            return await inner.ReadImportsAsync();
        }
        public Task<IReadOnlySet<string>> ReadSeenImportIdsAsync() => ForbidQueueReads ? throw new InvalidOperationException("Synthetic: configuration must not read seen IDs.") : inner.ReadSeenImportIdsAsync();
        public Task MarkImportsSeenAsync(IReadOnlyList<string> ids) => ForbidQueueReads ? throw new InvalidOperationException("Synthetic: configuration must not acknowledge queue.") : inner.MarkImportsSeenAsync(ids);
        public Task<ImportSettings> ReadImportSettingsAsync() => inner.ReadImportSettingsAsync();
        public Task ConfigureImportAsync(NotificationSource source, bool enabled) => inner.ConfigureImportAsync(source, enabled);
        public Task<bool> ReceivePushAsync(BankPush push) => inner.ReceivePushAsync(push);
        public Task<ImportRow> ReadImportAsync(string id) => inner.ReadImportAsync(id);
        public Task DismissImportAsync(string id) => inner.DismissImportAsync(id);
        public Task<string> ConfirmImportAsync(string id, FinanceOperation operation, ExpensePurpose? purpose, string? existingOperationId = null) => inner.ConfirmImportAsync(id, operation, purpose, existingOperationId);
    }

    private sealed class TestTheme : IThemeService { public void Apply(ThemePreference theme) { } }
    private sealed class ImportNavigation : IImportNavigation
    {
        public int Opens { get; private set; }
        public int SettingsOpens { get; private set; }
        public Task OpenSettingsAsync() { SettingsOpens++; return Task.CompletedTask; }
        public bool CanOpen { get; set; } = true;
        public int Closes { get; private set; }
        public Task<bool> TryOpenInboxAsync() { if (!CanOpen) return Task.FromResult(false); Opens++; return Task.FromResult(true); }
        public Task CloseInboxAsync() { Closes++; return Task.CompletedTask; }
        public Task OpenInboxAsync() { Opens++; return Task.CompletedTask; }
        public Task ReviewAsync(string id) => Task.CompletedTask;
        public NotificationSource? SelectedSource { get; set; }
        public Task<NotificationSource?> ChooseSourceAsync(IReadOnlyList<NotificationSource> sources) => Task.FromResult(SelectedSource);
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
