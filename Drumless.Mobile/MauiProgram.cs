using Drumless.Mobile.Services;
using Drumless.Mobile.ViewModels;
using Drumless.Mobile.Core.Services;
using Microsoft.Extensions.Logging;

namespace Drumless.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
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
