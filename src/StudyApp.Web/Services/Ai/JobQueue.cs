using System.Threading.Channels;

namespace StudyApp.Web.Services.Ai;

/// <summary>
/// Hands queued job ids to the background runner. The job itself lives in the database, so
/// this only carries the id — the queue is a signal, not the source of truth.
/// </summary>
public class JobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(jobId, ct);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
