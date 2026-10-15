using System.Text.Json;
using ConEngine.Dns;
using ConEngine.Events;
using ConEngine.Topology;
using ConEngine.Tracking;
using ConNotify;

namespace ConNotify.Tests;

public class SluggerTests
{
    [Theory]
    [InlineData("Google DNS", "google_dns")]
    [InlineData("Outer IP", "outer_ip")]
    [InlineData("bbc.co.uk", "bbc_co_uk")]
    [InlineData("  weird -- name!! ", "weird_name")]
    [InlineData("4.2.2.2", "4_2_2_2")]
    public void Slugs_are_mqtt_and_ha_safe(string name, string expected)
    {
        var slugger = new Slugger();
        Assert.Equal(expected, slugger.Slug(name));
    }

    [Fact]
    public void Collisions_get_numeric_suffixes()
    {
        var slugger = new Slugger();
        Assert.Equal("router", slugger.Slug("Router"));
        Assert.Equal("router_2", slugger.Slug("router!"));
        Assert.Equal("router_3", slugger.Slug("ROUTER?"));
    }
}

public class ReservedSlugTests
{
    [Fact]
    public void Targets_cannot_take_the_slug_of_a_fixed_entity()
    {
        var slugger = new Slugger(MqttTopics.ReservedSlugs);
        Assert.Equal("dns_2", slugger.Slug("DNS"));
        Assert.Equal("internet_2", slugger.Slug("Internet"));
        Assert.Equal("verdict", slugger.Slug("Verdict"));
        Assert.Equal("router", slugger.Slug("Router"));
    }
}

public class StateMappingTests
{
    [Theory]
    [InlineData(HostState.Responding, "ON")]
    [InlineData(HostState.Faltering, "ON")]
    [InlineData(HostState.Recovering, "ON")]
    [InlineData(HostState.Down, "OFF")]
    [InlineData(HostState.Unreachable, "OFF")]
    [InlineData(HostState.Starting, "ON")]
    public void Target_connectivity_mapping(HostState state, string expected)
    {
        Assert.Equal(expected, StateMapping.TargetPayload(state));
    }

    [Theory]
    [InlineData("Healthy", "ON")]
    [InlineData("SiteIncident", "ON")]
    [InlineData("InternetWeather", "ON")]
    [InlineData("LinkDegraded", "ON")]
    [InlineData("LinkDown", "OFF")]
    [InlineData("DnsDown", "OFF")]
    [InlineData("Unknown", "ON")]
    public void Internet_binary_is_on_unless_link_down_or_names_fail(string verdict, string expected)
    {
        Assert.Equal(expected, StateMapping.InternetPayload(verdict));
    }

    [Theory]
    [InlineData(DnsSummaryState.Ok, "ON")]
    [InlineData(DnsSummaryState.Degraded, "ON")]
    [InlineData(DnsSummaryState.Filtered, "ON")]
    [InlineData(DnsSummaryState.Unknown, "ON")]
    [InlineData(DnsSummaryState.Down, "OFF")]
    public void Dns_binary_is_off_only_when_every_resolver_is_down(DnsSummaryState state, string expected) =>
        Assert.Equal(expected, StateMapping.DnsPayload(state));

    [Fact]
    public void Dns_sensor_is_unavailable_not_off_when_the_watch_is_off() // Review Focus 2
    {
        Assert.Equal("offline", StateMapping.DnsAvailabilityPayload(watched: false));
        Assert.Equal("online", StateMapping.DnsAvailabilityPayload(watched: true));
    }
}

public class MqttTopicsTests
{
    private readonly MqttTopics _topics = new("conwatch", "homeassistant");

    [Fact]
    public void Topic_shapes()
    {
        Assert.Equal("conwatch/availability", _topics.Availability);
        Assert.Equal("conwatch/verdict/state", _topics.VerdictState);
        Assert.Equal("conwatch/verdict/attributes", _topics.VerdictAttributes);
        Assert.Equal("conwatch/internet/state", _topics.InternetState);
        Assert.Equal("conwatch/target/router/state", _topics.TargetState("router"));
        Assert.Equal("conwatch/transitions", _topics.Transitions);
        Assert.Equal("homeassistant/sensor/conwatch_verdict/config", _topics.VerdictDiscovery);
        Assert.Equal("homeassistant/binary_sensor/conwatch_internet/config", _topics.InternetDiscovery);
        Assert.Equal("homeassistant/binary_sensor/conwatch_router/config", _topics.TargetDiscovery("router"));
    }

    [Fact]
    public void Dns_topics_hang_off_the_base()
    {
        Assert.Equal("conwatch/dns/state", _topics.DnsState);
        Assert.Equal("conwatch/dns/attributes", _topics.DnsAttributes);
        Assert.Equal("conwatch/dns/availability", _topics.DnsAvailability);
        Assert.Equal("homeassistant/binary_sensor/conwatch_dns/config", _topics.DnsDiscovery);
    }
}

public class HaDiscoveryTests
{
    private static readonly MqttTopics Topics = new("conwatch", "homeassistant");

    [Fact]
    public void Target_discovery_document_shape()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var json = JsonSerializer.Deserialize<JsonElement>(
            HaDiscovery.TargetConfig(Topics, "Google DNS", "google_dns", id));

        Assert.Equal("Google DNS", json.GetProperty("name").GetString());
        Assert.Equal("conwatch/target/google_dns/state", json.GetProperty("state_topic").GetString());
        Assert.Equal("conwatch/target/google_dns/attributes", json.GetProperty("json_attributes_topic").GetString());
        Assert.Equal("conwatch_11111111222233334444555555555555", json.GetProperty("unique_id").GetString());
        Assert.Equal("connectivity", json.GetProperty("device_class").GetString());
        Assert.Equal("conwatch/availability", json.GetProperty("availability_topic").GetString());
        Assert.Equal("conwatch", json.GetProperty("device").GetProperty("identifiers")[0].GetString());
        Assert.Equal("DSoftCo", json.GetProperty("device").GetProperty("manufacturer").GetString());
    }

    [Fact]
    public void Target_attributes_carry_critical()
    {
        Assert.Equal("conwatch/target/router/attributes", Topics.TargetAttributes("router"));
        Assert.Equal("{\"critical\":true,\"scope\":\"Local\"}",
            HaDiscovery.TargetAttributes(critical: true, ConCore.Targets.TargetScope.Local));
        Assert.Equal("{\"critical\":false,\"scope\":\"Verdict\"}",
            HaDiscovery.TargetAttributes(critical: false, ConCore.Targets.TargetScope.Verdict));
    }

    [Fact]
    public void Verdict_and_internet_documents_reference_their_topics()
    {
        var verdict = JsonSerializer.Deserialize<JsonElement>(HaDiscovery.VerdictConfig(Topics));
        Assert.Equal("conwatch/verdict/state", verdict.GetProperty("state_topic").GetString());
        Assert.Equal("conwatch/verdict/attributes", verdict.GetProperty("json_attributes_topic").GetString());

        var internet = JsonSerializer.Deserialize<JsonElement>(HaDiscovery.InternetConfig(Topics));
        Assert.Equal("conwatch/internet/state", internet.GetProperty("state_topic").GetString());
        Assert.Equal("connectivity", internet.GetProperty("device_class").GetString());
    }

    [Fact]
    public void Dns_discovery_document_needs_both_availabilities()
    {
        var json = JsonSerializer.Deserialize<JsonElement>(HaDiscovery.DnsConfig(Topics));
        Assert.Equal("Name resolution", json.GetProperty("name").GetString());
        Assert.Equal("conwatch_dns", json.GetProperty("unique_id").GetString());
        Assert.Equal("conwatch/dns/state", json.GetProperty("state_topic").GetString());
        Assert.Equal("conwatch/dns/attributes", json.GetProperty("json_attributes_topic").GetString());
        Assert.Equal("connectivity", json.GetProperty("device_class").GetString());
        Assert.Equal("all", json.GetProperty("availability_mode").GetString());
        Assert.Equal(["conwatch/availability", "conwatch/dns/availability"],
            json.GetProperty("availability").EnumerateArray().Select(a => a.GetProperty("topic").GetString()!).ToArray());
        Assert.Equal("conwatch", json.GetProperty("device").GetProperty("identifiers")[0].GetString());
    }

    [Fact]
    public void Dns_attributes_carry_stable_keys_even_when_empty() // Review Focus 3
    {
        var empty = JsonSerializer.Deserialize<JsonElement>(HaDiscovery.DnsAttributes(
            new DnsSummary(true, DnsSummaryState.Ok), null,
            [new DnsResolverEntry("Your resolver 10.1.10.1", "10.1.10.1", "Responding")], []));
        Assert.Equal("Ok", empty.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("blame").ValueKind);
        Assert.Equal(0, empty.GetProperty("upstreamResolvers").GetArrayLength());
        var sys = Assert.Single(empty.GetProperty("systemResolvers").EnumerateArray());
        Assert.Equal("10.1.10.1", sys.GetProperty("address").GetString());
        Assert.Equal("Responding", sys.GetProperty("state").GetString());

        var blame = new DnsBlame(DnsBlameKind.NoResolver, "No resolver is answering, though the internet is reachable.", ["10.1.10.1"]);
        var down = JsonSerializer.Deserialize<JsonElement>(HaDiscovery.DnsAttributes(
            new DnsSummary(true, DnsSummaryState.Down), blame, [], [new DnsResolverEntry("1.1.1.1 directly", "1.1.1.1", "Down")]));
        Assert.Equal("NoResolver", down.GetProperty("blame").GetProperty("kind").GetString());
        Assert.Equal("1.1.1.1", down.GetProperty("upstreamResolvers")[0].GetProperty("address").GetString());
    }

    [Fact]
    public void Verdict_attributes_gain_the_dns_blame()
    {
        var none = JsonSerializer.Deserialize<JsonElement>(HaDiscovery.VerdictAttributes("Healthy: 2 anchors responding", null, null));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("dnsBlame").ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("operator").ValueKind);
        var blame = new DnsBlame(DnsBlameKind.YourResolver, "Your resolver 10.1.10.1 isn't answering, but 1.1.1.1 answers directly.", ["10.1.10.1"]);
        var some = JsonSerializer.Deserialize<JsonElement>(HaDiscovery.VerdictAttributes("DnsDown: …", null, blame));
        Assert.Equal("YourResolver", some.GetProperty("dnsBlame").GetProperty("kind").GetString());
        Assert.Equal("Comcast", JsonSerializer.Deserialize<JsonElement>(HaDiscovery.VerdictAttributes("d", "Comcast", null)).GetProperty("operator").GetString());
    }
}

public class WebhookPayloadTests
{
    [Fact]
    public void Payload_carries_the_transition()
    {
        var t = new TargetTransition
        {
            Kind = TransitionSubjectKind.Verdict,
            TargetId = Guid.NewGuid(),
            TargetName = "Internet",
            From = HostState.Responding,
            To = HostState.Down,
            Timestamp = DateTimeOffset.Parse("2026-08-24T12:00:00Z"),
            Reason = new TransitionReason
            {
                Trigger = TransitionTrigger.VerdictChanged,
                Verdict = "LinkDown",
                Detail = "LinkDown: Modem is Down",
            },
        };
        var json = JsonSerializer.Deserialize<JsonElement>(WebhookPayload.ToJson(t));
        Assert.Equal("Verdict", json.GetProperty("subjectKind").GetString());
        Assert.Equal("Internet", json.GetProperty("subjectName").GetString());
        Assert.Equal("Down", json.GetProperty("to").GetString());
        Assert.Equal("LinkDown", json.GetProperty("verdict").GetString());
        Assert.Contains("Modem", json.GetProperty("detail").GetString());
        Assert.Equal("2026-08-24T12:00:00", json.GetProperty("timestamp").GetString()![..19]);
    }

    [Fact]
    public void Payload_is_the_transition_dto_with_a_utc_offset_timestamp()
    {
        var t = new TargetTransition
        {
            Kind = TransitionSubjectKind.Target,
            TargetId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            TargetName = "Router",
            From = HostState.Responding,
            To = HostState.Down,
            Timestamp = DateTimeOffset.Parse("2026-08-24T12:00:00.5Z"),
            Reason = new TransitionReason { Trigger = TransitionTrigger.ThresholdCrossed, SuccessRate = 0.1, SampleSize = 10 },
            AverageRoundtripTime = 12.5f,
        };

        var json = JsonSerializer.Deserialize<JsonElement>(WebhookPayload.ToJson(t));

        Assert.Equal("2026-08-24T12:00:00.5+00:00", json.GetProperty("timestamp").GetString());
        Assert.False(json.TryGetProperty("id", out _));
        Assert.Equal(12.5, json.GetProperty("avgRtt").GetDouble());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("ancestorId").ValueKind);
        Assert.Equal("Down", json.GetProperty("to").GetString());
    }

    [Fact]
    public void A_DnsDown_verdict_carries_the_blame_as_dns_and_other_verdicts_omit_it()
    {
        var blame = new ConEngine.Topology.DnsBlame(ConEngine.Topology.DnsBlameKind.YourResolver,
            "Your resolver 10.1.10.1 isn't answering, but 1.1.1.1 answers directly.", ["10.1.10.1"]);
        var dnsDown = new TargetTransition
        {
            Kind = TransitionSubjectKind.Verdict, TargetId = Guid.NewGuid(), TargetName = "Internet",
            From = HostState.Responding, To = HostState.Down, Timestamp = DateTimeOffset.Parse("2026-10-15T12:00:00Z"),
            Reason = new TransitionReason
            {
                Trigger = TransitionTrigger.VerdictChanged, Verdict = "DnsDown",
                Detail = "DnsDown: Your resolver 10.1.10.1 isn't answering, but 1.1.1.1 answers directly.", Dns = blame,
            },
        };
        var json = JsonSerializer.Deserialize<JsonElement>(WebhookPayload.ToJson(dnsDown));
        var dns = json.GetProperty("dns");
        Assert.Equal("YourResolver", dns.GetProperty("kind").GetString());
        Assert.Contains("10.1.10.1", dns.GetProperty("message").GetString());
        Assert.Equal(["10.1.10.1"], dns.GetProperty("resolvers").EnumerateArray().Select(e => e.GetString()!).ToArray());

        var linkDown = dnsDown with { Reason = new TransitionReason { Trigger = TransitionTrigger.VerdictChanged, Verdict = "LinkDown", Detail = "LinkDown: Modem is Down" } };
        var other = JsonSerializer.Deserialize<JsonElement>(WebhookPayload.ToJson(linkDown));
        Assert.False(other.TryGetProperty("dns", out _)); // omitted, never null (Review Focus 1)
    }
}
