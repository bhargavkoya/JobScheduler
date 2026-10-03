using JobScheduler.Infrastructure.Runs;

namespace JobScheduler.Tests.Runs;

public class ChannelJobQueueTests
{
    private static async Task<Guid> ReadOneAsync(ChannelJobQueue queue, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var id in queue.ReadAllAsync(cts.Token)) return id;
        throw new InvalidOperationException("Queue completed unexpectedly.");
    }

    [Fact]
    public async Task Enqueue_ThenRead_ReturnsRunId()
    {
        var queue = new ChannelJobQueue();
        var id = Guid.NewGuid();

        await queue.EnqueueAsync(id, null, default);

        Assert.Equal(id, await ReadOneAsync(queue, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Messages_AreDeliveredInOrder()
    {
        var queue = new ChannelJobQueue();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids) await queue.EnqueueAsync(id, null, default);

        var received = new List<Guid>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var id in queue.ReadAllAsync(cts.Token))
        {
            received.Add(id);
            if (received.Count == ids.Count) break;
        }

        Assert.Equal(ids, received);
    }

    [Fact]
    public async Task DelayedEnqueue_ArrivesOnlyAfterTheDelay()
    {
        var queue = new ChannelJobQueue();
        var id = Guid.NewGuid();

        await queue.EnqueueAsync(id, TimeSpan.FromMilliseconds(300), default);

        // Not there immediately...
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadOneAsync(queue, TimeSpan.FromMilliseconds(50)));
        // ...but delivered once the delay has elapsed.
        Assert.Equal(id, await ReadOneAsync(queue, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task DelayedEnqueue_IsNotCancelledByCallersToken()
    {
        var queue = new ChannelJobQueue();
        var id = Guid.NewGuid();
        using var callerCts = new CancellationTokenSource();

        await queue.EnqueueAsync(id, TimeSpan.FromMilliseconds(100), callerCts.Token);
        callerCts.Cancel(); // the request that scheduled the retry has finished

        Assert.Equal(id, await ReadOneAsync(queue, TimeSpan.FromSeconds(3)));
    }
}
