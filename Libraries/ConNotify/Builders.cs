using System.Text;
using System.Text.Json;
using ConCore.Targets;
using ConEngine;
using ConEngine.Dns;
using ConEngine.Events;
using ConEngine.Topology;
using ConEngine.Tracking;
using ConPersistence.Queries;

namespace ConNotify;

/// <summary>MQTT/HA-safe slugs: lowercase, [a-z0-9] kept, runs of anything else → one '_', collisions suffixed.</summary>
public class Slugger
{
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public Slugger()
    {
    }

    /// <summary>Slugs that are taken before the first name is seen.</summary>
    public Slugger(IEnumerable<string> reserved) => _used.UnionWith(reserved);

    public string Slug(string name)
    {
        var sb = new StringBuilder();
        var lastWasSeparator = true;
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                sb.Append('_');
                lastWasSeparator = true;
            }
        }
        var slug = sb.ToString().Trim('_');
        if (slug.Length == 0)
            slug = "target";

        var candidate = slug;
        for (int i = 2; !_used.Add(candidate); i++)
            candidate = $"{slug}_{i}";
        return candidate;
    }
}

/// <summary>State payloads: HA connectivity semantics.</summary>
public static class StateMapping
{
    /// <summary>ON = reachable-ish (incl. degraded); OFF = Down/Unreachable.</summary>
    public static string TargetPayload(HostState state) =>
        state.IsFailed() ? "OFF" : "ON";

    /// <summary>The headline binary: ON unless the verdict says the LINK is down or name lookups are failing (spec 2026-10-10 D9).</summary>
    public static string InternetPayload(string verdict) =>
        verdict is "LinkDown" or "DnsDown" ? "OFF" : "ON";

    /// <summary>The name-resolution binary (spec 2026-10-10 §5.4): OFF only when every
    /// resolver is down; Degraded/Filtered/Unknown still read ON. Not-watched is handled by
    /// availability, never by OFF — an automation must not fire because the watch was switched off.</summary>
    public static string DnsPayload(DnsSummaryState state) => state == DnsSummaryState.Down ? "OFF" : "ON";

    public static string DnsAvailabilityPayload(bool watched) => watched ? "online" : "offline";
}

/// <summary>A resolver as the dns sensor's attributes list it.</summary>
public sealed record DnsResolverEntry(string Name, string Address, string State)
{
    public static DnsResolverEntry From(TargetMonitor m) => new(m.Target.Name, m.Target.Address, m.CurrentState.ToString());
}

/// <summary>Everything the dns entity publishes, so a periodic tick can tell "unchanged" from
/// "the summary moved without a transition" (Filtered is decided from the result log).</summary>
public sealed record DnsPublication(string Availability, string State, string Attributes)
{
    public static DnsPublication From(WatcherEngine engine)
    {
        var watch = engine.DnsWatch;
        var summary = DnsSummary.Compute(watch, engine.DnsResults);
        return new(StateMapping.DnsAvailabilityPayload(summary.Watched), StateMapping.DnsPayload(summary.State),
            HaDiscovery.DnsAttributes(summary, engine.Verdict?.Current.Dns,
                watch.System.Select(DnsResolverEntry.From).ToList(), watch.Upstream.Select(DnsResolverEntry.From).ToList()));
    }
}

/// <summary>All topic strings derive from here — one source of truth.</summary>
public class MqttTopics(string baseTopic, string discoveryPrefix)
{
    public string Base { get; } = baseTopic.Trim('/');
    public string Availability => $"{Base}/availability";
    public string VerdictState => $"{Base}/verdict/state";
    public string VerdictAttributes => $"{Base}/verdict/attributes";
    public string InternetState => $"{Base}/internet/state";
    public string DnsState => $"{Base}/dns/state";
    public string DnsAttributes => $"{Base}/dns/attributes";
    public string DnsAvailability => $"{Base}/dns/availability";
    public string Transitions => $"{Base}/transitions";
    public string TargetState(string slug) => $"{Base}/target/{slug}/state";
    public string TargetAttributes(string slug) => $"{Base}/target/{slug}/attributes";

    /// <summary>Slugs the fixed binary sensors' discovery topics use ({prefix}/binary_sensor/{base}_{slug}/config);
    /// a target must never take one, or its retained config would overwrite that entity's. The verdict is a
    /// sensor with its own topics, so it does not collide and is deliberately not reserved.</summary>
    public static readonly IReadOnlyList<string> ReservedSlugs = ["internet", "dns"];

    private string Prefix { get; } = discoveryPrefix.Trim('/');
    public string VerdictDiscovery => $"{Prefix}/sensor/{Base}_verdict/config";
    public string InternetDiscovery => $"{Prefix}/binary_sensor/{Base}_internet/config";
    public string DnsDiscovery => $"{Prefix}/binary_sensor/{Base}_dns/config";
    public string TargetDiscovery(string slug) => $"{Prefix}/binary_sensor/{Base}_{slug}/config";
}

/// <summary>Home Assistant MQTT discovery documents.</summary>
public static class HaDiscovery
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private static object Device(MqttTopics topics) => new
    {
        identifiers = new[] { topics.Base },
        name = "ConnectionWatcher",
        manufacturer = "DSoftCo",
    };

    public static string TargetConfig(MqttTopics topics, string name, string slug, Guid targetId) =>
        JsonSerializer.Serialize(new
        {
            name,
            unique_id = $"{topics.Base}_{targetId:N}",
            state_topic = topics.TargetState(slug),
            json_attributes_topic = topics.TargetAttributes(slug),
            device_class = "connectivity",
            availability_topic = topics.Availability,
            device = Device(topics),
        }, Options);

    /// <summary>A target sensor's attributes: whether it is critical (spec §4.7)
    /// and whether it is a LAN device, so automations can single either out.</summary>
    public static string TargetAttributes(bool critical, TargetScope scope) =>
        JsonSerializer.Serialize(new { critical, scope = scope.ToString() }, Options);

    public static string VerdictConfig(MqttTopics topics) =>
        JsonSerializer.Serialize(new
        {
            name = "Internet verdict",
            unique_id = $"{topics.Base}_verdict",
            state_topic = topics.VerdictState,
            json_attributes_topic = topics.VerdictAttributes,
            icon = "mdi:weather-partly-cloudy",
            availability_topic = topics.Availability,
            device = Device(topics),
        }, Options);

    public static string InternetConfig(MqttTopics topics) =>
        JsonSerializer.Serialize(new
        {
            name = "Internet",
            unique_id = $"{topics.Base}_internet",
            state_topic = topics.InternetState,
            device_class = "connectivity",
            availability_topic = topics.Availability,
            device = Device(topics),
        }, Options);

    /// <summary>Unavailable when either the node or the watch is off (availability_mode "all").</summary>
    public static string DnsConfig(MqttTopics topics) =>
        JsonSerializer.Serialize(new
        {
            name = "Name resolution",
            unique_id = $"{topics.Base}_dns",
            state_topic = topics.DnsState,
            json_attributes_topic = topics.DnsAttributes,
            device_class = "connectivity",
            availability = new[] { new { topic = topics.Availability }, new { topic = topics.DnsAvailability } },
            availability_mode = "all",
            device = Device(topics),
        }, Options);

    private static object? Blame(DnsBlame? b) => b is null ? null : new { kind = b.Kind.ToString(), message = b.Message, resolvers = b.Resolvers };

    /// <summary>Keys are always present (attribute templates need them): state, blame (null when none),
    /// systemResolvers, upstreamResolvers.</summary>
    public static string DnsAttributes(DnsSummary summary, DnsBlame? blame,
        IReadOnlyList<DnsResolverEntry> system, IReadOnlyList<DnsResolverEntry> upstream) =>
        JsonSerializer.Serialize(new
        {
            state = summary.State.ToString(),
            blame = Blame(blame),
            systemResolvers = system.Select(r => new { name = r.Name, address = r.Address, state = r.State }),
            upstreamResolvers = upstream.Select(r => new { name = r.Name, address = r.Address, state = r.State }),
        }, Options);

    public static string VerdictAttributes(string? detail, string? @operator, DnsBlame? dnsBlame) =>
        JsonSerializer.Serialize(new { detail, @operator, dnsBlame = Blame(dnsBlame) }, Options);
}

/// <summary>Webhook + MQTT transition-event body: the shared transition DTO.</summary>
public static class WebhookPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string ToJson(TargetTransition t) =>
        JsonSerializer.Serialize(TransitionDto.From(t), Options);
}
