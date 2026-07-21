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
/// listener. Drumless remains the owner of playlist sequencing: YouTube is only used
/// as an external player for the current blocked/non-embeddable item.
/// </summary>
internal static class ExternalYouTubePlaybackMonitor
{
    private static readonly object Sync = new();
    private static int _generation;
    private static bool _tracking;
    private static CancellationTokenSource? _watchdogCancellation;

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
        CancellationTokenSource cancellation;
        int generation;

        lock (Sync)
        {
            _watchdogCancellation?.Cancel();
            _watchdogCancellation?.Dispose();

            _generation++;
            generation = _generation;
            _tracking = true;
            cancellation = new CancellationTokenSource();
            _watchdogCancellation = cancellation;
        }

        // Use both mechanisms. The NotificationListenerService receives MediaSession
        // callbacks when Android delivers them, while the watchdog actively polls the
        // current YouTube session. The latter is important because YouTube can move to
        // its own next video without ever entering PAUSED/STOPPED between items.
        YouTubeMediaSessionListener.Instance?.AttachToYouTubeSession();
        _ = RunWatchdogAsync(generation, cancellation.Token);
    }

    public static void StopTracking()
    {
        CancellationTokenSource? cancellation;
        lock (Sync)
        {
            _tracking = false;
            _generation++;
            cancellation = _watchdogCancellation;
            _watchdogCancellation = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        PauseYouTubePlayback();
    }

    /// <summary>
    /// Best-effort hard stop for whichever video the official YouTube app currently owns.
    /// This is deliberately called before Drumless starts any new playlist item so the old
    /// external video cannot keep audio focus or continue its own autoplay sequence.
    /// </summary>
    internal static void PauseYouTubePlayback()
    {
        YouTubeMediaSessionListener.Instance?.PauseAndDetachController();
        PauseAnyYouTubeSession();
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
        CancellationTokenSource? cancellation;
        lock (Sync)
        {
            if (!_tracking || generation != _generation)
            {
                return;
            }

            _tracking = false;
            cancellation = _watchdogCancellation;
            _watchdogCancellation = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();

        // Critical ordering: pause YouTube first, then advance Drumless. A tiny delay gives
        // the remote MediaSession time to apply PAUSE before the next Drumless item asks for
        // audio focus/playback.
        PauseYouTubePlayback();
        _ = RaisePlaybackFinishedAfterPauseAsync();
    }

    internal static ComponentName ListenerComponent(Context context) =>
        new(context, Java.Lang.Class.FromType(typeof(YouTubeMediaSessionListener)));

    private static async Task RaisePlaybackFinishedAfterPauseAsync()
    {
        await Task.Delay(200);
        MainThread.BeginInvokeOnMainThread(() =>
            PlaybackFinished?.Invoke(null, EventArgs.Empty));
    }

    private static async Task RunWatchdogAsync(int generation, CancellationToken cancellationToken)
    {
        string? initialMetadataKey = null;
        bool hasPlayed = false;

        try
        {
            // StartTrackedExternalYouTubeAsync pauses the old session before ACTION_VIEW.
            // Give YouTube a moment to load the requested URL before taking our baseline.
            await Task.Delay(700, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var tracking = Snapshot();
                if (!tracking.Tracking || tracking.Generation != generation)
                {
                    return;
                }

                var controller = FindYouTubeController();
                if (controller is not null)
                {
                    var state = controller.PlaybackState;
                    var metadata = controller.Metadata;
                    var metadataKey = GetMetadataKey(metadata);
                    var durationMilliseconds = metadata?.GetLong(MediaMetadata.MetadataKeyDuration) ?? 0;

                    if (state?.State == PlaybackStateCode.Playing)
                    {
                        hasPlayed = true;
                        initialMetadataKey ??= metadataKey;
                    }

                    // YouTube often keeps the same MediaSession continuously alive and simply
                    // swaps metadata when its own autoplay starts the next video. Treat that
                    // metadata change as the end of OUR current item and stop YouTube immediately.
                    if (hasPlayed &&
                        !string.IsNullOrWhiteSpace(initialMetadataKey) &&
                        !string.IsNullOrWhiteSpace(metadataKey) &&
                        !string.Equals(initialMetadataKey, metadataKey, StringComparison.Ordinal))
                    {
                        TryPause(controller);
                        NotifyFinished(generation);
                        return;
                    }

                    if (state is not null && hasPlayed)
                    {
                        var positionMilliseconds = EstimatePositionMilliseconds(state);

                        // YouTube may never report PAUSED/STOPPED at the boundary because its
                        // own autoplay starts immediately. Stop just before the end instead.
                        if (state.State == PlaybackStateCode.Playing &&
                            durationMilliseconds > 0 &&
                            positionMilliseconds >= durationMilliseconds - 450)
                        {
                            TryPause(controller);
                            NotifyFinished(generation);
                            return;
                        }

                        if (state.State is PlaybackStateCode.Stopped or PlaybackStateCode.None)
                        {
                            NotifyFinished(generation);
                            return;
                        }

                        if (state.State == PlaybackStateCode.Paused &&
                            durationMilliseconds > 0 &&
                            positionMilliseconds >= durationMilliseconds - 2_000)
                        {
                            NotifyFinished(generation);
                            return;
                        }
                    }
                }

                await Task.Delay(250, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal when the user changes item or Drumless advances the playlist.
        }
        catch (Exception)
        {
            // The NotificationListenerService callbacks remain as the secondary path.
        }
    }

    private static MediaController? FindYouTubeController()
    {
        try
        {
            var context = Android.App.Application.Context;
            var manager = (MediaSessionManager?)context.GetSystemService(Context.MediaSessionService);
            return manager?
                .GetActiveSessions(ListenerComponent(context))
                .FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.PackageName,
                        YouTubeMediaSessionListener.YouTubePackageName,
                        StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? GetMetadataKey(MediaMetadata? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        var mediaId = metadata.GetString(MediaMetadata.MetadataKeyMediaId);
        var title = metadata.GetString(MediaMetadata.MetadataKeyDisplayTitle) ??
                    metadata.GetString(MediaMetadata.MetadataKeyTitle);
        return !string.IsNullOrWhiteSpace(mediaId)
            ? $"id:{mediaId}"
            : !string.IsNullOrWhiteSpace(title)
                ? $"title:{title}"
                : null;
    }

    private static long EstimatePositionMilliseconds(PlaybackState state)
    {
        var position = Math.Max(0L, state.Position);
        if (state.State != PlaybackStateCode.Playing || state.LastPositionUpdateTime <= 0)
        {
            return position;
        }

        var elapsed = Math.Max(0L, SystemClock.ElapsedRealtime() - state.LastPositionUpdateTime);
        return position + (long)(elapsed * state.PlaybackSpeed);
    }

    private static void TryPause(MediaController controller)
    {
        try
        {
            controller.GetTransportControls().Pause();
        }
        catch (Exception)
        {
            // Session can change while YouTube advances; the global pause below retries.
        }
    }

    private static void PauseAnyYouTubeSession()
    {
        var controller = FindYouTubeController();
        if (controller is not null)
        {
            TryPause(controller);
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
                _callback.Poll(controller.Metadata, controller.PlaybackState);
                SchedulePoll();
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
        _pollHandler.PostDelayed(_pollRunnable, 250);
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
                    // The static monitor retries the current YouTube session as well.
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

            var position = Math.Max(0L, state.Position);
            if (state.State == PlaybackStateCode.Playing && state.LastPositionUpdateTime > 0)
            {
                var elapsed = Math.Max(0L, SystemClock.ElapsedRealtime() - state.LastPositionUpdateTime);
                position += (long)(elapsed * state.PlaybackSpeed);
            }

            switch (state.State)
            {
                case PlaybackStateCode.Playing:
                    _hasPlayed = true;
                    // Proactively stop before YouTube autoplay rolls into its own next item.
                    if (_durationMilliseconds > 0 &&
                        position >= _durationMilliseconds - 450)
                    {
                        try
                        {
                            _controller.GetTransportControls().Pause();
                        }
                        catch (Exception)
                        {
                            // Finish still hands control back to Drumless.
                        }
                        Finish();
                    }
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
                        position >= _durationMilliseconds - 2_000)
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
