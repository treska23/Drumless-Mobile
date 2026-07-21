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
        RewireYouTubeMessageHandler();

        // The old iframe player must never become visible or own playback again. Hide its whole
        // 200px XAML container, not just the HybridWebView itself, so the track list keeps its space.
        _pendingYouTubeVideoId = null;
        YouTubePlayer.IsVisible = false;
        YouTubePlayer.InputTransparent = true;
        YouTubePlayer.Opacity = 0;
        if (YouTubePlayer.Parent is VisualElement legacyPlayerSurface)
        {
            legacyPlayerSurface.RemoveBinding(VisualElement.IsVisibleProperty);
            legacyPlayerSurface.IsVisible = false;
        }

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
                    // call TryStartPendingYouTube here: embedded video playback is disabled.
                    _youtubeReady = true;
                    TryStartPendingPlaylistImport();
                    return;

                case "playlist":
                case "playlistError":
                    // These are the only messages still owned by the HybridWebView helper.
                    OnYouTubeMessageReceived(sender, e);
                    return;

                default:
                    // Ignore state/position/error/autoplayBlocked from the obsolete iframe player.
                    return;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed helper messages. They are no longer part of playback control.
        }
    }
}
