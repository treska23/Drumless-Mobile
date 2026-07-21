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
        // Playback must not be tied to the visual window lifecycle. Local tracks are
        // kept alive by the Android foreground playback service and external YouTube
        // playback is handled by the official YouTube app when needed.
        return new Window(_mainPage);
    }
}
