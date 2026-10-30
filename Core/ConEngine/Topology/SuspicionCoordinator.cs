using System.Reactive.Subjects;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;

namespace ConEngine.Topology;

/// <summary>
/// Reacts to the first sign of trouble: a lost ping from a healthy anchor or
/// trunk target triggers a corroboration sweep (burst everything relevant),
/// and every target's sub-Faltering loss episodes are tracked and emitted as
/// glitch events. Overlapping anchor episodes correlate into Internet
/// glitches. Never throws into the ping path; never touches host states.
/// </summary>
public class SuspicionCoordinator : IDisposable
{
    private static readonly TimeSpan CoalesceGap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CorrelationSlack = TimeSpan.FromSeconds(10);
    private const int CloseAfterSuccesses = 5;
    private const int GlitchFloorLosses = 2;

    private sealed class Episode
    {
        public DateTimeOffset FirstLoss;
        public DateTimeOffset LastLoss;
        public int LostCount;
        public int SampleCount;
        public int ConsecutiveSuccesses;
        public bool CountedInInternet;
    }

    private sealed record RecentGlitch(Guid TargetId, DateTimeOffset Start, DateTimeOffset End);

    private readonly IReadOnlyDictionary<Guid, TargetMonitor> _monitors;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Lock _lock = new();

    private readonly HashSet<Guid> _anchorIds;
    private readonly HashSet<Guid> _trunkIds;
    private readonly List<TargetMonitor> _sweepSet;
    private readonly int _weatherThreshold;

    private readonly Dictionary<Guid, Episode> _episodes = new();
    private readonly Dictionary<Guid, HostState> _priorStates = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastGlitchEnd = new();
    private readonly List<RecentGlitch> _recentAnchorGlitches = new();
    private readonly List<RecentGlitch> _recentTrunkGlitches = new();

    private readonly Subject<GlitchEvent> _glitchSubject = new();
    public IObservable<GlitchEvent> GlitchSource => _glitchSubject;

    public SuspicionCoordinator(TargetGraph graph, PoolDefinition? anchorPool,
        IReadOnlyDictionary<Guid, TargetMonitor> monitors, int weatherThreshold,
        TimeProvider timeProvider, ILogger<SuspicionCoordinator> log)
    {
        _monitors = monitors;
        _weatherThreshold = weatherThreshold;
        _time = timeProvider;
        _log = log;

        _anchorIds = anchorPool is null
            ? new HashSet<Guid>()
            : anchorPool.MemberTargetIds.Where(monitors.ContainsKey).ToHashSet();

        _trunkIds = graph.TrunkIds(_anchorIds, monitors.ContainsKey).ToHashSet();

        _sweepSet = _anchorIds.Concat(_trunkIds).Select(id => monitors[id]).ToList();
    }

    /// <summary>Called by the engine for every ping result, after the tracker
    /// recorded it (so CurrentState reflects this sample).</summary>
    public void OnPingSample(Guid targetId, PingResult result)
    {
        try
        {
            lock (_lock)
            {
                ProcessLocked(targetId, result);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Suspicion processing failed for {TargetId}; ping flow unaffected", targetId);
        }
    }

    private void ProcessLocked(Guid targetId, PingResult result)
    {
        if (!_monitors.TryGetValue(targetId, out var monitor))
            return;

        var priorState = _priorStates.TryGetValue(targetId, out var known)
            ? known : monitor.CurrentState;
        bool priorHealthy = priorState.IsHealthy();
        var timestamp = result.Timestamp;

        if (!result.Success && priorHealthy &&
            (_anchorIds.Contains(targetId) || _trunkIds.Contains(targetId)))
        {
            Sweep();
        }

        var episode = _episodes.GetValueOrDefault(targetId);
        if (result.Success)
        {
            if (episode is not null)
            {
                episode.SampleCount++;
                if (++episode.ConsecutiveSuccesses >= CloseAfterSuccesses)
                    CloseEpisode(targetId, monitor, episode);
            }
        }
        else
        {
            if (episode is null && priorHealthy)
            {
                episode = new Episode { FirstLoss = timestamp };
                _episodes[targetId] = episode;
            }
            if (episode is not null)
            {
                episode.LostCount++;
                episode.SampleCount++;
                episode.LastLoss = timestamp;
                episode.ConsecutiveSuccesses = 0;
            }
        }

        // Any state transition during the episode hands ownership to the
        // transition ledger: Faltering+ is recorded there, Unreachable is the
        // parent's problem either way.
        var stateNow = monitor.CurrentState;
        if (_episodes.ContainsKey(targetId) &&
            !stateNow.IsHealthy())
        {
            _episodes.Remove(targetId);
        }
        _priorStates[targetId] = stateNow;
    }

    /// <summary>Bursts every anchor and trunk target. Rate limiting is the
    /// discipline's job: PingDiscipline.SuspicionGuard refuses a new burst
    /// within 60 s of the last one on that target (owner decision 2026-09-18).</summary>
    private void Sweep()
    {
        foreach (var monitor in _sweepSet)
            monitor.Discipline.TriggerBurst();
    }

    private void CloseEpisode(Guid targetId, TargetMonitor monitor, Episode episode)
    {
        _episodes.Remove(targetId);
        if (episode.LostCount < GlitchFloorLosses)
            return;

        bool coalesced = _lastGlitchEnd.TryGetValue(targetId, out var previousEnd) &&
                         episode.FirstLoss - previousEnd < CoalesceGap;
        _lastGlitchEnd[targetId] = episode.LastLoss;

        _glitchSubject.OnNext(new GlitchEvent
        {
            TargetId = targetId,
            TargetName = monitor.Target.Name,
            Class = GlitchClass.Target,
            StartUtc = episode.FirstLoss,
            EndUtc = episode.LastLoss,
            LostCount = episode.LostCount,
            SampleCount = episode.SampleCount,
            Coalesced = coalesced,
        });

        if (_trunkIds.Contains(targetId))
            _recentTrunkGlitches.Add(new RecentGlitch(targetId, episode.FirstLoss, episode.LastLoss));
        if (_anchorIds.Contains(targetId))
        {
            _recentAnchorGlitches.Add(new RecentGlitch(targetId, episode.FirstLoss, episode.LastLoss));
            CorrelateInternetGlitch();
        }
        PruneRecent(_time.GetUtcNow());
    }

    private void CorrelateInternetGlitch()
    {
        // Emitted-but-unconsumed anchor glitches plus open anchor episodes
        // that overlap (with slack) form a cluster; enough of them = one
        // Internet glitch.
        var newest = _recentAnchorGlitches[^1];
        var cluster = _recentAnchorGlitches
            .Where(g => Overlaps(g.Start, g.End, newest.Start, newest.End))
            .ToList();
        var contributors = cluster.Select(g => g.TargetId).ToHashSet();
        foreach (var (id, open) in _episodes)
        {
            if (_anchorIds.Contains(id) && !open.CountedInInternet && open.LostCount > 0 &&
                Overlaps(open.FirstLoss, open.LastLoss, newest.Start, newest.End))
                contributors.Add(id);
        }

        if (contributors.Count < _weatherThreshold)
            return;

        var start = cluster.Min(g => g.Start);
        var end = cluster.Max(g => g.End);
        bool trunkAffected = _recentTrunkGlitches.Any(g => Overlaps(g.Start, g.End, start, end)) ||
            _episodes.Any(kv => _trunkIds.Contains(kv.Key) && kv.Value.LostCount > 0 &&
                Overlaps(kv.Value.FirstLoss, kv.Value.LastLoss, start, end));

        var names = contributors.Select(id => _monitors[id].Target.Name).OrderBy(n => n);
        _glitchSubject.OnNext(new GlitchEvent
        {
            TargetId = VerdictCoordinator.SubjectId,
            TargetName = VerdictCoordinator.SubjectName,
            Class = GlitchClass.Internet,
            StartUtc = start,
            EndUtc = end,
            LostCount = 0,
            SampleCount = 0,
            Detail = $"anchors: {string.Join(", ", names)}; " +
                     (trunkAffected ? "trunk affected (likely link/ISP)" : "trunk clean (upstream)"),
        });

        // Consume the cluster so one wobble yields one Internet glitch.
        _recentAnchorGlitches.RemoveAll(cluster.Contains);
        foreach (var id in contributors)
        {
            if (_episodes.TryGetValue(id, out var open))
                open.CountedInInternet = true;
        }
    }

    private static bool Overlaps(DateTimeOffset aStart, DateTimeOffset aEnd,
        DateTimeOffset bStart, DateTimeOffset bEnd) =>
        aStart - CorrelationSlack <= bEnd && bStart - CorrelationSlack <= aEnd;

    private void PruneRecent(DateTimeOffset now)
    {
        _recentAnchorGlitches.RemoveAll(g => now - g.End > TimeSpan.FromSeconds(120));
        _recentTrunkGlitches.RemoveAll(g => now - g.End > TimeSpan.FromSeconds(120));
    }

    public void Dispose() => _glitchSubject.OnCompleted();
}
