using Microsoft.UI.Xaml;

namespace AppHost2;

public partial class App : Application
{
    private Window? _window;
    public static IntPtr MainWindowHandle { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _window.Activate();
    }
}
