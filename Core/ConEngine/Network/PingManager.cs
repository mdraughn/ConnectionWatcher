using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ConCore;

namespace ConEngine.Network;

public class PingManager : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly IPingTransceiverFactory _pingTransceiverFactory;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<IPAddress, PingController> _pingControllers = new();
    private readonly Channel<PingResult> _resultChannel;
    private readonly ChannelWriter<PingResult> _resultWriter;
    private readonly ChannelReader<PingResult> _resultReader;
    private readonly Task _messageProcessingTask;
    private bool _disposed = false;

    public PingManager(TimeProvider timeProvider,
        IPingTransceiverFactory pingTransceiverFactory,
        CancellationToken cancellationToken)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _pingTransceiverFactory = pingTransceiverFactory ?? throw new ArgumentNullException(nameof(pingTransceiverFactory));
        _cancellationToken = cancellationToken;

        // Create unbounded channel for ping results
        _resultChannel = Channel.CreateUnbounded<PingResult>();
        _resultWriter = _resultChannel.Writer;
        _resultReader = _resultChannel.Reader;

        // Start background task to process messages
        _messageProcessingTask = Task.Run(ProcessMessages, cancellationToken);
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
            var controllerId = $"Controller-{ipAddress}";
            controller = new PingController(_timeProvider, _pingTransceiverFactory); 
            controller.Start(ipAddress,
                _resultWriter,
                _cancellationToken);
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
            await foreach (var message in _resultReader.ReadAllAsync(_cancellationToken))
            {
                // Print the message - in a real application, you might want to:
                // - Log to a structured logger
                // - Store in a database
                // - Send to a monitoring system
                // - Update metrics/statistics
                Console.WriteLine($"[PingManager] {message}");

                // You could also add additional processing here, such as:
                // - Tracking success/failure rates
                // - Detecting patterns or anomalies
                // - Triggering alerts on failures
                ProcessPingResult(message);
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
    }

    /// <summary>
    /// Processes individual ping result messages. Override this method to customize message handling.
    /// </summary>
    /// <param name="message">The ping result message to process.</param>
    protected virtual void ProcessPingResult(PingResult message)
    {
        // Default implementation - you can extend this for custom logic
        if (!message.Success)
        {
            Console.WriteLine($"[PingManager] ⚠️  Ping failure detected for {message.IpAddress}: {message.Status}");
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