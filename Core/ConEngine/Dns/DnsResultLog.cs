using ConCore;

namespace ConEngine.Dns;

/// <summary>One DNS probe outcome. Outcome is the message's leading token
/// ("Verified", "Answered", "Filtered", "WrongAnswer", "TimedOut", …); Name is the asked
/// lookup name, null for a probe that sent nothing.</summary>
public sealed record DnsResult(DateTimeOffset At, bool Success, string Outcome, string? Name, string Message);

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
        var (outcome, name) = Parse(result.Message);
        lock (_lock)
        {
            if (!_results.TryGetValue(targetId, out var queue))
                _results[targetId] = queue = new Queue<DnsResult>(Keep);
            queue.Enqueue(new DnsResult(result.Timestamp, result.Success, outcome, name, result.Message));
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

    /// <summary>"Filtered: NXDOMAIN (dns.google)" → ("Filtered", "dns.google").
    /// The name is the last parenthesised group; the outcome ends at the first
    /// space or colon.</summary>
    public static (string Outcome, string? Name) Parse(string message)
    {
        var text = message.Trim();
        string? name = null;
        if (text.EndsWith(')') && text.LastIndexOf(" (", StringComparison.Ordinal) is var open and >= 0)
        {
            name = text[(open + 2)..^1];
            text = text[..open];
        }
        var end = text.IndexOfAny([' ', ':']);
        var outcome = end < 0 ? text : text[..end];
        return (outcome, name is { Length: > 0 } ? name : null);
    }
}
