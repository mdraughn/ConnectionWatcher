using System.Net;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System;
using System.Threading.Channels;
using ConCore;
using ConEngine.Scheduling;
using Microsoft.Extensions.Logging;

namespace ConEngine.Network;

/// <summary>
/// This class manages the pinging of a specific IP address,
/// creating and controlling ping transceivers as needed.
/// </summary>
/// <param name="timeProvider"></param>
/// <param name="pingTransceiverFactory"></param>
public class PingController(TimeProvider timeProvider, IPingTransceiverFactory pingTransceiverFactory, ILogger log) : IDisposable
{
    private readonly Lock _lock = new();
    private bool _disposed = false;
    private Task? _pingTask;
    private IPAddress? _ipAddress = null;
    private ChannelWriter<PingResult>? _resultWriter;
    private IPingCadence? _cadence;
    private Func<int>? _timeoutMsProvider;
    private readonly CancellationTokenSource _localCancellationTokenSource = new();
    private CancellationToken _cancellationToken;

    public Task? Task => _pingTask;

    /// <summary>
    /// Starts pinging the specified IP address, pacing pings via the cadence,
    /// and sending results through the channel.
    /// </summary>
    /// <param name="ipAddress">The IP address to ping.</param>
    /// <param name="resultWriter">Channel to report results.</param>
    /// <param name="cadence">Decides when each ping happens (may park indefinitely).</param>
    /// <param name="timeoutMsProvider">Per-ping timeout in milliseconds, read before
    /// every ping (bursting shortens the timeout).</param>
    /// <param name="cancellationToken"></param>
    /// <returns>A task that completes when the ping operation is stopped.</returns>
    public Task Launch(IPAddress ipAddress, ChannelWriter<PingResult> resultWriter,
        IPingCadence cadence, Func<int> timeoutMsProvider, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(PingController));

            // Start running.
            _cancellationToken =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _localCancellationTokenSource.Token).Token;
            _ipAddress = ipAddress;
            _resultWriter = resultWriter;
            _cadence = cadence;
            _timeoutMsProvider = timeoutMsProvider;

            // Start long-running ping task
            _pingTask = Task.Run(()=>PollingLoop(ipAddress), _cancellationToken);
            return _pingTask;
        }
    }

    private async Task PollingLoop(IPAddress ipAddress)
    {
        try
        {
            // Create the transceiver to send and receive pings for the specified IP address
            log.LogTrace("Creating ping transceiver for {ipAddress} ...", ipAddress);
            using var ping = pingTransceiverFactory.Create(ipAddress, _cancellationToken);

            // Run until we finish or are cancelled
            while(IsRunnable())
            {
                try
                {
                    // Cadence first: honors initial phase offsets and parking.
                    await _cadence!.WaitForNextPingAsync(_cancellationToken).ConfigureAwait(false);
                    if(!IsRunnable())
                        break;

                    var reply = await ping.PingAsync(_timeoutMsProvider!()).ConfigureAwait(false);
                    log.LogTrace("Ping result for {ipAddress}: {reply}", ipAddress, reply);
                    SendMessage(ipAddress, reply);
                }
                catch(OperationCanceledException) when(_cancellationToken.IsCancellationRequested)
                {
                    // Expected cancellation, exit gracefully
                    log.LogTrace("Ping for {ipAddress} has been cancelled", ipAddress);
                    break;
                }
                catch(Exception pingEx)
                {
                    // Fault isolation: report a failed sample and keep the loop alive.
                    log.LogError(pingEx, "Error pinging {ipAddress}", ipAddress);
                    SendMessage(ipAddress, new PingResult(ipAddress, false, 0, $"Ping error: {pingEx.Message}"));
                }
            }
        }
        catch(OperationCanceledException)
        {
            // Handle cancellation
            log.LogTrace("Ping for {ipAddress} has been cancelled", _ipAddress);
        }
        catch(Exception ex)
        {
            // Handle unexpected errors
            log.LogError(ex, "Ping loop for {ipAddress} terminated unexpectedly", _ipAddress);
        }
        // NOTE: no writer completion here. The result channel is shared by all
        // controllers and owned by PingManager; completing it would kill the
        // stream for every other host.
    }

    /// <summary>
    /// Determines whether the task can be started or continued based on its completion and cancellation state.
    /// </summary>
    /// <returns>true if the task has not completed and cancellation has not been requested; otherwise, false.</returns>
    private bool IsRunnable()
    {
        return !_cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// Stops the ping operations.
    /// </summary>
    public void Stop()
    {
        _localCancellationTokenSource.Cancel();
    }

    /// <summary>
    /// Sends a ping result message through the channel to the PingManager.
    /// </summary>
    /// <param name="ipAddress">The IP address that was pinged.</param>
    /// <param name="result">The ping result.</param>
    private void SendMessage(IPAddress ipAddress, PingResult result)
    {
        try
        {
            if (_resultWriter is null)
            {
                throw new NullReferenceException(nameof(ipAddress));
            }
            // Try to send the message - if the channel is closed, this will fail gracefully
            if (!_resultWriter.TryWrite(result))
            {
                log.LogError("[{ipAddress}] Warning: Failed to send message to channel",_ipAddress);
            }
        }
        catch (Exception ex)
        {
            // Log the error but don't let it stop the ping operations
            log.LogError("[{ipAddress}] Error sending message: {ErrorMessage}",ipAddress,ex.Message);
        }
    }

    /// <summary>
    /// Releases all resources used by the PingController.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the PingController and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Stop the ping operations
                Stop();

                // Wait for the ping task to complete (with timeout)
                if (_pingTask != null)
                {
                    try
                    {
                        _pingTask.Wait(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{_ipAddress}] Warning: Ping task did not complete cleanly: {ex.Message}");
                    }
                    finally
                    {
                        _pingTask.Dispose();
                    }
                }
            }

            _disposed = true;
        }
    }

    /// <summary>
    /// Finalizer for PingController to ensure cleanup if Dispose is not called.
    /// </summary>
    ~PingController()
    {
        Dispose(false);
    }
}