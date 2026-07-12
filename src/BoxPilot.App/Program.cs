using System;
using Avalonia;
using BoxPilot.Core.Infrastructure;

namespace BoxPilot.App;

sealed class Program
{
    // Avalonia services are unavailable until the desktop lifetime initializes the platform.
    [STAThread]
    public static int Main(string[] args)
    {
        if (CoreServiceInstaller.IsInvocation(args))
            return CoreServiceInstaller.RunAsync(args).GetAwaiter().GetResult();
        if (CoreServiceHost.IsInvocation(args))
            return CoreServiceHost.Run(args);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // The visual designer and desktop entry point share this builder.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();
#if DEBUG
        builder
            .WithDeveloperTools()
            .LogToTrace();
#endif
        return builder;
    }
}
