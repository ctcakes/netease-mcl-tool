using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace MclLauncher;

public partial class App : Application
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    public App()
    {
        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4.
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        window.Activate();
    }
}
