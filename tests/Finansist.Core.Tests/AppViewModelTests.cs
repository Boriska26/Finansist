using Finansist.Core.Storage;
using Finansist.Core.ViewModels;

namespace Finansist.Core.Tests;

public sealed class AppViewModelTests
{
    [Fact]
    public async Task Initialization_RestoresSavedTheme()
    {
        var theme = new RecordingTheme();
        var vm = new AppViewModel(new StubStore { Theme = ThemePreference.Dark }, theme);
        await vm.InitializeCommand.ExecuteAsync(null);
        Assert.True(vm.IsReady);
        Assert.False(vm.IsBusy);
        Assert.Equal(ThemePreference.Dark, theme.Last);
        Assert.Equal("Тёмная", vm.ThemeStatus);
    }

    [Fact]
    public async Task InitializationFailure_IsVisible_AndRetryRecovers()
    {
        var store = new StubStore { Fail = true };
        var vm = new AppViewModel(store, new RecordingTheme());
        await vm.InitializeCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.False(vm.CanChangeTheme);
        Assert.False(vm.IsBusy);
        store.Fail = false;
        await vm.InitializeCommand.ExecuteAsync(null);
        Assert.True(vm.IsReady);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task FailedSave_DoesNotApplyUnsavedTheme_AndCanBeRetried()
    {
        var store = new StubStore();
        var theme = new RecordingTheme();
        var vm = new AppViewModel(store, theme);
        await vm.InitializeCommand.ExecuteAsync(null);
        store.Fail = true;
        await vm.SetThemeCommand.ExecuteAsync("dark");
        Assert.True(vm.HasError);
        Assert.Equal(ThemePreference.System, theme.Last);
        Assert.Equal(ThemePreference.System, vm.CurrentTheme);
        Assert.False(vm.IsBusy);
        store.Fail = false;
        await vm.SetThemeCommand.ExecuteAsync("dark");
        Assert.False(vm.HasError);
        Assert.Equal(ThemePreference.Dark, store.Theme);
        Assert.Equal(ThemePreference.Dark, theme.Last);
    }

    private sealed class RecordingTheme : IThemeService
    {
        public ThemePreference Last { get; private set; }
        public void Apply(ThemePreference theme) => Last = theme;
    }
    private sealed class StubStore : ILocalStore
    {
        public bool Fail { get; set; }
        public ThemePreference Theme { get; set; }
        public Task<ThemePreference> ReadThemeAsync() => Fail
            ? Task.FromException<ThemePreference>(new StorageException("Синтетическая ошибка"))
            : Task.FromResult(Theme);
        public Task SaveThemeAsync(ThemePreference theme)
        {
            if (Fail) return Task.FromException(new StorageException("Синтетическая ошибка"));
            Theme = theme;
            return Task.CompletedTask;
        }
    }
}
