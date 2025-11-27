using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ConCore;
using Microsoft.Extensions.Logging;
using System.Reactive;
using System.Reactive.Subjects;

namespace ConEngine.Network;

/// <summary>
/// Manages a collection of PingControllers, each responsible for pinging a specific IP address.
/// </summary>
public class PingManager : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly IPingTransceiverFactory _pingTransceiverFactory;
    private readonly CancellationToken _globalCancellationToken;
    private readonly Dictionary<IPAddress, PingController> _pingControllers = new();
    private readonly ChannelWriter<PingResult> _resultWriter;
    private readonly ChannelReader<PingResult> _resultReader;
    private readonly Task _messageProcessingTask;
    private bool _disposed = false;
    private readonly ILogger _log;

    private readonly ReplaySubject<PingResult> _pingResultSubject = new ReplaySubject<PingResult>(bufferSize: 100);
    public IObservable<PingResult> PingResults => _pingResultSubject;

    public PingManager(TimeProvider timeProvider,
        IPingTransceiverFactory pingTransceiverFactory,
        CancellationToken globalCancellationToken,
        ILogger log)
    {
        _log = log;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _pingTransceiverFactory = pingTransceiverFactory ?? throw new ArgumentNullException(nameof(pingTransceiverFactory));
        _globalCancellationToken = globalCancellationToken;

        // Create unbounded channel for ping results
        var resultChannel = Channel.CreateUnbounded<PingResult>();
        _resultWriter = resultChannel.Writer;
        _resultReader = resultChannel.Reader;

        // Launch background task to process messages
        _messageProcessingTask = Task.Run(ProcessMessages, globalCancellationToken);
    }

    /// <summary>
    /// Gets or creates a PingController for the specified IP address.
    /// </summary>
    /// <param name="ipAddress">The IP address to ping.</param>
    /// <returns>A PingController configured for the specified IP address.</returns>
    public PingController GetOrCreateController(IPAddress ipAddress)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PingManager));

        if (!_pingControllers.TryGetValue(ipAddress, out var controller))
        {
            controller = new PingController(_timeProvider, _pingTransceiverFactory,_log); 
            controller.Launch(ipAddress,
                _resultWriter,
                _globalCancellationToken);
            _pingControllers[ipAddress] = controller;
        }
        return controller;
    }

    /// <summary>
    /// Stops and removes the controller for the specified IP address.
    /// </summary>
    /// <param name="ipAddress">The IP address whose controller should be stopped.</param>
    public void StopController(IPAddress ipAddress)
    {
        if (_pingControllers.TryGetValue(ipAddress, out var controller))
        {
            controller.Stop();
            _pingControllers.Remove(ipAddress);
        }
    }

    /// <summary>
    /// Stops all controllers managed by this PingManager.
    /// </summary>
    public void StopAllControllers()
    {
        foreach (var controller in _pingControllers.Values)
        {
            controller.Stop();
        }
        _pingControllers.Clear();
    }

    /// <summary>
    /// Gets the current count of active controllers.
    /// </summary>
    public int ActiveControllerCount => _pingControllers.Count;

    /// <summary>
    /// Background task that processes ping result messages from the channel.
    /// </summary>
    private async Task ProcessMessages()
    {
        try
        {
            await foreach (var message in _resultReader.ReadAllAsync(_globalCancellationToken))
            {
                LogPingResult(message);
                _pingResultSubject.OnNext(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
            Console.WriteLine("[PingManager] Message processing stopped due to cancellation");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PingManager] Error processing messages: {ex.Message}");
        }
        finally
        {
            _pingResultSubject.OnCompleted();
        }
    }

    private void LogPingResult(PingResult message)
    {
        if (message.Success)
        {
            _log.LogTrace("Processing success from {ipAddress}", message.IpAddress);
        } else
        {
            _log.LogTrace("Processing lost ping from {ipAddress}", message.IpAddress);
        }
    }

    /// <summary>
    /// Closes the result channel to signal no more messages will be sent.
    /// </summary>
    public void CloseChannel()
    {
        _resultWriter.TryComplete();
    }

    /// <summary>
    /// Releases all resources used by the PingManager.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the PingManager and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Stop all controllers first
                StopAllControllers();

                // Close the channel to signal no more messages
                CloseChannel();

                // Wait for message processing to complete (with timeout)
                try
                {
                    _messageProcessingTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PingManager] Warning: Message processing task did not complete cleanly: {ex.Message}");
                }

                // Dispose the message processing task
                _messageProcessingTask?.Dispose();
            }

            _disposed = true;
        }
    }

    /// <summary>
    /// Finalizer for PingManager to ensure cleanup if Dispose is not called.
    /// </summary>
    ~PingManager()
    {
        Dispose(false);
    }
}