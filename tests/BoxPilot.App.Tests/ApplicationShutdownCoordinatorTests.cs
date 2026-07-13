using Avalonia.Controls.ApplicationLifetimes;
using BoxPilot.App.Services;

namespace BoxPilot.App.Tests;

public sealed class ApplicationShutdownCoordinatorTests
{
    [Fact]
    public async Task RequestDefersShutdownUntilCleanupCompletes()
    {
        var cleanupStarted = CreateCompletion();
        var releaseCleanup = CreateCompletion();
        var shutdownRequested = CreateCompletion();
        var cleanupCalls = 0;
        var coordinator = new ApplicationShutdownCoordinator(
            async () =>
            {
                Interlocked.Increment(ref cleanupCalls);
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task;
            },
            () => shutdownRequested.TrySetResult());

        var first = new ShutdownRequestedEventArgs();
        coordinator.Request(first);

        Assert.True(first.Cancel);
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(shutdownRequested.Task.IsCompleted);

        var repeated = new ShutdownRequestedEventArgs();
        coordinator.Request(repeated);

        Assert.True(repeated.Cancel);
        Assert.Equal(1, Volatile.Read(ref cleanupCalls));

        releaseCleanup.TrySetResult();
        await shutdownRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CleanupFailureStillRequestsShutdown()
    {
        var shutdownRequested = CreateCompletion();
        var coordinator = new ApplicationShutdownCoordinator(
            () => ValueTask.FromException(new InvalidOperationException("cleanup failed")),
            () => shutdownRequested.TrySetResult());
        var eventArgs = new ShutdownRequestedEventArgs();

        coordinator.Request(eventArgs);

        Assert.True(eventArgs.Cancel);
        await shutdownRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static TaskCompletionSource CreateCompletion()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
