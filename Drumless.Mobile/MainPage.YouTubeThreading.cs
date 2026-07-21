namespace Drumless.Mobile;

public partial class MainPage
{
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
    }

    private void OnYouTubeMessageReceivedOnMainThread(
        object? sender,
        HybridWebViewRawMessageReceivedEventArgs e)
    {
        if (MainThread.IsMainThread)
        {
            OnYouTubeMessageReceived(sender, e);
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
            OnYouTubeMessageReceived(sender, e));
    }
}
