using Avalonia.Controls.ApplicationLifetimes;

namespace BoxPilot.App.Services;

internal sealed class ApplicationActivationHandler
{
    private readonly Action showMainWindow;

    public ApplicationActivationHandler(Action showMainWindow)
    {
        ArgumentNullException.ThrowIfNull(showMainWindow);
        this.showMainWindow = showMainWindow;
    }

    public void Handle(object? sender, ActivatedEventArgs eventArgs)
    {
        if (eventArgs.Kind == ActivationKind.Reopen)
            showMainWindow();
    }
}
