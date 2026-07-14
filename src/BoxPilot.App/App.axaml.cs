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
    private IActivatableLifetime? activatableLifetime;
    private ApplicationActivationHandler? activationHandler;

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
            var trayViewModel = new TrayViewModel(runtime.Session, window);
            DataContext = trayViewModel;
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime lifetime)
            {
                activatableLifetime = lifetime;
                activationHandler = new ApplicationActivationHandler(
                    () => trayViewModel.ShowWindowCommand.Execute(null));
                lifetime.Activated += activationHandler.Handle;
            }
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
                DisposeRuntimeAsync,
                () => desktop.Shutdown());
            desktop.ShutdownRequested += (_, eventArgs) =>
            {
                window.AllowClose = true;
                shutdown.Request(eventArgs);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async ValueTask DisposeRuntimeAsync()
    {
        if (activatableLifetime is not null && activationHandler is not null)
            activatableLifetime.Activated -= activationHandler.Handle;
        activatableLifetime = null;
        activationHandler = null;

        if (runtime is null)
            return;

        await runtime.DisposeAsync();
        runtime = null;
    }
}
