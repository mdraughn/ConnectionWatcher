using MQTTnet;

namespace ConNotify;

/// <summary>The one place MQTT client options are built, so the notifier and the Test button connect alike.</summary>
public static class MqttConnection
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static MqttClientOptionsBuilder Options(string host, int port, string? username, string? password)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithTimeout(ConnectTimeout);
        if (username is { Length: > 0 })
            builder = builder.WithCredentials(username, password ?? "");
        return builder;
    }
}
