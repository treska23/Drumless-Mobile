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
        PromoteToForeground();
    }

    public override StartCommandResult OnStartCommand(
        Intent? intent,
        StartCommandFlags flags,
        int startId)
    {
        PromoteToForeground();

        // Never ask Android to resurrect this service after the app/process has been killed.
        // A sticky restart can create a crash/restart loop if Android no longer allows a
        // foreground-service promotion in the new background state.
        return StartCommandResult.NotSticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public static bool TryStart()
    {
        try
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

            return true;
        }
        catch (Exception)
        {
            // Playback should degrade gracefully instead of taking down the app if Android
            // refuses a foreground-service start in the current lifecycle state.
            return false;
        }
    }

    public static void Start() => TryStart();

    public static void Stop()
    {
        try
        {
            var context = Android.App.Application.Context;
            context.StopService(new Intent(context, typeof(PlaybackKeepAliveService)));
        }
        catch (Exception)
        {
            // The service may already be gone with the process.
        }
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
