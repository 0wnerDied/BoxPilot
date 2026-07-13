using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;

namespace BoxPilot.App.Services;

internal sealed class ApplicationShutdownCoordinator
{
    private readonly Func<ValueTask> cleanupAsync;
    private readonly Action shutdown;
    private int started;

    public ApplicationShutdownCoordinator(
        Func<ValueTask> cleanupAsync,
        Action shutdown)
    {
        ArgumentNullException.ThrowIfNull(cleanupAsync);
        ArgumentNullException.ThrowIfNull(shutdown);

        this.cleanupAsync = cleanupAsync;
        this.shutdown = shutdown;
    }

    public void Request(ShutdownRequestedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);

        eventArgs.Cancel = true;
        if (Interlocked.Exchange(ref started, 1) != 0)
            return;

        _ = CompleteAsync();
    }

    private async Task CompleteAsync()
    {
        // Return from ShutdownRequested before a completed cleanup can force nested shutdown.
        await Task.Yield();
        try
        {
            await cleanupAsync();
        }
        catch (Exception exception)
        {
            Trace.TraceError("Application shutdown cleanup failed: {0}", exception);
        }
        finally
        {
            shutdown();
        }
    }
}
