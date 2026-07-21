using System.Text.Json;

namespace Drumless.Mobile;

public partial class MainPage
{
    /// <summary>
    /// The HybridWebView is now only a hidden helper for playlist inspection. Actual YouTube
    /// playback is owned exclusively by MainPage.YouTubeBrowser.cs.
    /// </summary>
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        Loaded -= OnPageReadyForYouTubeBrowser;
        Loaded += OnPageReadyForYouTubeBrowser;
        Appearing -= OnPageReadyForYouTubeBrowser;
        Appearing += OnPageReadyForYouTubeBrowser;

        RewireYouTubeMessageHandler();
    }

    private void OnPageReadyForYouTubeBrowser(object? sender, EventArgs e)
    {
        // Both Loaded and Appearing happen after construction. Enabling twice is harmless because
        // EnableYouTubeBrowserPlaybackIntegration removes every relevant handler before re-adding
        // the browser route.
        RewireYouTubeMessageHandler();

        // The old iframe player must never be visible or become the active playback surface again.
        // Keep the control alive only because the playlist importer still uses its JS bridge.
        _pendingYouTubeVideoId = null;
        YouTubePlayer.IsVisible = false;
        YouTubePlayer.InputTransparent = true;
        YouTubePlayer.Opacity = 0;
        SendYouTubeCommand(new { type = "pause" });

        EnableYouTubeBrowserPlaybackIntegration();
    }

    private void RewireYouTubeMessageHandler()
    {
        if (YouTubePlayer is null)
        {
            return;
        }

        YouTubePlayer.RawMessageReceived -= OnYouTubeMessageReceived;
        YouTubePlayer.RawMessageReceived -= OnYouTubeMessageReceivedOnMainThread;
        YouTubePlayer.RawMessageReceived += OnYouTubeMessageReceivedOnMainThread;
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

        if (string.IsNullOrWhiteSpace(e.Message))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(e.Message);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;

            switch (type)
            {
                case "ready":
                    // Ready is needed only so the helper can inspect imported playlists. Never
                    // call TryStartPendingYouTube here: embedded video playback is intentionally
                    // disabled now.
                    _youtubeReady = true;
                    TryStartPendingPlaylistImport();
                    return;

                case "playlist":
                case "playlistError":
                    // These are the only messages still owned by the HybridWebView helper.
                    OnYouTubeMessageReceived(sender, e);
                    return;

                default:
                    // Ignore state/position/error/autoplayBlocked from the old embedded player.
                    // In particular, an iframe error must never show the old "Abrir en YouTube /
                    // Saltar pista" dialog again.
                    return;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed helper messages. They are no longer part of playback control.
        }
    }
}
