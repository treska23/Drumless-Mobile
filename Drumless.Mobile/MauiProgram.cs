using Drumless.Mobile.Services;
using Drumless.Mobile.ViewModels;
using Drumless.Mobile.Core.Services;
using Microsoft.Extensions.Logging;

namespace Drumless.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
#if ANDROID
        // Both web surfaces use Android's native WebView. Relax the gesture requirement so a
        // playlist transition initiated by Drumless can start the next YouTube item automatically.
        Microsoft.Maui.Handlers.HybridWebViewHandler.Mapper.AppendToMapping(
            "AllowMediaAutoplay",
            (handler, _) =>
            {
                if (handler.PlatformView is Android.Webkit.WebView webView)
                {
                    webView.Settings.MediaPlaybackRequiresUserGesture = false;
                }
            });

        Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping(
            "AllowMediaAutoplay",
            (handler, _) =>
            {
                if (handler.PlatformView is Android.Webkit.WebView webView)
                {
                    webView.Settings.MediaPlaybackRequiresUserGesture = false;
                    webView.Settings.DomStorageEnabled = true;
                    Android.Webkit.CookieManager.Instance.SetAcceptCookie(true);
                    Android.Webkit.CookieManager.Instance.SetAcceptThirdPartyCookies(webView, true);
                }
            });
#endif

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Services.AddHybridWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<IAudioPlaybackService, PlatformAudioPlaybackService>();
        builder.Services.AddSingleton<IAudioTempoAnalysisService, PlatformTempoAnalysisService>();
        builder.Services.AddSingleton<IMidiInputService, PlatformMidiInputService>();
        builder.Services.AddSingleton(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        });
        builder.Services.AddSingleton<IYouTubeMetadataService, YouTubeMetadataService>();
        builder.Services.AddSingleton<LocalAudioImportService>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
