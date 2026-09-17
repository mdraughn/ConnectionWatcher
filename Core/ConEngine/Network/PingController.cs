using System.Net;
using System.Threading.Channels;
using ConCore;
using ConEngine.Scheduling;
using Microsoft.Extensions.Logging;

namespace ConEngine.Network;

/// <summary>
/// Runs the probe loop for one IP address: waits on its cadence, sends one
/// probe through its transceiver, reports the result on the shared channel.
/// </summary>
public class PingController(TimeProvider timeProvider, IPingTransceiverFactory pingTransceiverFactory, ILogger log) : IDisposable
{
    private readonly Lock _lock = new();
    private bool _disposed;
    private Task? _pingTask;
    private IPAddress? _ipAddress;
    private ChannelWriter<PingResult>? _resultWriter;
    private ProbeSpec _probe;
    private IPingCadence? _cadence;
    private Func<int>? _timeoutMsProvider;
    private readonly CancellationTokenSource _localCancellationTokenSource = new();
    private CancellationTokenSource? _linkedCts;
    private CancellationToken _cancellationToken;

    public Task? Task => _pingTask;

    /// <summary>
    /// Starts probing the specified IP address, pacing probes via the cadence,
    /// and sending results through the channel.
    /// </summary>
    /// <param name="ipAddress">The IP address to probe.</param>
    /// <param name="resultWriter">Channel to report results.</param>
    /// <param name="probe">How to probe the target (ICMP echo or TCP connect).</param>
    /// <param name="cadence">Decides when each probe happens (may park indefinitely).</param>
    /// <param name="timeoutMsProvider">Per-probe timeout in milliseconds, read before
    /// every probe (bursting shortens the timeout).</param>
    /// <param name="cancellationToken">Engine-wide cancellation.</param>
    /// <returns>A task that completes when the loop exits.</returns>
    public Task Launch(IPAddress ipAddress, ChannelWriter<PingResult> resultWriter,
        ProbeSpec probe, IPingCadence cadence, Func<int> timeoutMsProvider, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _localCancellationTokenSource.Token);
            _cancellationToken = _linkedCts.Token;
            _ipAddress = ipAddress;
            _resultWriter = resultWriter;
            _probe = probe;
            _cadence = cadence;
            _timeoutMsProvider = timeoutMsProvider;

            _pingTask = System.Threading.Tasks.Task.Run(() => PollingLoop(ipAddress), _cancellationToken);
            return _pingTask;
        }
    }

    private async Task PollingLoop(IPAddress ipAddress)
    {
        try
        {
            log.LogTrace("Creating ping transceiver for {ipAddress} ...", ipAddress);
            using var ping = pingTransceiverFactory.Create(ipAddress, _probe, _cancellationToken);

            while (!_cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Cadence first: honors initial phase offsets and parking.
                    await _cadence!.WaitForNextPingAsync(_cancellationToken).ConfigureAwait(false);
                    if (_cancellationToken.IsCancellationRequested)
                        break;

                    var reply = await ping.PingAsync(_timeoutMsProvider!()).ConfigureAwait(false);
                    log.LogTrace("Ping result for {ipAddress}: {reply}", ipAddress, reply);
                    SendMessage(reply);
                }
                catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
                {
                    log.LogTrace("Ping for {ipAddress} has been cancelled", ipAddress);
                    break;
                }
                catch (Exception pingEx)
                {
                    // Fault isolation: report a failed sample and keep the loop alive.
                    log.LogError(pingEx, "Error pinging {ipAddress}", ipAddress);
                    SendMessage(new PingResult(ipAddress, false, 0, $"Ping error: {pingEx.Message}"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            log.LogTrace("Ping for {ipAddress} has been cancelled", _ipAddress);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ping loop for {ipAddress} terminated unexpectedly", _ipAddress);
        }
        // NOTE: no writer completion here. The result channel is shared by all
        // controllers and owned by PingManager; completing it would kill the
        // stream for every other host.
    }

    /// <summary>Requests the loop to stop. Returns immediately.</summary>
    public void Stop()
    {
        _localCancellationTokenSource.Cancel();
    }

    /// <summary>Requests the loop to stop and waits (bounded) for it to exit. Never throws.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        Stop();
        if (_pingTask is not { } task)
            return;
        try
        {
            await task.WaitAsync(grace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            log.LogWarning("Ping loop for {ipAddress} did not exit within {Grace}", _ipAddress, grace);
        }
        catch (OperationCanceledException)
        {
            // The loop's own cancellation surfacing through the task: expected.
        }
    }

    private void SendMessage(PingResult result)
    {
        if (_resultWriter is null)
            return;
        // If the channel is closed this fails quietly; the manager is shutting down.
        if (!_resultWriter.TryWrite(result))
            log.LogDebug("[{ipAddress}] result dropped: channel closed", _ipAddress);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _localCancellationTokenSource.Cancel();
        _linkedCts?.Dispose();
        _localCancellationTokenSource.Dispose();
    }
}
