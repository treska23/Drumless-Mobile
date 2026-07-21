namespace Drumless.Mobile;

public partial class App : Application
{
    private readonly MainPage _mainPage;

    public App(MainPage mainPage)
    {
        InitializeComponent();
        _mainPage = mainPage;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Do not pause playback merely because the window loses focus. External fallback
        // videos are handled by the official YouTube app, while local playback currently
        // remains owned by Drumless' in-process audio player.
        var window = new Window(_mainPage);
#if ANDROID
        window.Resumed += (_, _) => _mainPage.ResumePendingExternalYouTubeAsync();
#endif
        return window;
    }
}
