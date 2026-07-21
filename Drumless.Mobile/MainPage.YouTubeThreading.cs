using System.Text.Json;
using Drumless.Mobile.Core.Models;
#if ANDROID
using Android.Content;
#endif

namespace Drumless.Mobile;

public partial class MainPage
{
    private bool _externalYouTubeMonitorSubscribed;
    private bool _externalYouTubeActive;
    private string? _externalYouTubeItemId;
    private MediaItem? _pendingExternalYouTubeItem;

    /// <summary>
    /// OnHandlerChanged can run from InitializeComponent before MainPage's constructor has
    /// assigned _viewModel. Keep it limited to UI event wiring; anything that touches the
    /// view model is initialized from Appearing, after construction is complete.
    /// </summary>
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        Appearing -= OnPageAppearingForYouTubeIntegration;
        Appearing += OnPageAppearingForYouTubeIntegration;
        RewireYouTubeMessageHandler();
    }

    private void OnPageAppearingForYouTubeIntegration(object? sender, EventArgs e)
    {
        RewireYouTubeMessageHandler();

#if ANDROID
        EnsureExternalYouTubeIntegrationInitialized();
#endif
    }

    private void RewireYouTubeMessageHandler()
    {
        if (YouTubePlayer is null)
        {
            return;
        }

        // The XAML-generated hookup points directly at OnYouTubeMessageReceived. Replace it
        // with a wrapper that always resumes on MAUI's main thread before touching WebView.
        YouTubePlayer.RawMessageReceived -= OnYouTubeMessageReceived;
        YouTubePlayer.RawMessageReceived -= OnYouTubeMessageReceivedOnMainThread;
        YouTubePlayer.RawMessageReceived += OnYouTubeMessageReceivedOnMainThread;
    }

#if ANDROID
    private void EnsureExternalYouTubeIntegrationInitialized()
    {
        if (_externalYouTubeMonitorSubscribed)
        {
            return;
        }

        _externalYouTubeMonitorSubscribed = true;
        ExternalYouTubePlaybackMonitor.PlaybackFinished += OnExternalYouTubePlaybackFinished;

        // Keep this guard first in the event chain. It only shuts down an actually active
        // external YouTube session before Drumless starts the newly requested item.
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
        if (TryGetExternalFallbackError(e.Message, out var code, out var videoId))
        {
            // Ignore a late error emitted by the iframe for a video that is no longer current.
            // Without this guard an old failed video could reopen YouTube after the user had
            // already moved to another track.
            var currentVideoId = _viewModel.CurrentItem?.Model.YouTubeVideoId;
            if (!string.IsNullOrWhiteSpace(videoId) &&
                !string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
            {
                return;
            }

            _ = HandleExternalYouTubeFallbackAsync(code);
            return;
        }
#endif

        OnYouTubeMessageReceived(sender, e);
    }

#if ANDROID
    private static bool TryGetExternalFallbackError(
        string? message,
        out int code,
        out string? videoId)
    {
        code = 0;
        videoId = null;
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

            if (root.TryGetProperty("videoId", out var videoElement) &&
                videoElement.ValueKind == JsonValueKind.String)
            {
                videoId = videoElement.GetString();
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
        SendYouTubeCommand(new { type = "pause" });

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
            await OpenOfficialYouTubeAsync(item);
            return;
        }

        await StartTrackedExternalYouTubeAsync(item);
    }

    private async Task StartTrackedExternalYouTubeAsync(MediaItem item)
    {
        _pendingExternalYouTubeItem = null;

        // Stop the previous official-YouTube session before opening the exact requested video.
        // This prevents YouTube from carrying on with its own playlist while Drumless already
        // considers a different item current.
        ExternalYouTubePlaybackMonitor.PauseYouTubePlayback();
        await Task.Delay(250);

        _externalYouTubeActive = true;
        _externalYouTubeItemId = item.Id;

        if (!await OpenOfficialYouTubeAsync(item))
        {
            _externalYouTubeActive = false;
            _externalYouTubeItemId = null;
            _viewModel.ReportPlaybackFailure("No se pudo abrir la pista en la aplicación de YouTube");
            return;
        }

        // Give the official app enough time to replace any previous MediaSession metadata with
        // the exact requested video before taking the tracking baseline. Starting the monitor
        // too early made the old video look like the current Drumless item and caused bad jumps.
        await Task.Delay(1200);

        // The user may have changed track while YouTube was opening.
        if (!_externalYouTubeActive ||
            !string.Equals(_externalYouTubeItemId, item.Id, StringComparison.Ordinal) ||
            !string.Equals(_viewModel.CurrentItem?.Id, item.Id, StringComparison.Ordinal))
        {
            ExternalYouTubePlaybackMonitor.PauseYouTubePlayback();
            return;
        }

        ExternalYouTubePlaybackMonitor.BeginTracking();
        _viewModel.ReportPlaybackState(true);
    }

    private async Task<bool> OpenOfficialYouTubeAsync(MediaItem item)
    {
        if (string.IsNullOrWhiteSpace(item.YouTubeVideoId))
        {
            return false;
        }

        SendYouTubeCommand(new { type = "pause" });
        _pendingYouTubeVideoId = null;
        _youtubeIsPlaying = false;
        _viewModel.ReportPlaybackState(false);

        var exactVideoUrl = $"https://www.youtube.com/watch?v={Uri.EscapeDataString(item.YouTubeVideoId)}";
        try
        {
            var context = Android.App.Application.Context;
            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(exactVideoUrl));
            intent.SetPackage(YouTubeMediaSessionListener.YouTubePackageName);
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return await Launcher.Default.OpenAsync(exactVideoUrl);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async void ResumePendingExternalYouTubeAsync()
    {
        try
        {
            if (_pendingExternalYouTubeItem is not { } item ||
                !ExternalYouTubePlaybackMonitor.HasNotificationAccess)
            {
                return;
            }

            await StartTrackedExternalYouTubeAsync(item);
        }
        catch (Exception exception)
        {
            _pendingExternalYouTubeItem = null;
            _externalYouTubeActive = false;
            _externalYouTubeItemId = null;
            _viewModel.ReportPlaybackFailure($"No se pudo reanudar YouTube: {exception.Message}");
        }
    }

    private void OnExternalYouTubePlaybackFinished(object? sender, EventArgs e) =>
        _ = CompleteExternalYouTubePlaybackAsync();

    private async Task CompleteExternalYouTubePlaybackAsync()
    {
        try
        {
            if (!_externalYouTubeActive)
            {
                return;
            }

            var finishedItemId = _externalYouTubeItemId;
            _externalYouTubeActive = false;
            _externalYouTubeItemId = null;

            // A late MediaSession callback must never advance a newer manually selected track.
            if (string.IsNullOrWhiteSpace(finishedItemId) ||
                !string.Equals(_viewModel.CurrentItem?.Id, finishedItemId, StringComparison.Ordinal))
            {
                ExternalYouTubePlaybackMonitor.PauseYouTubePlayback();
                return;
            }

            // Pause is already sent by the monitor. Reorder the exact live Drumless task without
            // creating or relaunching MainActivity, then continue according to the selected mode.
            var broughtToFront = DrumlessTaskForeground.BringToFront();
            if (broughtToFront)
            {
                await Task.Delay(150);
            }

            _viewModel.ReportPlaybackState(false);
            _viewModel.Next(automatic: true);
        }
        catch (Exception exception)
        {
            // Never let a background media-session completion tear down the MAUI process.
            _externalYouTubeActive = false;
            _externalYouTubeItemId = null;
            _viewModel.ReportPlaybackFailure($"No se pudo continuar la playlist: {exception.Message}");
        }
    }

    private void OnPlaybackRequestedWhileExternalYouTubeActive(object? sender, MediaItem item)
    {
        // Do not touch MediaSession at all for ordinary local/embedded track changes. The previous
        // version issued a global YouTube pause on every single PlaybackRequested event, which
        // introduced an unnecessary native Android code path exactly when switching songs.
        if (!_externalYouTubeActive)
        {
            return;
        }

        _externalYouTubeActive = false;
        _externalYouTubeItemId = null;
        ExternalYouTubePlaybackMonitor.StopTracking();
    }

    private void OnStopRequestedWhileExternalYouTubeActive(object? sender, EventArgs e)
    {
        _externalYouTubeItemId = null;
        if (!_externalYouTubeActive)
        {
            return;
        }

        _externalYouTubeActive = false;
        ExternalYouTubePlaybackMonitor.StopTracking();
    }
#endif
}
