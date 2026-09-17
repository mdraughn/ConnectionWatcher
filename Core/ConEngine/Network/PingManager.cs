using System.Net;
using System.Reactive.Subjects;
using System.Threading.Channels;
using ConCore;
using ConEngine.Scheduling;
using Microsoft.Extensions.Logging;

namespace ConEngine.Network;

/// <summary>
/// Owns one PingController per target and pumps their results from the
/// shared channel onto <see cref="PingResultSource"/> on a single thread.
/// Controllers are keyed by target id, never by address: targets that share
/// an address (or probe one host two ways) stay independent.
/// </summary>
public class PingManager : IDisposable
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly IPingTransceiverFactory _pingTransceiverFactory;
    private readonly CancellationToken _globalCancellationToken;
    private readonly Dictionary<Guid, PingController> _pingControllers = new();
    private readonly ChannelWriter<RoutedPingResult> _resultWriter;
    private readonly ChannelReader<RoutedPingResult> _resultReader;
    private readonly Task _messageProcessingTask;
    private readonly ILogger<PingManager> _log;
    private readonly ILoggerFactory _loggerFactory;
    private bool _stopped;

    // Plain Subject: the engine subscribes before any controller exists, so
    // nothing ever needs a replay.
    private readonly Subject<RoutedPingResult> _pingResultSubject = new();
    public IObservable<RoutedPingResult> PingResultSource => _pingResultSubject;

    public PingManager(TimeProvider timeProvider,
        IPingTransceiverFactory pingTransceiverFactory,
        CancellationToken globalCancellationToken,
        ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<PingManager>();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _pingTransceiverFactory = pingTransceiverFactory ?? throw new ArgumentNullException(nameof(pingTransceiverFactory));
        _globalCancellationToken = globalCancellationToken;

        var resultChannel = Channel.CreateUnbounded<RoutedPingResult>();
        _resultWriter = resultChannel.Writer;
        _resultReader = resultChannel.Reader;

        _messageProcessingTask = Task.Run(ProcessMessages, globalCancellationToken);
    }

    /// <summary>Gets or creates the controller probing on behalf of <paramref name="targetId"/>.</summary>
    /// <param name="targetId">The target the results belong to.</param>
    /// <param name="ipAddress">The IP address to probe.</param>
    /// <param name="probe">How to probe the target (ICMP echo or TCP connect).</param>
    /// <param name="cadence">Decides when each probe happens.</param>
    /// <param name="timeoutMsProvider">Per-probe timeout in milliseconds, read before every probe.</param>
    public PingController GetOrCreateController(Guid targetId, IPAddress ipAddress, ProbeSpec probe,
        IPingCadence cadence, Func<int> timeoutMsProvider)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);

            if (!_pingControllers.TryGetValue(targetId, out var controller))
            {
                var controllerLogger = _loggerFactory.CreateLogger($"ConEngine.Network.PingController[{ipAddress}]");
                controller = new PingController(_timeProvider, _pingTransceiverFactory, controllerLogger);
                controller.Launch(targetId, ipAddress, _resultWriter, probe, cadence, timeoutMsProvider, _globalCancellationToken);
                _pingControllers[targetId] = controller;
                _log.LogInformation("Created PingController for {IpAddress} (target {TargetId})", ipAddress, targetId);
            }
            return controller;
        }
    }

    /// <summary>Stops and removes one target's controller (no-op when absent). Never throws.</summary>
    public async Task StopControllerAsync(Guid targetId, TimeSpan grace)
    {
        PingController? controller;
        lock (_lock)
        {
            _pingControllers.Remove(targetId, out controller);
        }
        if (controller is null)
            return;
        await controller.StopAsync(grace).ConfigureAwait(false);
        controller.Dispose();
    }

    /// <summary>
    /// Stops every controller concurrently, closes the result channel, and
    /// waits for the pump to drain. Each phase is bounded by <paramref name="grace"/>.
    /// Idempotent; never throws.
    /// </summary>
    public async Task StopAsync(TimeSpan grace)
    {
        List<PingController> controllers;
        lock (_lock)
        {
            if (_stopped)
                return;
            _stopped = true;
            controllers = _pingControllers.Values.ToList();
            _pingControllers.Clear();
        }
        _log.LogDebug("Stopping {Count} PingControllers", controllers.Count);

        await Task.WhenAll(controllers.Select(c => c.StopAsync(grace))).ConfigureAwait(false);
        foreach (var c in controllers)
            c.Dispose();

        _resultWriter.TryComplete();
        try
        {
            await _messageProcessingTask.WaitAsync(grace).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Result pump did not drain within {Grace}", grace);
        }
    }

    private async Task ProcessMessages()
    {
        try
        {
            _log.LogDebug("PingManager message processing started");
            await foreach (var routed in _resultReader.ReadAllAsync(_globalCancellationToken))
            {
                LogPingResult(routed.Result);
                _pingResultSubject.OnNext(routed);
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("PingManager message processing stopped due to cancellation");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error processing messages in PingManager");
        }
        finally
        {
            _pingResultSubject.OnCompleted();
        }
    }

    private void LogPingResult(PingResult message)
    {
        if (message.Success)
            _log.LogTrace("Processing success from {IpAddress}, {RoundtripTime}ms", message.IpAddress, message.RoundtripTime);
        else
            _log.LogTrace("Processing lost ping from {IpAddress}: {Message}", message.IpAddress, message.Message);
    }

    /// <summary>Synchronous best effort for callers that cannot await; <see cref="StopAsync"/> is the real path.</summary>
    public void Dispose()
    {
        _ = StopAsync(TimeSpan.FromSeconds(5));
        GC.SuppressFinalize(this);
    }
}
