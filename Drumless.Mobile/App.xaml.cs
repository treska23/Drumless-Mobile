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
        // YouTube playback now remains inside Drumless' own WebView, so there is no external
        // YouTube-app handoff to resume when the window returns to the foreground.
        return new Window(_mainPage);
    }
}
