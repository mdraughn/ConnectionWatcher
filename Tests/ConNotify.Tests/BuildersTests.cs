using System.Text.Json;
using ConEngine.Events;
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
    [InlineData("Unknown", "ON")]
    public void Internet_binary_is_on_unless_link_down(string verdict, string expected)
    {
        Assert.Equal(expected, StateMapping.InternetPayload(verdict));
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
        Assert.Equal("conwatch_11111111222233334444555555555555", json.GetProperty("unique_id").GetString());
        Assert.Equal("connectivity", json.GetProperty("device_class").GetString());
        Assert.Equal("conwatch/availability", json.GetProperty("availability_topic").GetString());
        Assert.Equal("conwatch", json.GetProperty("device").GetProperty("identifiers")[0].GetString());
        Assert.Equal("DSoftCo", json.GetProperty("device").GetProperty("manufacturer").GetString());
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
}
