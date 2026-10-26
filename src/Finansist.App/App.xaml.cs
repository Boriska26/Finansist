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
        var shell = services.GetRequiredService<AppShell>();
        var review = services.GetRequiredService<Finansist.Core.ViewModels.ImportReviewViewModel>();
        var window = new Window(shell);
        shell.Loaded += async (_, _) => await review.CheckNewCommand.ExecuteAsync(null);
        window.Resumed += (_, _) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            await services.GetRequiredService<Finansist.Core.ViewModels.FinanceViewModel>().RefreshCommand.ExecuteAsync(null);
            await services.GetRequiredService<Finansist.Core.ViewModels.SettingsViewModel>().RefreshCommand.ExecuteAsync(null);
            if (shell.CurrentPage is Views.ImportInboxPage)
                await services.GetRequiredService<Finansist.Core.ViewModels.ImportInboxViewModel>().RefreshCommand.ExecuteAsync(null);
            await review.CheckNewCommand.ExecuteAsync(null);
        });
        return window;
    }
}
