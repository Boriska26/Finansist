using Finansist.Core.Storage;
using Finansist.Core.ViewModels;

namespace Finansist.App.Services;

public sealed class ThemeService : IThemeService
{
    public void Apply(ThemePreference theme)
    {
        if (Application.Current is { } app)
            app.UserAppTheme = theme switch
            {
                ThemePreference.Light => AppTheme.Light,
                ThemePreference.Dark => AppTheme.Dark,
                _ => AppTheme.Unspecified
            };
    }
}
