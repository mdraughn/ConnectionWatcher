using System.Text.Json;
using ConEngine.Events;
using ConPersistence.Queries;

namespace ConNotify;

/// <summary>Webhook + MQTT transition-event body: the shared transition DTO.</summary>
public static class WebhookPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string ToJson(TargetTransition t) =>
        JsonSerializer.Serialize(TransitionDto.From(t), Options);
}
