using BoxPilot.Core.Infrastructure;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Tests;

public sealed class CoreServiceClientTests
{
    [Fact]
    public async Task UnresponsiveRequestFailsAtItsDeadline()
    {
        var exception = await Assert.ThrowsAsync<CoreServiceException>(() =>
            CoreServiceClient.RunWithTimeoutAsync(
                cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));

        Assert.Equal(CoreServiceFailure.Unavailable, exception.Failure);
        Assert.Equal(CoreServiceErrorCodes.Unavailable, exception.Message);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsServiceFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CoreServiceClient.RunWithTimeoutAsync(
                cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                TimeSpan.FromSeconds(1),
                cancellation.Token));
    }
}
