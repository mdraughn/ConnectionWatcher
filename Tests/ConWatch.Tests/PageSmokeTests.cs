using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ConEngine.Topology;
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Report;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class PageSmokeTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/", "Quick setup")] // empty config redirects to the wizard
    [InlineData("/History", "history-root")]
    [InlineData("/Config/Targets", "Targets")]
    [InlineData("/Config/Profiles", "Profiles")]
    [InlineData("/Config/Profiles", "Be polite")]           // built-in description in the table
    [InlineData("/Config/Targets", "option selected=\"selected\" title=\"Public internet sites")] // the Public default's title
    [InlineData("/Config/Pools", "Pools")]
    [InlineData("/Config/Pools", "Anchor pool")]
    [InlineData("/Config/Pools", "Degraded operators that mean internet weather")]
    [InlineData("/Traceroute", "Traceroute")]
    [InlineData("/Settings", "Danger zone")]
    [InlineData("/Settings", "Look up the operator of each hop")]
    [InlineData("/Settings", "MQTT / Home Assistant")]
    [InlineData("/Settings", "Webhook URLs")]
    [InlineData("/Settings", "Heartbeat (watch the watchman)")]
    [InlineData("/Settings", "<h2>Settings</h2>")]
    [InlineData("/Data", "db-stats")]
    [InlineData("/Data", "Retention")]
    [InlineData("/Data", "Snapshot JSON")]
    [InlineData("/Setup", "Quick setup")]
    [InlineData("/Setup", "internet provider")]
    [InlineData("/Config/Targets", "ISP / operator")]
    [InlineData("/Report", "Internet connection report")]
    public async Task Page_renders(string url, string marker)
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(marker, html);
    }

    [Fact]
    public async Task Pools_page_badges_the_anchor_pool()
    {
        var client = _factory.CreateClient();
        await ConfigApiTests.SeedHomeAsync(client);          // creates pool "Internet"; AnchorPool is "Internet" from appsettings
        var html = await client.GetStringAsync("/Config/Pools");
        Assert.Contains("cw-chip\">anchor</span>", html);
        await client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Verdict.AnchorPool"] = null });
        html = await client.GetStringAsync("/Config/Pools");
        Assert.DoesNotContain("cw-chip\">anchor</span>", html);
    }

    [Fact]
    public async Task Pools_page_does_not_offer_Local_targets_as_parent_or_member()
    {
        var client = _factory.CreateClient();
        await ConfigApiTests.SeedHomeAsync(client);
        (await client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NasBox", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();
        var html = await client.GetStringAsync("/Config/Pools");
        // Only the select regions: the name may legitimately appear elsewhere.
        var selects = string.Concat(Regex.Matches(html, @"<select\b.*?</select>", RegexOptions.Singleline)
            .Select(m => m.Value));
        Assert.Contains("<option>Router</option>", selects);
        Assert.DoesNotContain("NasBox", selects);
    }

    [Fact]
    public async Task Old_admin_url_redirects_permanently_to_settings()
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });
        var response = await client.GetAsync("/Admin");
        Assert.Equal(System.Net.HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/Settings", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Profiles_page_offers_delete_only_on_custom_profiles()
    {
        var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/Config/Profiles");
        Assert.DoesNotContain("data-delete=", html); // only the three built-ins exist

        (await client.PostAsJsonAsync("/api/v1/profiles", new ProfileDto { Name = "Custom" })).EnsureSuccessStatusCode();
        html = await client.GetStringAsync("/Config/Profiles");
        Assert.Single(Regex.Matches(html, "data-delete="));
        Assert.Contains("data-name=\"Custom\"", html);
    }

    [Fact]
    public async Task Targets_page_renders_pipeline_for_anchor_pool()
    {
        await ConfigApiTests.SeedHomeAsync(_factory.CreateClient());
        var html = await _factory.CreateClient().GetStringAsync("/Config/Targets");
        Assert.Contains("Path to the internet", html);
        Assert.Contains("data-insert-children", html);
        Assert.Contains("Internet anchors", html);
    }

    [Fact]
    public async Task Probe_test_result_clears_the_negative_top_margin_the_stylesheet_gives_small_text()
    {
        // Pico pulls <small> up 12px to tuck it under an input; after the Address button group that
        // drew the Test result over the field. The element must override it.
        var html = await _factory.CreateClient().GetStringAsync("/Config/Targets");
        Assert.Matches(@"<small id=""probe-test-out""[^>]*style=""[^""]*margin-top:\s*\.?\d", html);
    }

    [Fact]
    public async Task Targets_pipeline_lists_parentless_anchors_and_parents_written_in_another_case()
    {
        var client = _factory.CreateClient();
        var profile = new ProfileDto
        {
            Name = "Fast", NormalIntervalSeconds = 1, BurstIntervalSeconds = 0.5,
            BurstMaxDurationSeconds = 5, HistorySize = 20, MinUsableSample = 5,
        };
        (await client.PostAsJsonAsync("/api/v1/profiles", profile)).EnsureSuccessStatusCode();
        foreach (var target in new TargetDto[]
        {
            new() { Name = "Router", Address = "192.168.1.1", Profile = "Fast" },
            new() { Name = "Google", Address = "8.8.8.8", Parent = "router", Profile = "Fast" },
            new() { Name = "Quad9", Address = "9.9.9.9", Profile = "Fast" },
        })
            (await client.PostAsJsonAsync("/api/v1/targets", target)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/pools", new PoolDto { Name = "Internet", Members = ["Google", "Quad9"] }))
            .EnsureSuccessStatusCode();

        var html = await client.GetStringAsync("/Config/Targets");
        var start = html.IndexOf("Path to the internet", StringComparison.Ordinal);
        Assert.True(start >= 0, "no pipeline section");
        var pipeline = html[start..html.IndexOf("<table", start, StringComparison.Ordinal)];
        Assert.Contains("Google", pipeline); // parent "router" still finds Router's chain
        Assert.Contains("Quad9", pipeline);  // no parent: reached directly
    }

    [Fact]
    public async Task Config_page_shows_health_banner_for_thin_anchor_pool()
    {
        // SeedHome makes a 2-member pool with threshold 2 -> below-recommended fires.
        await ConfigApiTests.SeedHomeAsync(_factory.CreateClient());
        var html = await _factory.CreateClient().GetStringAsync("/Config/Targets");
        Assert.Contains("cw-health", html);
        Assert.Contains("anchors-below-recommended", html);
    }

    [Fact]
    public async Task Targets_pipeline_draws_one_chain_per_anchor_parent()
    {
        var client = _factory.CreateClient();
        foreach (var target in new TargetDto[]
        {
            new() { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
            new() { Name = "WanA", Address = "192.168.100.1", Parent = "Router", Profile = "Internal" },
            new() { Name = "WanB", Address = "192.168.200.1", Parent = "Router", Profile = "Internal" },
            new() { Name = "AnchorA", Address = "8.8.8.8", Parent = "WanA" },
            new() { Name = "AnchorB", Address = "1.1.1.1", Parent = "WanB" },
        })
            (await client.PostAsJsonAsync("/api/v1/targets", target)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/pools",
            new PoolDto { Name = "Internet", Members = ["AnchorA", "AnchorB"] })).EnsureSuccessStatusCode();

        var html = await client.GetStringAsync("/Config/Targets");

        Assert.Equal(2, Regex.Matches(html, "class=\"cw-pipeline\"").Count);
        Assert.Contains("<b>WanA</b>", html);
        Assert.Contains("<b>WanB</b>", html);
    }

    /// <summary>Both OPEN pages embed config-derived names in a JSON island via Html.Raw;
    /// only the serializer's escaping keeps a name from closing the script element.</summary>
    [Theory]
    [InlineData("/", "initial-data")]
    [InlineData("/History", "history-data")]
    public async Task A_hostile_target_name_cannot_break_out_of_a_script_island(string url, string islandId)
    {
        const string hostile = "x</script><script>alert(1)</script>";
        var client = _factory.CreateClient();
        await ConfigApiTests.SeedHomeAsync(client);
        (await client.PostAsJsonAsync("/api/v1/targets", new { name = hostile, address = "192.0.2.77", profile = "Fast" }))
            .EnsureSuccessStatusCode();
        // The dashboard renders from the running engine: wait for the rebuilt graph to carry the name.
        var host = _factory.Services.GetRequiredService<WatcherHostService>();
        await HostTestWait.Eventually(() => Task.FromResult(
            host.Engine?.Monitors.Values.Any(m => m.Target.Name == hostile) == true));

        var html = await client.GetStringAsync(url);
        var body = HistoryPageTests.Island(html, islandId);   // cut where a browser would end the element

        Assert.DoesNotContain("<!--", body);                   // no switch into the script-data-escaped state
        using var doc = JsonDocument.Parse(body);              // a breakout leaves a truncated, unparsable body
        Assert.Contains(hostile, Strings(doc.RootElement));    // and the name survives intact
        Assert.DoesNotContain("<script>alert(1)", html);       // nowhere else on the page either
    }

    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => [e.GetString()!],
        JsonValueKind.Array => e.EnumerateArray().SelectMany(Strings),
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Strings(p.Value)),
        _ => [],
    };

    [Fact]
    public async Task Dashboard_embeds_initial_data_blob()
    {
        await ConfigApiTests.SeedHomeAsync(_factory.CreateClient());
        await ConfigApiTests.Eventually(async () =>
        {
            var html = await _factory.CreateClient().GetStringAsync("/");
            return html.Contains("initial-data") && html.Contains("Router");
        });
    }

    [Fact]
    public async Task Targets_dialog_has_a_critical_checkbox_unchecked_by_default()
    {
        var html = await _factory.CreateClient().GetStringAsync("/Config/Targets");
        Assert.Contains("Critical — notify when this is down, even if the internet is fine", html);
        Assert.Matches("<input[^>]*name=\"critical\"", html);
        Assert.DoesNotMatch("<input[^>]*name=\"critical\"[^>]*checked", html);
    }

    [Fact]
    public async Task Critical_prefill_checks_the_box()
    {
        var html = await _factory.CreateClient().GetStringAsync("/Config/Targets?address=192.0.2.1&name=Router&critical=true");
        Assert.Matches("<input[^>]*name=\"critical\"[^>]*checked", html);
    }

    [Fact]
    public async Task Targets_page_prefills_profile_and_operator_from_the_query()
    {
        var html = await _factory.CreateClient().GetStringAsync(
            "/Config/Targets?address=96.120.0.1&name=hop-3&profile=Path&operator=COMCAST-7922");
        Assert.Contains("value=\"COMCAST-7922\"", html);
        Assert.Matches("<option selected=\"selected\"[^>]*>Path</option>", html);
        Assert.DoesNotMatch("<option selected=\"selected\"[^>]*>Public</option>", html);
    }

    [Fact]
    public async Task Unknown_prefilled_profile_falls_back_to_Public()
    {
        var html = await _factory.CreateClient().GetStringAsync("/Config/Targets?address=1.2.3.4&profile=Nope");
        Assert.Matches("<option selected=\"selected\"[^>]*>Public</option>", html);
    }

    [Fact]
    public async Task Targets_dialog_has_a_scope_select_and_the_table_a_scope_column()
    {
        var html = await _factory.CreateClient().GetStringAsync("/Config/Targets");
        Assert.Contains("name=\"scope\"", html);
        Assert.Contains("<th>Scope</th>", html);
        Assert.Contains("<option value=\"Verdict\"", html);
        Assert.Contains("<option value=\"Local\"", html);
    }

    [Fact]
    public async Task Scope_prefill_selects_Local_and_defaults_the_profile_to_Local()
    {
        var html = await _factory.CreateClient()
            .GetStringAsync("/Config/Targets?address=203.0.113.9&name=Cam&scope=Local");
        Assert.Contains("<option value=\"Local\" selected=\"selected\"", html);
        Assert.Contains("<option selected=\"selected\" title=\"LAN devices tracked on the side", html);
    }

    [Fact]
    public async Task The_parent_dropdown_never_offers_a_Local_target()
    {
        var client = _factory.CreateClient();
        await ConfigApiTests.SeedHomeAsync(client);
        (await client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NasBox", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();

        var html = await client.GetStringAsync("/Config/Targets");
        var parent = System.Text.RegularExpressions.Regex.Match(html,
            "<select name=\"parent\">(.*?)</select>", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(parent.Success);
        Assert.Contains("Router", parent.Groups[1].Value);
        Assert.DoesNotContain("NasBox", parent.Groups[1].Value);
    }

    [Fact]
    public async Task The_report_renders_its_incident_table()
    {
        // A window in the recent past: the live engine's own rows (at "now") stay outside it.
        var hour = DateTime.UtcNow.AddHours(-5);
        hour = new DateTime(hour.Year, hour.Month, hour.Day, hour.Hour, 0, 0, DateTimeKind.Utc);
        var from = hour;
        var to = hour.AddHours(4);
        var downAt = hour.AddHours(1);
        var upAt = hour.AddHours(2);
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Transitions.AddRange(
                VerdictRow(from.AddMinutes(-1), "Healthy", null),
                VerdictRow(downAt, "LinkDown", "LinkDown: Modem is Down"),
                VerdictRow(upAt, "Healthy", null));
            db.Glitches.AddRange(
                new GlitchRow { TargetId = VerdictCoordinator.SubjectId, TargetName = "Internet", Class = 1,
                    StartUtc = hour.AddMinutes(30), EndUtc = hour.AddMinutes(30).AddSeconds(8), Detail = "anchors: A, B" },
                new GlitchRow { TargetId = VerdictCoordinator.SubjectId, TargetName = "Internet", Class = 1,
                    StartUtc = hour.AddMinutes(200), EndUtc = hour.AddMinutes(200).AddSeconds(5), Detail = "anchors: A, B" });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var html = await client.GetStringAsync($"/Report?from={from:yyyy-MM-ddTHH:mm:ssZ}&to={to:yyyy-MM-ddTHH:mm:ssZ}");

        // Exactly one incident row, labelled for a person, carrying the pair's own times.
        Assert.Single(Regex.Matches(html, "<td>Line down</td>"));
        Assert.DoesNotContain("<td>Widespread</td>", html);
        Assert.Contains("Modem is Down", html);
        Assert.Contains($"datetime=\"{new DateTimeOffset(downAt):O}\"", html);
        Assert.Contains($"datetime=\"{new DateTimeOffset(upAt):O}\"", html);
        Assert.Contains("+00:00\"", html);
        Assert.Contains("<td>60 minutes</td>", html);                 // the duration of the pair
        // Glitches: the count sentence, with how many were corroborated.
        Assert.Matches(@"<b>2</b> brief loss episodes\s+too short to register as outages\s+\(2 of them corroborated", html);

        // The data the page was built from is UTC-kinded all the way (a local-kinded DateTime
        // would print a local offset instead of +00:00 on any machine not on UTC).
        var report = await _factory.Services.GetRequiredService<IspReportBuilder>().BuildAsync(from, to);
        var episode = Assert.Single(report.Episodes);
        Assert.Equal("LinkDown", episode.Kind);
        Assert.Equal(DateTimeKind.Utc, episode.StartUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, episode.EndUtc.Kind);
        Assert.Equal(downAt, episode.StartUtc);
        Assert.Equal(upAt, episode.EndUtc);
        Assert.Equal(DateTimeKind.Utc, report.FromUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, report.ToUtc.Kind);
    }

    private static TransitionRow VerdictRow(DateTime at, string verdict, string? detail) => new()
    {
        TimestampUtc = at, SubjectId = VerdictCoordinator.SubjectId, SubjectName = "Internet",
        SubjectKind = "Verdict", FromState = "Responding", ToState = "Responding",
        Trigger = "VerdictChanged", Verdict = verdict, Detail = detail,
    };
}
