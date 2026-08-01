using Avalonia;

namespace GameCheater.App;

internal static class Program
{
    // Avalonia entry point. Runs the same on macOS (dev) and Windows (real target).
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
