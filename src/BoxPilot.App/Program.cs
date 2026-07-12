using System;
using Avalonia;

namespace BoxPilot.App;

sealed class Program
{
    // Avalonia services are unavailable until the desktop lifetime initializes the platform.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // The visual designer and desktop entry point share this builder.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
