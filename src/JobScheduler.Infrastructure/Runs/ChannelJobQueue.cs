using System.Threading.Channels;
using JobScheduler.Application.Runs;

namespace JobScheduler.Infrastructure.Runs;

/// <summary>
/// In-process queue on System.Threading.Channels. Delayed enqueues (retry backoff) are in-memory too;
/// anything lost on restart is re-queued by StartupRecovery from the database.
/// </summary>
public class ChannelJobQueue : IJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public async Task EnqueueAsync(Guid runId, TimeSpan? delay, CancellationToken ct)
    {
        if (delay is null || delay <= TimeSpan.Zero)
        {
            await _channel.Writer.WriteAsync(runId, ct);
            return;
        }

        // Deliberately not tied to the caller's token: a finished request must not cancel the retry.
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay.Value);
            await _channel.Writer.WriteAsync(runId);
        });
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
