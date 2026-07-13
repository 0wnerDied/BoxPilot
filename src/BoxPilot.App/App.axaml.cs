using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BoxPilot.App.Services;
using BoxPilot.App.ViewModels;
using BoxPilot.App.Views;

namespace BoxPilot.App;

public partial class App : Application
{
    private AppRuntime? runtime;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            runtime = new AppRuntime();
            runtime.Localization.Apply("zh-CN");

            var viewModel = new MainViewModel(runtime.Session, runtime.Localization);
            var window = new MainWindow { DataContext = viewModel };
            DataContext = new TrayViewModel(runtime.Session, window);
            var initialized = false;
            window.Opened += async (_, _) =>
            {
                if (initialized)
                    return;
                initialized = true;
                await runtime.Session.InitializeAsync();
            };

            desktop.MainWindow = window;
            var shutdown = new ApplicationShutdownCoordinator(
                runtime.DisposeAsync,
                () => desktop.Shutdown());
            desktop.ShutdownRequested += (_, eventArgs) =>
            {
                window.AllowClose = true;
                shutdown.Request(eventArgs);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
