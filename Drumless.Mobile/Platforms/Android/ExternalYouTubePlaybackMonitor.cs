using Android.App;
using Android.Content;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Android.Provider;
using Android.Service.Notification;

namespace Drumless.Mobile;

/// <summary>
/// Tracks playback delegated to the official YouTube Android app. Access to active
/// media sessions is available after the user enables Drumless Play as a notification
/// listener. That lets the playlist continue even while Drumless is in the background.
/// </summary>
internal static class ExternalYouTubePlaybackMonitor
{
    private static readonly object Sync = new();
    private static int _generation;
    private static bool _tracking;

    public static event EventHandler? PlaybackFinished;

    public static bool HasNotificationAccess
    {
        get
        {
            var context = Android.App.Application.Context;
            var component = ListenerComponent(context);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O_Mr1)
            {
                var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
                return manager?.IsNotificationListenerAccessGranted(component) == true;
            }

            var enabled = Settings.Secure.GetString(
                context.ContentResolver,
                "enabled_notification_listeners");
            return !string.IsNullOrWhiteSpace(enabled) &&
                   enabled.Contains(context.PackageName, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void OpenNotificationAccessSettings()
    {
        var context = Android.App.Application.Context;
        var intent = new Intent(Settings.ActionNotificationListenerSettings)
            .AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    public static void BeginTracking()
    {
        lock (Sync)
        {
            _generation++;
            _tracking = true;
        }

        // Keep Drumless alive while the official YouTube app owns the audio session.
        PlaybackKeepAliveService.Start();
        YouTubeMediaSessionListener.Instance?.AttachToYouTubeSession();
    }

    public static void StopTracking()
    {
        lock (Sync)
        {
            _tracking = false;
            _generation++;
        }

        YouTubeMediaSessionListener.Instance?.PauseAndDetachController();
        PlaybackKeepAliveService.Stop();
    }

    internal static (bool Tracking, int Generation) Snapshot()
    {
        lock (Sync)
        {
            return (_tracking, _generation);
        }
    }

    internal static void NotifyFinished(int generation)
    {
        lock (Sync)
        {
            if (!_tracking || generation != _generation)
            {
                return;
            }

            _tracking = false;
        }

        PlaybackKeepAliveService.Stop();
        MainThread.BeginInvokeOnMainThread(() =>
            PlaybackFinished?.Invoke(null, EventArgs.Empty));
    }

    internal static ComponentName ListenerComponent(Context context) =>
        new(context, Java.Lang.Class.FromType(typeof(YouTubeMediaSessionListener)));
}

[Service(
    Label = "Drumless Play media monitor",
    Permission = Android.Manifest.Permission.BindNotificationListenerService,
    Exported = true)]
[IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
public sealed class YouTubeMediaSessionListener : NotificationListenerService
{
    private const string YouTubePackage = "com.google.android.youtube";
    private MediaController? _controller;
    private YouTubeControllerCallback? _callback;

    internal static YouTubeMediaSessionListener? Instance { get; private set; }

    public override void OnListenerConnected()
    {
        base.OnListenerConnected();
        Instance = this;
        AttachToYouTubeSession();
    }

    public override void OnListenerDisconnected()
    {
        DetachController();
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
        base.OnListenerDisconnected();
    }

    public override void OnNotificationPosted(StatusBarNotification? sbn)
    {
        base.OnNotificationPosted(sbn);
        if (sbn?.PackageName == YouTubePackage &&
            ExternalYouTubePlaybackMonitor.Snapshot().Tracking)
        {
            AttachToYouTubeSession();
        }
    }

    internal void AttachToYouTubeSession()
    {
        var tracking = ExternalYouTubePlaybackMonitor.Snapshot();
        if (!tracking.Tracking)
        {
            return;
        }

        try
        {
            var manager = (MediaSessionManager?)GetSystemService(MediaSessionService);
            var component = ExternalYouTubePlaybackMonitor.ListenerComponent(this);
            var controller = manager?
                .GetActiveSessions(component)
                .FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.PackageName,
                        YouTubePackage,
                        StringComparison.OrdinalIgnoreCase));

            if (controller is null)
            {
                // YouTube may need a moment to create its MediaSession after ACTION_VIEW.
                new Handler(Looper.MainLooper!).PostDelayed(
                    () => AttachToYouTubeSession(),
                    600);
                return;
            }

            if (_controller is not null &&
                _controller.Equals(controller) &&
                _callback is not null)
            {
                return;
            }

            DetachController();
            _controller = controller;
            _callback = new YouTubeControllerCallback(controller, tracking.Generation);
            controller.RegisterCallback(_callback, new Handler(Looper.MainLooper!));
            _callback.Prime(controller.Metadata, controller.PlaybackState);
        }
        catch (Exception)
        {
            // If Android temporarily refuses access while the listener is reconnecting,
            // the next YouTube notification will retry attaching the controller.
        }
    }

    internal void PauseAndDetachController()
    {
        if (_controller is not null)
        {
            try
            {
                _controller.GetTransportControls().Pause();
            }
            catch (Exception)
            {
                // The remote YouTube session may already have disappeared.
            }
        }

        DetachController();
    }

    internal void DetachController()
    {
        if (_controller is not null && _callback is not null)
        {
            try
            {
                _controller.UnregisterCallback(_callback);
            }
            catch (Exception)
            {
                // The remote YouTube session may already have disappeared.
            }
        }

        _callback?.Dispose();
        _callback = null;
        _controller = null;
    }

    private sealed class YouTubeControllerCallback : MediaController.Callback
    {
        private readonly MediaController _controller;
        private readonly int _generation;
        private string? _initialMetadataKey;
        private long _durationMilliseconds;
        private bool _hasPlayed;
        private bool _finished;

        public YouTubeControllerCallback(MediaController controller, int generation)
        {
            _controller = controller;
            _generation = generation;
        }

        public void Prime(MediaMetadata? metadata, PlaybackState? state)
        {
            ReadMetadata(metadata, allowFinish: false);
            ReadPlaybackState(state);
        }

        public override void OnMetadataChanged(MediaMetadata? metadata)
        {
            base.OnMetadataChanged(metadata);
            ReadMetadata(metadata, allowFinish: true);
        }

        public override void OnPlaybackStateChanged(PlaybackState? state)
        {
            base.OnPlaybackStateChanged(state);
            ReadPlaybackState(state);
        }

        private void ReadMetadata(MediaMetadata? metadata, bool allowFinish)
        {
            if (metadata is null || _finished)
            {
                return;
            }

            _durationMilliseconds = metadata.GetLong(MediaMetadata.MetadataKeyDuration);
            var mediaId = metadata.GetString(MediaMetadata.MetadataKeyMediaId);
            var title = metadata.GetString(MediaMetadata.MetadataKeyDisplayTitle) ??
                        metadata.GetString(MediaMetadata.MetadataKeyTitle);
            var key = !string.IsNullOrWhiteSpace(mediaId)
                ? $"id:{mediaId}"
                : !string.IsNullOrWhiteSpace(title)
                    ? $"title:{title}"
                    : null;

            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (_initialMetadataKey is null)
            {
                _initialMetadataKey = key;
                return;
            }

            // If YouTube autoplay moves to another video, stop that unintended video and
            // let Drumless choose the next item from its own playlist instead.
            if (allowFinish && _hasPlayed &&
                !string.Equals(_initialMetadataKey, key, StringComparison.Ordinal))
            {
                try
                {
                    _controller.GetTransportControls().Pause();
                }
                catch (Exception)
                {
                    // The session may already be changing; advancing Drumless is enough.
                }
                Finish();
            }
        }

        private void ReadPlaybackState(PlaybackState? state)
        {
            if (state is null || _finished)
            {
                return;
            }

            switch (state.State)
            {
                case PlaybackStateCode.Playing:
                    _hasPlayed = true;
                    break;

                case PlaybackStateCode.Stopped:
                case PlaybackStateCode.None:
                    if (_hasPlayed)
                    {
                        Finish();
                    }
                    break;

                case PlaybackStateCode.Paused:
                    if (_hasPlayed && _durationMilliseconds > 0 &&
                        state.Position >= _durationMilliseconds - 2_000)
                    {
                        Finish();
                    }
                    break;
            }
        }

        private void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            ExternalYouTubePlaybackMonitor.NotifyFinished(_generation);
        }
    }
}
