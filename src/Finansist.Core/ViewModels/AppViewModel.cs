using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Finansist.Core.Storage;

namespace Finansist.Core.ViewModels;

public interface IThemeService
{
    void Apply(ThemePreference theme);
}

// Shared state makes initialization/errors visible from every tab.
public partial class AppViewModel(ILocalStore store, IThemeService themeService) : ObservableObject
{
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isReady;
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private string storageStatus = "Подготавливаем локальное хранилище…";
    [ObservableProperty] private string themeStatus = "Как на устройстве";
    [ObservableProperty] private ThemePreference currentTheme;

    public bool HasError => ErrorMessage.Length > 0;
    public bool CanChangeTheme => IsReady && !IsBusy;

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsReadyChanged(bool value) => OnPropertyChanged(nameof(CanChangeTheme));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanChangeTheme));

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = "";
        try
        {
            ApplyTheme(await store.ReadThemeAsync());
            IsReady = true;
            StorageStatus = $"SQLite · схема {LocalStore.SchemaVersion} · на этом устройстве";
        }
        catch (StorageException exception)
        {
            IsReady = false;
            StorageStatus = "Хранилище недоступно";
            ErrorMessage = exception.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SetThemeAsync(string choice)
    {
        if (!CanChangeTheme) return;
        var theme = choice switch
        {
            "light" => ThemePreference.Light,
            "dark" => ThemePreference.Dark,
            _ => ThemePreference.System
        };
        IsBusy = true;
        ErrorMessage = "";
        try
        {
            await store.SaveThemeAsync(theme);
            ApplyTheme(theme);
        }
        catch (StorageException exception) { ErrorMessage = exception.Message; }
        finally { IsBusy = false; }
    }

    private void ApplyTheme(ThemePreference theme)
    {
        themeService.Apply(theme);
        CurrentTheme = theme;
        ThemeStatus = theme switch
        {
            ThemePreference.Light => "Светлая",
            ThemePreference.Dark => "Тёмная",
            _ => "Как на устройстве"
        };
    }
}
