using System.Net;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System;

namespace ConEngine.Network;

public class PingController(TimeProvider timeProvider,
    IPingTransceiverFactory pingTransceiverFactory,
    CancellationToken cancellationToken)
{
    private TaskCompletionSource<bool>? _taskCompletionSource;
    public Task<bool> Start(IPAddress ipAddress)
    {
        if (_taskCompletionSource is null)
        {
            _taskCompletionSource = new TaskCompletionSource<bool>();
            
            // Start long-running ping task
            _ = Task.Run(async () =>
            {
                try
                {
                    using var ping = pingTransceiverFactory.Create(ipAddress, cancellationToken);

                    while (!_taskCompletionSource.Task.IsCompleted)
                    {
                        var reply = await ping.PingAsync(5000);

                        if (reply.Success)
                        {
                            // Nothing to do, just continue
                        }

                        // Wait before next ping
                        await Task.Delay(TimeSpan.FromMilliseconds(1000), timeProvider, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _taskCompletionSource.TrySetException(ex);
                }
            });
        }
        return _taskCompletionSource.Task;
    }
    
    public void Stop()
    {
        _taskCompletionSource?.TrySetResult(true);
        _taskCompletionSource = null;
    }
}