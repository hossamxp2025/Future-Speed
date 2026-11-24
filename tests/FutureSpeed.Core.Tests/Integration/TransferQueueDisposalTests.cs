using FutureSpeed.Core.Transfer;
using Xunit;

namespace FutureSpeed.Core.Tests.Integration;

public sealed class TransferQueueDisposalTests
{
    [Fact]
    public async Task Disposing_twice_is_safe()
    {
        // The app disposes the queue explicitly on shutdown (to write a last checkpoint)
        // and the DI container disposes it again straight after.
        using var workspace = new TestWorkspace();
        var queue = new TransferQueue(workspace.CreateEngine());

        await queue.DisposeAsync();
        await queue.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_an_idle_queue_completes_quickly()
    {
        using var workspace = new TestWorkspace();
        var queue = new TransferQueue(workspace.CreateEngine());

        var dispose = queue.DisposeAsync().AsTask();
        var finished = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(dispose, finished);
    }
}
