using Finansist.App.Views;

namespace Finansist.App;

public sealed class AppShell : Shell
{
    public AppShell(FinancePage finance, BudgetPage budget, ShoppingPage shopping, SettingsPage settings)
    {
        FlyoutBehavior = FlyoutBehavior.Disabled;
        SetNavBarIsVisible(this, false);
        var tabs = new TabBar();
        tabs.Items.Add(CreateTab("Финансы", "wallet.png", "finance", finance));
        tabs.Items.Add(CreateTab("Бюджет", "budget.png", "budget", budget));
        tabs.Items.Add(CreateTab("Покупки", "basket.png", "shopping", shopping));
        tabs.Items.Add(CreateTab("Настройки", "settings.png", "settings", settings));
        Items.Add(tabs);
    }

    private static Tab CreateTab(string title, string icon, string route, ContentPage page)
    {
        var tab = new Tab { Title = title, Icon = icon, Route = route };
        tab.Items.Add(new ShellContent { Title = title, Content = page });
        return tab;
    }
}
