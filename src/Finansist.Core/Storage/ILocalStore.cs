namespace Finansist.Core.Storage;

public enum ThemePreference { System, Light, Dark }

public interface ILocalStore
{
    Task<ThemePreference> ReadThemeAsync();
    Task SaveThemeAsync(ThemePreference theme);
}

public sealed class StorageException(string message, Exception? inner = null) : Exception(message, inner);
