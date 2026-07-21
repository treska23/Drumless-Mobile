using System.Text.Json;
using Drumless.Mobile.Core.Models;

namespace Drumless.Mobile;

public partial class MainPage
{
    private bool _externalYouTubeMonitorSubscribed;
    private bool _externalYouTubeActive;
    private MediaItem? _pendingExternalYouTubeItem;

    /// <summary>
    /// HybridWebView raises RawMessageReceived from Android's JavaBridge thread.
    /// Any playback transition triggered by that event can end up calling back into
    /// the WebView, and Android requires every WebView method to run on the UI thread.
    /// Rewire the XAML event handler through this dispatcher once the page handler exists.
    /// </summary>
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        if (YouTubePlayer is null)
        {
            return;
        }

        // The XAML-generated hookup points directly at OnYouTubeMessageReceived.
        // Replace it with a wrapper that always continues on the MAUI main thread.
        YouTubePlayer.RawMessageReceived -= OnYouTubeMessageReceived;
        YouTubePlayer.RawMessageReceived -= OnYouTubeMessageReceivedOnMainThread;
        YouTubePlayer.RawMessageReceived += OnYouTubeMessageReceivedOnMainThread;

#if ANDROID
        if (!_externalYouTubeMonitorSubscribed)
        {
            _externalYouTubeMonitorSubscribed = true;
            ExternalYouTubePlaybackMonitor.PlaybackFinished += OnExternalYouTubePlaybackFinished;

            // These handlers must run BEFORE the normal playback handlers. Otherwise a new
            // Drumless item can start while the previous video is still owning YouTube's
            // MediaSession/audio focus.
            _viewModel.PlaybackRequested -= OnPlaybackRequested;
            _viewModel.PlaybackRequested -= OnPlaybackRequestedWhileExternalYouTubeActive;
            _viewModel.PlaybackRequested += OnPlaybackRequestedWhileExternalYouTubeActive;
            _viewModel.PlaybackRequested += OnPlaybackRequested;

            _viewModel.StopPlaybackRequested -= OnStopPlaybackRequested;
            _viewModel.StopPlaybackRequested -= OnStopRequestedWhileExternalYouTubeActive;
            _viewModel.StopPlaybackRequested += OnStopRequestedWhileExternalYouTubeActive;
            _viewModel.StopPlaybackRequested += OnStopPlaybackRequested;
        }
#endif
    }

    private void OnYouTubeMessageReceivedOnMainThread(
        object? sender,
        HybridWebViewRawMessageReceivedEventArgs e)
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() =>
                OnYouTubeMessageReceivedOnMainThread(sender, e));
            return;
        }

#if ANDROID
        if (TryGetExternalFallbackError(e.Message, out var code))
        {
            _ = HandleExternalYouTubeFallbackAsync(code);
            return;
        }
#endif

        OnYouTubeMessageReceived(sender, e);
    }

#if ANDROID
    private static bool TryGetExternalFallbackError(string? message, out int code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "error", StringComparison.Ordinal) ||
                !root.TryGetProperty("code", out var codeElement) ||
                !codeElement.TryGetInt32(out code))
            {
                return false;
            }

            // 101/150: embedding disabled by the owner. 5/153 also commonly work when
            // delegated to the official YouTube app even though the iframe cannot play them.
            return code is 5 or 101 or 150 or 153;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task HandleExternalYouTubeFallbackAsync(int code)
    {
        var item = _viewModel.CurrentItem?.Model;
        if (item?.Kind != MediaKind.YouTube || string.IsNullOrWhiteSpace(item.YouTubeUrl))
        {
            await HandleYouTubePlaybackErrorAsync(code);
            return;
        }

        _pendingYouTubeVideoId = null;
        _youtubeIsPlaying = false;

        if (!ExternalYouTubePlaybackMonitor.HasNotificationAccess)
        {
            _pendingExternalYouTubeItem = item;
            var configure = await DisplayAlertAsync(
                "Continuar automáticamente con YouTube",
                "Este vídeo no admite reproducción dentro de Drumless. Para abrirlo en la app oficial de YouTube y continuar automáticamente la playlist cuando termine, activa una vez el acceso de Drumless Play a las notificaciones. Se usa para detectar el estado del reproductor de YouTube.",
                "Configurar",
                "Abrir sin seguimiento");

            if (configure)
            {
                ExternalYouTubePlaybackMonitor.OpenNotificationAccessSettings();
                return;
            }

            _pendingExternalYouTubeItem = null;
            await OpenInYouTubeAsync(item);
            return;
        }

        await StartTrackedExternalYouTubeAsync(item);
    }

    private async Task StartTrackedExternalYouTubeAsync(MediaItem item)
    {
        _pendingExternalYouTubeItem = null;
        _externalYouTubeActive = true;

        // Launch the requested video first. Starting the monitor before ACTION_VIEW can attach
        // to stale metadata from the previously playing YouTube video.
        await OpenInYouTubeAsync(item);
        ExternalYouTubePlaybackMonitor.BeginTracking();

        // OpenInYouTubeAsync marks the internal player as paused. From Drumless' point
        // of view the current playlist item is nevertheless playing in the YouTube app.
        _viewModel.ReportPlaybackState(true);
    }

    public async void ResumePendingExternalYouTubeAsync()
    {
        if (_pendingExternalYouTubeItem is not { } item ||
            !ExternalYouTubePlaybackMonitor.HasNotificationAccess)
        {
            return;
        }

        await StartTrackedExternalYouTubeAsync(item);
    }

    private void OnExternalYouTubePlaybackFinished(object? sender, EventArgs e)
    {
        if (!_externalYouTubeActive)
        {
            return;
        }

        // Stop/pause the official YouTube session first so its own autoplay cannot
        // compete with the next item selected by Drumless.
        ExternalYouTubePlaybackMonitor.StopTracking();
        _externalYouTubeActive = false;
        _viewModel.ReportPlaybackState(false);
        _viewModel.Next(automatic: true);
    }

    private void OnPlaybackRequestedWhileExternalYouTubeActive(object? sender, MediaItem item)
    {
        if (!_externalYouTubeActive)
        {
            return;
        }

        // This handler is deliberately first in the PlaybackRequested invocation list.
        // Pause YouTube before Drumless starts the newly selected local/embedded item.
        _externalYouTubeActive = false;
        ExternalYouTubePlaybackMonitor.StopTracking();
    }

    private void OnStopRequestedWhileExternalYouTubeActive(object? sender, EventArgs e)
    {
        if (!_externalYouTubeActive)
        {
            return;
        }

        _externalYouTubeActive = false;
        ExternalYouTubePlaybackMonitor.StopTracking();
    }
#endif
}
