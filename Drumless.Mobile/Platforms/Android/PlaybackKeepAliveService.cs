using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Drumless.Mobile;

[Service(
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class PlaybackKeepAliveService : Service
{
    private const string ChannelId = "drumless_playback";
    private const int NotificationId = 2407;

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureNotificationChannel();

        // Android gives a service launched through StartForegroundService only a few
        // seconds to promote itself. Do it immediately, and explicitly provide the
        // mediaPlayback type on API 29+ so Android 14+ can validate the declaration.
        PromoteToForeground();
    }

    public override StartCommandResult OnStartCommand(
        Intent? intent,
        StartCommandFlags flags,
        int startId)
    {
        // Re-promoting is harmless and also covers restarts of a sticky service.
        PromoteToForeground();
        return StartCommandResult.Sticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public static void Start()
    {
        var context = Android.App.Application.Context;
        var intent = new Intent(context, typeof(PlaybackKeepAliveService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    public static void Stop()
    {
        var context = Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(PlaybackKeepAliveService)));
    }

    private void PromoteToForeground()
    {
        var notification = BuildNotification();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            StartForeground(
                NotificationId,
                notification,
                ForegroundService.TypeMediaPlayback);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    private void EnsureNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            return;
        }

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager?.GetNotificationChannel(ChannelId) is not null)
        {
            return;
        }

        var channel = new NotificationChannel(
            ChannelId,
            "Reproducción de Drumless Play",
            NotificationImportance.Low)
        {
            Description = "Mantiene la playlist de Drumless Play activa en segundo plano"
        };
        manager?.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification()
    {
        var launchIntent = new Intent(this, typeof(MainActivity));
        launchIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        Notification.Builder builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        return builder
            .SetContentTitle("Drumless Play")
            .SetContentText("Playlist activa en segundo plano")
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .Build();
    }
}
