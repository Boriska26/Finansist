using Microsoft.Extensions.DependencyInjection;

namespace Finansist.App;

public partial class App : Application
{
    private readonly IServiceProvider services;
    public App(IServiceProvider services)
    {
        InitializeComponent();
        this.services = services;
    }

    // Resolve pages only after the application resources have been loaded.
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(services.GetRequiredService<AppShell>());
        window.Resumed += (_, _) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            await services.GetRequiredService<Finansist.Core.ViewModels.FinanceViewModel>().RefreshCommand.ExecuteAsync(null);
            await services.GetRequiredService<Finansist.Core.ViewModels.ImportInboxViewModel>().RefreshCommand.ExecuteAsync(null);
        });
        return window;
    }
}
