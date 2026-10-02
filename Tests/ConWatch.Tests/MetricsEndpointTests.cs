using System.Net;
using System.Net.Http.Json;

namespace ConWatch.Tests;

public class MetricsEndpointTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public MetricsEndpointTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<string> ScrapeAsync()
    {
        var response = await _client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"scrape returned {(int)response.StatusCode}: {body[..Math.Min(800, body.Length)]}");
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.ToString());
        return body;
    }

    private static double Value(string body, string series)
    {
        var line = body.Split('\n').FirstOrDefault(l => l.StartsWith(series + " "));
        Assert.False(line is null, $"series '{series}' not found in scrape");
        return double.Parse(line!.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Scrape_works_before_any_configuration()
    {
        var body = await ScrapeAsync();
        Assert.Contains("# TYPE conwatch_build_info gauge", body);
        Assert.Contains("conwatch_build_info{version=\"", body);
        Assert.Contains("# TYPE conwatch_engine_enabled gauge", body);
        Assert.DoesNotContain("conwatch_last_sample_age_seconds ", body);
    }

    [Fact]
    public async Task Last_sample_age_is_exposed_once_results_flow()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        string body = "";
        await ConfigApiTests.Eventually(async () =>
        {
            body = await ScrapeAsync();
            return body.Contains("conwatch_last_sample_age_seconds ");
        });
        Assert.Contains("# TYPE conwatch_last_sample_age_seconds gauge", body);
        var age = Value(body, "conwatch_last_sample_age_seconds");
        Assert.InRange(age, 0, 60);
    }

    [Fact]
    public async Task Gauges_reflect_a_healthy_home()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        // Seeding posts several config writes, each debouncing a rebuild;
        // require the whole steady state in ONE scrape and assert on that
        // same body, so a trailing rebuild can't slip between wait and check.
        var body = "";
        await ConfigApiTests.Eventually(async () =>
        {
            body = await ScrapeAsync();
            return body.Contains("conwatch_verdict{state=\"Healthy\"} 1")
                && body.Contains("conwatch_target_up{name=\"Router\"} 1")
                && body.Contains("conwatch_pool_up{name=\"Internet\"} 1")
                && body.Contains("conwatch_target_sample_size{name=\"Google\"}")
                && !body.Contains("conwatch_target_sample_size{name=\"Google\"} 0");
        });
        Assert.Equal(1, Value(body, "conwatch_engine_enabled"));
        // One-hot verdict: the current state is 1, every other state 0.
        Assert.Equal(0, Value(body, "conwatch_verdict{state=\"LinkDown\"}"));
        Assert.Equal(0, Value(body, "conwatch_target_loss_ratio{name=\"Google\"}"));
        // RTT is exposed in seconds, Prometheus-style.
        Assert.Contains("# TYPE conwatch_target_rtt_seconds gauge", body);
        Assert.True(Value(body, "conwatch_target_rtt_seconds{name=\"Google\"}") < 1.0);
        Assert.True(Value(body, "conwatch_target_sample_size{name=\"Google\"}") > 0);
    }

    [Fact]
    public async Task Transition_counters_exist_at_zero_before_any_transition()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        // Catch the engine in its first seconds: monitors exist (gauges
        // enumerate them immediately) but no subject has transitioned yet —
        // Starting needs MinUsableSample pings before its first transition.
        // An absent counter series makes rate() return "no data" instead of
        // 0, so panels and alerts sit blind; warm restarts (silent seeding)
        // can leave a stable target series-less for days. Every live subject
        // must appear from the very first scrape that knows the engine.
        // Seeding posts the pool last, and each post debounces its own
        // rebuild, so wait for the scrape that enumerates the pool: an
        // earlier one may show the targets from an intermediate rebuild
        // that has no "Internet" subject yet.
        var body = "";
        await ConfigApiTests.Eventually(async () =>
        {
            body = await ScrapeAsync();
            return body.Contains("conwatch_pool_up{name=\"Internet\"}");
        });
        Assert.Contains("conwatch_target_up{name=\"Router\"}", body);
        Assert.Contains("conwatch_transitions_total{name=\"Router\"}", body);
        Assert.Contains("conwatch_transitions_total{name=\"Google\"}", body);
        Assert.Contains("conwatch_transitions_total{name=\"Internet\"}", body);
    }

    [Fact]
    public async Task Downed_target_flips_gauges_and_bumps_the_transition_counter()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () =>
            (await ScrapeAsync()).Contains("conwatch_verdict{state=\"Healthy\"} 1"));
        var baseline = Value(await ScrapeAsync(), "conwatch_transitions_total{name=\"Google\"}");

        _factory.Pings.SetUp(System.Net.IPAddress.Parse("8.8.8.8"), false);
        // Generous timeout: under full-suite load the fake-ping cadence and
        // the verdict recalculation can lag well behind wall clock.
        await ConfigApiTests.Eventually(async () =>
        {
            var body = await ScrapeAsync();
            return body.Contains("conwatch_target_up{name=\"Google\"} 0")
                && body.Contains("conwatch_verdict{state=\"SiteIncident\"} 1");
        }, timeoutMs: 40000);

        var body = await ScrapeAsync();
        Assert.True(Value(body, "conwatch_target_loss_ratio{name=\"Google\"}") > 0);
        Assert.True(Value(body, "conwatch_transitions_total{name=\"Google\"}") > baseline,
            "transition counter should grow when a target goes down");
        Assert.Contains("# TYPE conwatch_transitions_total counter", body);
        Assert.Contains("# TYPE conwatch_glitches_total counter", body);
    }

    [Fact]
    public async Task Local_targets_carry_a_scope_label_and_verdict_targets_do_not()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NAS", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();

        var body = "";
        await ConfigApiTests.Eventually(async () =>
        {
            body = await ScrapeAsync();
            return body.Contains("conwatch_target_up{name=\"NAS\",scope=\"local\"}")
                && body.Contains("conwatch_target_up{name=\"Router\"}");
        });

        Assert.Contains("conwatch_target_sample_size{name=\"NAS\",scope=\"local\"}", body);
        Assert.Contains("conwatch_transitions_total{name=\"NAS\",scope=\"local\"}", body);
        Assert.DoesNotContain("name=\"Router\",scope=", body);
    }
}
