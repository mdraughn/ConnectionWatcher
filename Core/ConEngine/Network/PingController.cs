using System.Net;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System;
using System.Threading.Channels;
using System.Xml;
using ConCore;
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
    private bool _disposed = false;
    private Task? _pingTask;
    private IPAddress? _ipAddress = null;
    private ChannelWriter<PingResult>? _resultWriter;
    private readonly CancellationTokenSource _localCancellationTokenSource = new();
    private CancellationToken _cancellationToken;

    /// <summary>
    /// Starts pinging the specified IP address and sending results through the channel.
    /// </summary>
    /// <param name="ipAddress">The IP address to ping.</param>
    /// <param name="resultWriter">Channel to report results.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>A task that completes when the ping operation is stopped.</returns>
    public Task Launch(IPAddress ipAddress, ChannelWriter<PingResult> resultWriter, CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PingController));

        // Start running.
        _cancellationToken =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _localCancellationTokenSource.Token).Token;
        _ipAddress = ipAddress;
        _resultWriter = resultWriter;

        // Start long-running ping task
        _pingTask = Task.Run(async () =>
        {
            try
            {
                // Create the transceiver to send and receive pings for the specified IP address
                log.LogTrace("Creating ping transceiver for {ipAddress} ...", _ipAddress);
                using var ping = pingTransceiverFactory.Create(_ipAddress, _cancellationToken);

                // Run until we finish or are cancelled
                while (IsRunnable())
                {
                    try
                    {
                        log.LogTrace("Pinging {ipAddress} ...", _ipAddress);
                        var reply = await ping.PingAsync(5000).ConfigureAwait(false);
                        if (reply.Success)
                        {
                            log.LogTrace("Ping received from {ipAddress}, {pingTime} ms", _ipAddress, reply.RoundtripTime);
                        }
                        else
                        {
                            log.LogTrace("Ping timeout for {ipAddress}", _ipAddress);
                        }

                        // Send the ping result through the channel
                        await SendMessage(ipAddress, reply);

                        // Wait before next ping (only if we're still running)
                        if (IsRunnable())
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(1000), timeProvider, _cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
                    {
                        // Expected cancellation, exit gracefully
                        log.LogTrace("Ping for {ipAddress} has been cancelled", _ipAddress);
                        break;
                    }
                    catch (Exception pingEx)
                    {
                        log.LogError(pingEx, "Error pinging {ipAddress}", _ipAddress);
                        // Handle individual ping errors - send error result but continue pinging
                        var errorResult = new PingResult(ipAddress, false, 0, $"Ping error: {pingEx.Message}");
                        await SendMessage(ipAddress, errorResult);

                        // Wait before retrying
                        if (IsRunnable())
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(1000), timeProvider, _cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Handle cancellation
                log.LogTrace("Ping for {ipAddress} has been cancelled", _ipAddress);
            }
            catch (Exception ex)
            {
                // Handle unexpected errors
                log.LogError(ex, "Error pinging {ipAddress}", _ipAddress);
            }
            finally
            {
                _resultWriter.Complete();
            }
        }, _cancellationToken);

        return _pingTask;
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
    private async Task SendMessage(IPAddress ipAddress, PingResult result)
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