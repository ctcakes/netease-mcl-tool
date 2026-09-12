using Microsoft.UI.Xaml;

namespace MclLauncher;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        Application.Start(_ =>
        {
            var app = new App();
        });
    }
}
