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
        var window = new Window(_mainPage);
        window.Deactivated += (_, _) => _mainPage.PauseForBackground();
        return window;
    }
}
