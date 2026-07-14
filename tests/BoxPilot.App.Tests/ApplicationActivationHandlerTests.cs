using Avalonia.Controls.ApplicationLifetimes;
using BoxPilot.App.Services;

namespace BoxPilot.App.Tests;

public sealed class ApplicationActivationHandlerTests
{
    [Fact]
    public void ReopenShowsMainWindow()
    {
        var showCalls = 0;
        var handler = new ApplicationActivationHandler(() => showCalls++);

        handler.Handle(this, new ActivatedEventArgs(ActivationKind.Background));
        Assert.Equal(0, showCalls);

        handler.Handle(this, new ActivatedEventArgs(ActivationKind.Reopen));
        Assert.Equal(1, showCalls);
    }
}
