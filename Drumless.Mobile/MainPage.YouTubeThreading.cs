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

        YouTubePlayer.RawMessageReceived -= OnYouTubeMessageReceived;
        YouTubePlayer.RawMessageReceived -= OnYouTubeMessageReceivedOnMainThread;
        YouTubePlayer.RawMessageReceived += OnYouTubeMessageReceivedOnMainThread;

#if ANDROID
        if (!_externalYouTubeMonitorSubscribed)
        {
            _externalYouTubeMonitorSubscribed = true;
            ExternalYouTubePlaybackMonitor.PlaybackFinished += OnExternalYouTubePlaybackFinished;

            // Always stop any official-YouTube playback BEFORE the normal Drumless playback
            // handler starts the newly selected item. This prevents the UI saying one track
            // while the previous external YouTube video keeps sounding.
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

        // Kill any previous external YouTube playback first. Otherwise ACTION_VIEW can reuse
        // YouTube's existing session and leave the old video audible while Drumless already
        // considers the newly selected item current.
        ExternalYouTubePlaybackMonitor.PauseYouTubePlayback();
        await Task.Delay(150);

        _externalYouTubeActive = true;
        await OpenInYouTubeAsync(item);

        // Let YouTube replace the old MediaSession metadata with the requested video, then
        // establish the tracking baseline. The watchdog will take over from here.
        await Task.Delay(500);
        ExternalYouTubePlaybackMonitor.BeginTracking();
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

        // NotifyFinished has already paused YouTube. Mark the external item inactive BEFORE
        // Next() so the following PlaybackRequested event cannot be mistaken for the old item.
        _externalYouTubeActive = false;
        _viewModel.ReportPlaybackState(false);
        _viewModel.Next(automatic: true);
    }

    private void OnPlaybackRequestedWhileExternalYouTubeActive(object? sender, MediaItem item)
    {
        // This handler is deliberately first. Even if our boolean got out of sync, always make
        // a best-effort PAUSE against the official YouTube MediaSession before Drumless starts
        // any new local or embedded track.
        if (_externalYouTubeActive)
        {
            _externalYouTubeActive = false;
            ExternalYouTubePlaybackMonitor.StopTracking();
        }
        else
        {
            ExternalYouTubePlaybackMonitor.PauseYouTubePlayback();
        }
    }

    private void OnStopRequestedWhileExternalYouTubeActive(object? sender, EventArgs e)
    {
        if (_externalYouTubeActive)
        {
            _externalYouTubeActive = false;
            ExternalYouTubePlaybackMonitor.StopTracking();
        }
        else
        {
            ExternalYouTubePlaybackMonitor.PauseYouTubePlayback();
        }
    }
#endif
}
