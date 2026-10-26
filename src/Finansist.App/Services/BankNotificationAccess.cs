using Android.Content;
using Android.Content.PM;
using Finansist.Core.Importing;

namespace Finansist.App.Services;

public sealed class BankNotificationAccess : INotificationAccess
{
    public bool IsGranted
    {
        get
        {
            var context = Android.App.Application.Context;
            var component = new ComponentName(context.PackageName!, BankNotificationListener.ServiceName);
            if (OperatingSystem.IsAndroidVersionAtLeast(27))
                return (context.GetSystemService(Context.NotificationService) as Android.App.NotificationManager)?.IsNotificationListenerAccessGranted(component) == true;
            var enabled = Android.Provider.Settings.Secure.GetString(context.ContentResolver, "enabled_notification_listeners") ?? "";
            return enabled.Split(':').Any(name => name == component.FlattenToString());
        }
    }
    public bool HasCaptureError => Preferences.Default.Get("bank_capture_failed", false);
    public Task<IReadOnlyList<NotificationSource>> GetBankAppsAsync() => Task.Run<IReadOnlyList<NotificationSource>>(() =>
    {
        var manager = Android.App.Application.Context.PackageManager;
        if (manager is null) return [];
        using var intent = new Intent(Intent.ActionMain); intent.AddCategory(Intent.CategoryLauncher);
        var apps = manager.QueryIntentActivities(intent, (PackageInfoFlags)0) ?? [];
        return apps.Where(a => a.ActivityInfo is not null).Select(a => new NotificationSource(a.ActivityInfo!.PackageName!, a.LoadLabel(manager)?.ToString() ?? ""))
            .Where(a => a.Name.Contains("ВБРР", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("VBRR", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(a => a.PackageId).OrderBy(a => a.Name).ToArray();
    });
    public Task OpenSettingsAsync()
    {
        try
        {
            using var intent = new Intent(Android.Provider.Settings.ActionNotificationListenerSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
            return Task.CompletedTask;
        }
        catch (ActivityNotFoundException) { throw new Finansist.Core.Finance.FinanceValidationException("Не удалось открыть настройки. Откройте доступ к уведомлениям для «Финансиста» в настройках Android."); }
    }
}
