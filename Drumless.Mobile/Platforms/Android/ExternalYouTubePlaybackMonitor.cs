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

        PlaybackKeepAliveService.Start();

        // YouTube may still be creating/updating its MediaSession just after ACTION_VIEW.
        // Attach immediately and keep retrying/polling from the listener until it appears.
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

        PlaybackKeepAliveService.Stop();
        MainThread.BeginInvokeOnMainThread(() =>
            PlaybackFinished?.Invoke(null, EventArgs.Empty));
    }

    internal static ComponentName ListenerComponent(Context context) =>
        new(context, Java.Lang.Class.FromType(typeof(YouTubeMediaSessionListener)));

    private static void PauseAnyYouTubeSession()
    {
        if (!HasNotificationAccess)
        {
            return;
        }

        try
        {
            var context = Android.App.Application.Context;
            var manager = (MediaSessionManager?)context.GetSystemService(Context.MediaSessionService);
            var component = ListenerComponent(context);
            var controllers = manager?.GetActiveSessions(component);
            if (controllers is null)
            {
                return;
            }

            foreach (var controller in controllers)
            {
                if (string.Equals(
                        controller.PackageName,
                        YouTubeMediaSessionListener.YouTubePackageName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    controller.GetTransportControls().Pause();
                }
            }
        }
        catch (Exception)
        {
            // The listener service may be reconnecting. The normal controller path will
            // pause the session as soon as Android exposes it again.
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
    private readonly Handler _mainHandler = new(Looper.MainLooper!);
    private MediaController? _controller;
    private YouTubeControllerCallback? _callback;
    private int _pollVersion;

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
                _mainHandler.PostDelayed(() => AttachToYouTubeSession(), 500);
                return;
            }

            if (_controller is not null &&
                _controller.Equals(controller) &&
                _callback is not null)
            {
                StartPolling(tracking.Generation);
                return;
            }

            DetachController();
            _controller = controller;
            _callback = new YouTubeControllerCallback(controller, tracking.Generation);
            controller.RegisterCallback(_callback, _mainHandler);
            _callback.Prime(controller.Metadata, controller.PlaybackState);
            StartPolling(tracking.Generation);
        }
        catch (Exception)
        {
            // Retry while tracking. This also covers the short reconnect window after the
            // user grants notification-listener access.
            _mainHandler.PostDelayed(() => AttachToYouTubeSession(), 700);
        }
    }

    private void StartPolling(int generation)
    {
        var version = ++_pollVersion;
        _mainHandler.PostDelayed(() => PollYouTubeSession(version, generation), 500);
    }

    private void PollYouTubeSession(int version, int generation)
    {
        if (version != _pollVersion)
        {
            return;
        }

        var tracking = ExternalYouTubePlaybackMonitor.Snapshot();
        if (!tracking.Tracking || tracking.Generation != generation)
        {
            return;
        }

        if (_controller is null || _callback is null)
        {
            AttachToYouTubeSession();
            return;
        }

        try
        {
            _callback.Poll(_controller.Metadata, _controller.PlaybackState);
        }
        catch (Exception)
        {
            DetachController();
            AttachToYouTubeSession();
            return;
        }

        _mainHandler.PostDelayed(() => PollYouTubeSession(version, generation), 500);
    }

    internal void PauseAndDetachController()
    {
        ++_pollVersion;
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
        ++_pollVersion;
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
        private const long StartupGraceMilliseconds = 2_500;
        private readonly MediaController _controller;
        private readonly int _generation;
        private readonly long _startedAtMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

            var inStartupGrace =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _startedAtMilliseconds <
                StartupGraceMilliseconds;

            // ACTION_VIEW can briefly expose metadata from the previous YouTube video.
            // During the startup grace window, keep replacing the baseline instead of
            // interpreting that change as YouTube autoplay reaching the end.
            if (_initialMetadataKey is null || inStartupGrace)
            {
                _initialMetadataKey = key;
                return;
            }

            if (allowFinish && _hasPlayed &&
                !string.Equals(_initialMetadataKey, key, StringComparison.Ordinal))
            {
                PauseAndFinish();
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
                        PauseAndFinish();
                    }
                    break;

                case PlaybackStateCode.Paused:
                    if (_hasPlayed && _durationMilliseconds > 0 &&
                        state.Position >= _durationMilliseconds - 2_000)
                    {
                        PauseAndFinish();
                    }
                    break;
            }
        }

        private void PauseAndFinish()
        {
            if (_finished)
            {
                return;
            }

            try
            {
                _controller.GetTransportControls().Pause();
            }
            catch (Exception)
            {
                // The session may already be transitioning; NotifyFinished also performs
                // a second best-effort pause before Drumless advances.
            }

            _finished = true;
            ExternalYouTubePlaybackMonitor.NotifyFinished(_generation);
        }
    }
}
