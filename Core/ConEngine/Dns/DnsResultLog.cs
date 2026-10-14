using ConCore;

namespace ConEngine.Dns;

/// <summary>One DNS probe outcome, as the transceiver classified it.</summary>
public sealed record DnsResult(DateTimeOffset At, bool Success, string Message);

/// <summary>
/// The last <see cref="Keep"/> DNS results per Resolver target, in memory and
/// per engine like the loss windows (spec 2026-10-10 §4.2). The notes
/// (ResolverLying, NameUnresolvable) and the status lookupNames[] read it.
/// </summary>
public sealed class DnsResultLog
{
    public const int Keep = 20;

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Queue<DnsResult>> _results = new();

    public void Record(Guid targetId, PingResult result)
    {
        lock (_lock)
        {
            if (!_results.TryGetValue(targetId, out var queue))
                _results[targetId] = queue = new Queue<DnsResult>(Keep);
            queue.Enqueue(new DnsResult(result.Timestamp, result.Success, result.Message));
            while (queue.Count > Keep)
                queue.Dequeue();
        }
    }

    /// <summary>Oldest first; empty for a target with no DNS samples.</summary>
    public IReadOnlyList<DnsResult> Recent(Guid targetId)
    {
        lock (_lock)
            return _results.TryGetValue(targetId, out var queue) ? queue.ToList() : [];
    }
}
