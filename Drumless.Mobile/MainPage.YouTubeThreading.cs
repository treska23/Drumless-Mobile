namespace Drumless.Mobile;

public partial class MainPage
{
    // Kept temporarily only because MainPage.YouTubeBrowser still contains a defensive check from
    // the previous implementation. No code sets this state anymore, so the external-app path is
    // unreachable while the internal browser prototype is active.
    private bool _externalYouTubeActive;
    private string? _externalYouTubeItemId;

    /// <summary>
    /// Keep HybridWebView callbacks on MAUI's main thread. Actual YouTube playback now stays
    /// inside Drumless in the full mobile-site WebView; HybridWebView remains only as a helper.
    /// </summary>
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        Appearing -= OnPageAppearingForYouTubeIntegration;
        Appearing += OnPageAppearingForYouTubeIntegration;
        RewireYouTubeMessageHandler();

        // OnHandlerChanged can run during InitializeComponent before _viewModel is assigned.
        // Dispatch the routing change until construction has completed.
        Dispatcher.Dispatch(() =>
        {
            RewireYouTubeMessageHandler();
            EnableYouTubeBrowserPlaybackIntegration();
        });
    }

    private void OnPageAppearingForYouTubeIntegration(object? sender, EventArgs e)
    {
        RewireYouTubeMessageHandler();
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

        // The old Android external-app fallback is deliberately gone. Errors from this helper
        // stay inside Drumless and are handled by the normal HybridWebView error path.
        OnYouTubeMessageReceived(sender, e);
    }
}
