using System.Net;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System;
using System.Threading.Channels;
using System.Xml;
using ConCore;

namespace ConEngine.Network;

/// <summary>
/// This class manages ping operations to a specific IP address.
/// </summary>
/// <param name="timeProvider"></param>
/// <param name="pingTransceiverFactory"></param>
public class PingController(TimeProvider timeProvider, IPingTransceiverFactory pingTransceiverFactory) : IDisposable
{
    private TaskCompletionSource<bool> _taskCompletionSource = new();
    private Task? _pingTask;
    private bool _disposed = false;
    private IPAddress? _ipAddress = null;
    private ChannelWriter<PingResult>? _resultWriter;
    private CancellationToken _cancellationToken;

    /// <summary>
    /// Starts pinging the specified IP address and sending results through the channel.
    /// </summary>
    /// <param name="ipAddress">The IP address to ping.</param>
    /// <param name="resultWriter">Channel to report results.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>A task that completes when the ping operation is stopped.</returns>
    public Task<bool> Start(IPAddress ipAddress, ChannelWriter<PingResult> resultWriter, CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PingController));

        if (_taskCompletionSource is null)
        {
            _cancellationToken = cancellationToken;
            _taskCompletionSource = new TaskCompletionSource<bool>();
            _ipAddress = ipAddress;
            _resultWriter = resultWriter;

            // Start long-running ping task
            _pingTask = Task.Run(async () =>
            {
                try
                {
                    // Create the transceiver to send and receive pings for the specified IP address
                    using var ping = pingTransceiverFactory.Create(_ipAddress, _cancellationToken);

                    // Run until we finish or are cancelled
                    while (IsRunnable())
                    {
                        try
                        {
                            var reply = await ping.PingAsync(5000).ConfigureAwait(false);

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
                            break;
                        }
                        catch (Exception pingEx)
                        {
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

                    _taskCompletionSource?.TrySetResult(true);
                }
                catch (OperationCanceledException)
                {
                    // Handle cancellation
                    _taskCompletionSource?.TrySetResult(false);
                }
                catch (Exception ex)
                {
                    // Handle unexpected errors
                    _taskCompletionSource?.TrySetException(ex);
                }
                finally
                {
                    _resultWriter.Complete();
                }
            }, _cancellationToken);
        }
        return _taskCompletionSource.Task;
    }

    /// <summary>
    /// Determines whether the task can be started or continued based on its completion and cancellation state.
    /// </summary>
    /// <returns>true if the task has not completed and cancellation has not been requested; otherwise, false.</returns>
    private bool IsRunnable()
    {
        return !_taskCompletionSource.Task.IsCompleted && !_cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// Stops the ping operations.
    /// </summary>
    public void Stop()
    {
        _taskCompletionSource?.TrySetResult(true);
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
                Console.WriteLine($"[{ipAddress}] Warning: Failed to send message to channel");
            }
        }
        catch (Exception ex)
        {
            // Log the error but don't let it stop the ping operations
            Console.WriteLine($"[{ipAddress}] Error sending message: {ex.Message}");
        }
    }

    /// <summary>
    /// Waits for the ping operations to complete or timeout.
    /// </summary>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <returns>True if the operations completed successfully, false if timeout occurred.</returns>
    public async Task<bool> WaitForCompletion(TimeSpan timeout)
    {
        if (_taskCompletionSource?.Task == null)
            return true; // Not running

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await _taskCompletionSource.Task.WaitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false; // Timeout
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