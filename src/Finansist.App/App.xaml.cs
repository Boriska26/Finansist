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
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(services.GetRequiredService<AppShell>());
}
