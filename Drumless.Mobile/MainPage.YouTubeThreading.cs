namespace Drumless.Mobile;

public partial class MainPage
{
    /// <summary>
    /// Keep HybridWebView callbacks on MAUI's main thread. Actual YouTube playback now stays
    /// inside Drumless in the full mobile-site WebView; HybridWebView remains only as a helper.
    /// </summary>
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        Loaded -= OnPageLoadedForYouTubeIntegration;
        Loaded += OnPageLoadedForYouTubeIntegration;
        RewireYouTubeMessageHandler();

        // Never touch _viewModel or playback routing here. OnHandlerChanged can run during
        // InitializeComponent, before MainPage's constructor has finished assigning services.
    }

    private void OnPageLoadedForYouTubeIntegration(object? sender, EventArgs e)
    {
        // Loaded runs after the MainPage constructor has completed, including the original
        // playback-event subscriptions. Replace them here once, deterministically, so the old
        // embedded/external playback route cannot remain subscribed alongside the browser route.
        Loaded -= OnPageLoadedForYouTubeIntegration;
        RewireYouTubeMessageHandler();

        try
        {
            EnableYouTubeBrowserPlaybackIntegration();
        }
        catch (Exception exception)
        {
            _viewModel.ReportPlaybackFailure($"No se pudo preparar el reproductor de YouTube: {exception.Message}");
        }
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

        // The old Android external-app fallback is deliberately gone. Errors from this helper
        // stay inside Drumless and are handled by the normal HybridWebView error path.
        OnYouTubeMessageReceived(sender, e);
    }
}
