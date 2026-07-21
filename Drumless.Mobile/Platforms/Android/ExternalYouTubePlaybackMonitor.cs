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
            if (Build.VERSION.SdkInt >= BuildVersionCodes.OMr1)
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

        // Do NOT start our own foreground keep-alive service here. YouTube already owns
        // the actual media playback and its NotificationListenerService is system-bound.
        // Starting an extra foreground service while handing the app to YouTube can be
        // rejected by recent Android versions and bring the whole Drumless process down.
        YouTubeMediaSessionListener.Instance?.AttachToYouTubeSession();
    }

    public static void StopTracking()
    {
        lock (Sync)
        {
            _tracking = false;
            _generation++;
        }

        if (YouTubeMediaSessionListener.Instance is { } listener)
        {
            listener.PauseAndDetachController();
        }
        else
        {
            PauseAnyYouTubeSession();
        }
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

        // Critical ordering: stop YouTube BEFORE telling Drumless to advance. Otherwise
        // YouTube autoplay keeps its own next video playing and can retain audio focus while
        // Drumless has already selected the following local/embedded track.
        if (YouTubeMediaSessionListener.Instance is { } listener)
        {
            listener.PauseAndDetachController();
        }
        else
        {
            PauseAnyYouTubeSession();
        }

        MainThread.BeginInvokeOnMainThread(() =>
            PlaybackFinished?.Invoke(null, EventArgs.Empty));
    }

    internal static ComponentName ListenerComponent(Context context) =>
        new(context, Java.Lang.Class.FromType(typeof(YouTubeMediaSessionListener)));

    private static void PauseAnyYouTubeSession()
    {
        try
        {
            var context = Android.App.Application.Context;
            var manager = (MediaSessionManager?)context.GetSystemService(Context.MediaSessionService);
            var controller = manager?
                .GetActiveSessions(ListenerComponent(context))
                .FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.PackageName,
                        YouTubeMediaSessionListener.YouTubePackageName,
                        StringComparison.OrdinalIgnoreCase));
            controller?.GetTransportControls().Pause();
        }
        catch (Exception)
        {
            // Best effort only. A session can disappear while Android is switching apps.
        }
    }
}

[Service(
    Label = "Drumless Play media monitor",
    Permission = Android.Manifest.Permission.BindNotificationListenerService,
    Exported = true)]
[IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
public sealed class YouTubeMediaSessionListener : NotificationListenerService
{
    internal const string YouTubePackageName = "com.google.android.youtube";
    private MediaController? _controller;
    private YouTubeControllerCallback? _callback;
    private Handler? _pollHandler;
    private Java.Lang.Runnable? _pollRunnable;

    internal static YouTubeMediaSessionListener? Instance { get; private set; }

    public override void OnListenerConnected()
    {
        base.OnListenerConnected();
        Instance = this;
        AttachToYouTubeSession();
    }

    public override void OnListenerDisconnected()
    {
        StopPolling();
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
        if (sbn?.PackageName == YouTubePackageName &&
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
            StopPolling();
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
                        YouTubePackageName,
                        StringComparison.OrdinalIgnoreCase));

            if (controller is null)
            {
                SchedulePoll();
                return;
            }

            if (_controller is not null &&
                _controller.Equals(controller) &&
                _callback is not null)
            {
                SchedulePoll();
                _callback.Poll(controller.Metadata, controller.PlaybackState);
                return;
            }

            DetachController();
            _controller = controller;
            _callback = new YouTubeControllerCallback(controller, tracking.Generation);
            controller.RegisterCallback(_callback, new Handler(Looper.MainLooper!));
            _callback.Prime(controller.Metadata, controller.PlaybackState);
            SchedulePoll();
        }
        catch (Exception)
        {
            SchedulePoll();
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

        StopPolling();
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

    private void SchedulePoll()
    {
        if (!ExternalYouTubePlaybackMonitor.Snapshot().Tracking)
        {
            StopPolling();
            return;
        }

        _pollHandler ??= new Handler(Looper.MainLooper!);
        _pollRunnable ??= new Java.Lang.Runnable(() =>
        {
            if (ExternalYouTubePlaybackMonitor.Snapshot().Tracking)
            {
                AttachToYouTubeSession();
            }
        });
        _pollHandler.RemoveCallbacks(_pollRunnable);
        _pollHandler.PostDelayed(_pollRunnable, 1_000);
    }

    private void StopPolling()
    {
        if (_pollHandler is not null && _pollRunnable is not null)
        {
            _pollHandler.RemoveCallbacks(_pollRunnable);
        }
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

        public void Poll(MediaMetadata? metadata, PlaybackState? state)
        {
            ReadMetadata(metadata, allowFinish: true);
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
