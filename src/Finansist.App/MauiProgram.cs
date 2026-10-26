using Finansist.App.Services;
using Finansist.App.Views;
using Finansist.Core.Storage;
using Finansist.Core.Importing;
using Finansist.Core.Budgeting;
using Finansist.Core.Finance;
using Finansist.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Finansist.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddSingleton<ISqliteConnectionFactory>(_ =>
            new AndroidSqliteConnectionFactory(Path.Combine(FileSystem.AppDataDirectory, "finansist.db")));
        builder.Services.AddSingleton<LocalStore>();
        builder.Services.AddSingleton<ILocalStore>(services => services.GetRequiredService<LocalStore>());
        builder.Services.AddSingleton<IFinanceStore>(services => services.GetRequiredService<LocalStore>());
        builder.Services.AddSingleton<IFinanceNavigation, FinanceNavigation>();
        builder.Services.AddSingleton<IBudgetStore>(services => services.GetRequiredService<LocalStore>());
        builder.Services.AddSingleton<IBudgetNavigation, BudgetNavigation>();
        builder.Services.AddSingleton<BudgetViewModel>();
        builder.Services.AddTransient<BudgetEditorViewModel>();
        builder.Services.AddSingleton<BudgetPage>();
        builder.Services.AddSingleton<IBankImportStore>(services => services.GetRequiredService<LocalStore>());
        builder.Services.AddSingleton<INotificationAccess, BankNotificationAccess>();
        builder.Services.AddSingleton<IImportNavigation, ImportNavigation>();
        builder.Services.AddSingleton<ImportInboxViewModel>();
        builder.Services.AddSingleton<FinanceViewModel>();
        builder.Services.AddTransient<AccountEditorViewModel>();
        builder.Services.AddTransient<OperationEditorViewModel>();
        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddSingleton<AppViewModel>();
        builder.Services.AddSingleton<FinancePage>();
        builder.Services.AddSingleton<ShoppingPage>();
        builder.Services.AddSingleton<SettingsPage>();
        builder.Services.AddSingleton<AppShell>();
        return builder.Build();
    }
}
