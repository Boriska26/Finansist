using Android.App;
using Android.Content;
using Android.Service.Notification;
using Finansist.Core.Importing;
using Microsoft.Extensions.DependencyInjection;

namespace Finansist.App.Services;

[Service(Name = ServiceName, Label = "Финансист — банковские уведомления", Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE", Exported = true)]
[IntentFilter(["android.service.notification.NotificationListenerService"])]
public sealed class BankNotificationListener : NotificationListenerService
{
    public const string ServiceName = "ru.finansist.personal.BankNotificationListener";
    public override async void OnNotificationPosted(StatusBarNotification? notification)
    {
        if (notification is not null) await CaptureAsync(notification);
    }
    public override async void OnListenerConnected()
    {
        base.OnListenerConnected();
        try
        {
            foreach (var notification in GetActiveNotifications() ?? []) await CaptureAsync(notification);
        }
        catch (Exception) { Preferences.Default.Set("bank_capture_failed", true); }
    }
    private static async Task CaptureAsync(StatusBarNotification notification)
    {
        try
        {
            var store = IPlatformApplication.Current?.Services.GetService<IBankImportStore>();
            if (store is null) return;
            var settings = await store.ReadImportSettingsAsync();
            // Inspect message content only for the user's chosen bank.
            if (!settings.Enabled || notification.PackageName != settings.PackageId) return;
            var native = notification.Notification;
            if (native is null || (native.Flags & NotificationFlags.GroupSummary) != 0) return;
            var extras = native.Extras;
            var title = extras?.GetCharSequence(Notification.ExtraTitle)?.ToString() ?? "";
            var text = extras?.GetCharSequence(Notification.ExtraBigText)?.ToString();
            if (string.IsNullOrWhiteSpace(text)) text = extras?.GetCharSequence(Notification.ExtraText)?.ToString();
            if (string.IsNullOrWhiteSpace(text))
                text = string.Join("\n", extras?.GetCharSequenceArray(Notification.ExtraTextLines)?.Select(x => x?.ToString()) ?? []);
            if (title.Length + text.Length > 7999) return;
            var push = new BankPush(notification.PackageName!, notification.Key ?? notification.Id.ToString(),
                DateTimeOffset.FromUnixTimeMilliseconds(notification.PostTime), title, text);
            if (await store.ReceivePushAsync(push)) Preferences.Default.Set("bank_capture_failed", false);
        }
        catch (Exception)
        {
            // Never log exceptions or message content. Surface a neutral health indicator in the UI.
            Preferences.Default.Set("bank_capture_failed", true);
        }
    }
}
