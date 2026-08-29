using MQTTnet;

namespace ConNotify;

/// <summary>One-shot broker connectivity check for the admin Test button.</summary>
public static class MqttTester
{
    public static async Task<(bool Ok, string Message)> TestAsync(
        string host, int port, string? username, string? password, CancellationToken ct = default)
    {
        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithTimeout(TimeSpan.FromSeconds(5));
        if (username is { Length: > 0 })
            optionsBuilder = optionsBuilder.WithCredentials(username, password ?? "");

        try
        {
            using var client = new MqttClientFactory().CreateMqttClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(7));
            var result = await client.ConnectAsync(optionsBuilder.Build(), timeout.Token).ConfigureAwait(false);
            if (result.ResultCode != MqttClientConnectResultCode.Success)
                return (false, $"Broker refused connection: {result.ResultCode}");
            await client.DisconnectAsync().ConfigureAwait(false);
            return (true, $"Connected to {host}:{port} OK.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"Timed out connecting to {host}:{port}.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }
}
