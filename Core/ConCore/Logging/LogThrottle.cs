namespace ConCore.Logging;

/// <summary>Keeps a repeating error line from flooding the log: the first occurrence is
/// logged, then at most one line per window, carrying how many were suppressed since.</summary>
public sealed class LogThrottle(TimeSpan window)
{
    private readonly Lock _lock = new();
    private DateTimeOffset? _lastLogged;
    private int _suppressed;

    public bool ShouldLog(DateTimeOffset now, out int suppressedSince)
    {
        lock (_lock)
        {
            if (_lastLogged is { } last && now - last < window)
            {
                _suppressed++;
                suppressedSince = 0;
                return false;
            }
            suppressedSince = _suppressed;
            _suppressed = 0;
            _lastLogged = now;
            return true;
        }
    }
}
