using ConEngine.Configuration;
using Microsoft.Extensions.Options;

namespace ConEngine.Tests;

public class WatcherOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(WatcherOptions options) =>
        new WatcherOptionsValidator().Validate(null, options);

    private static WatcherOptions ValidHome() => new()
    {
        Targets =
        [
            new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
            new TargetConfig { Name = "Modem", Address = "192.168.100.1", Parent = "Router", Profile = "Internal" },
            new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Modem" },
            new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Modem" },
        ],
        Pools =
        [
            new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"], Parent = "Modem" },
        ],
    };

    [Fact]
    public void Valid_config_passes()
    {
        Assert.True(Validate(ValidHome()).Succeeded);
    }

    [Fact]
    public void Duplicate_target_name_fails()
    {
        var o = ValidHome();
        o.Targets.Add(new TargetConfig { Name = "router", Address = "192.168.1.2" });
        var result = Validate(o);
        Assert.True(result.Failed);
        Assert.Contains("duplicate", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Same_address_with_different_probes_is_allowed()
    {
        // Each target gets its own controller; sharing an address is the user's business.
        var o = ValidHome();
        o.Targets.Add(new TargetConfig { Name = "RouterHttps", Address = "192.168.1.1", Probe = "tcp:443" });
        Assert.True(Validate(o).Succeeded);
    }

    [Fact]
    public void Same_address_and_probe_twice_fails()
    {
        var o = ValidHome();
        o.Targets.Add(new TargetConfig { Name = "RouterAgain", Address = "192.168.1.1", Probe = "ICMP" });
        var result = Validate(o);
        Assert.True(result.Failed);
        Assert.Contains("Duplicate target address", result.FailureMessage);
    }

    [Fact]
    public void Unknown_profile_fails()
    {
        var o = ValidHome();
        o.Targets[0].Profile = "NoSuchProfile";
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void Custom_profile_is_accepted()
    {
        var o = ValidHome();
        o.Profiles["Slow"] = new PingProfileConfig { NormalIntervalSeconds = 30 };
        o.Targets[0].Profile = "Slow";
        Assert.True(Validate(o).Succeeded);
    }

    [Fact]
    public void Unknown_parent_fails()
    {
        var o = ValidHome();
        o.Targets[2].Parent = "Ghost";
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void Pool_as_parent_fails()
    {
        var o = ValidHome();
        o.Targets[2].Parent = "Internet";
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void Parent_cycle_fails()
    {
        var o = new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "A", Address = "10.0.0.1", Parent = "B" },
                new TargetConfig { Name = "B", Address = "10.0.0.2", Parent = "A" },
            ],
        };
        var result = Validate(o);
        Assert.True(result.Failed);
        Assert.Contains("cycle", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_pool_fails()
    {
        var o = ValidHome();
        o.Pools.Add(new PoolConfig { Name = "Empty", Members = [] });
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void Pool_member_referencing_pool_fails()
    {
        var o = ValidHome();
        o.Pools.Add(new PoolConfig { Name = "Meta", Members = ["Internet"] });
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void Bad_thresholds_fail()
    {
        var o = ValidHome();
        o.Profiles["Bad"] = new PingProfileConfig { RespondingThreshold = 1.5 };
        o.Targets[0].Profile = "Bad";
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void MinUsableSample_exceeding_history_fails()
    {
        var o = ValidHome();
        o.Profiles["Bad"] = new PingProfileConfig { HistorySize = 5, MinUsableSample = 10 };
        o.Targets[0].Profile = "Bad";
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void Invalid_probe_string_fails_validation()
    {
        var o = ValidHome();
        o.Targets[0].Probe = "tcp:notaport";
        var result = Validate(o);
        Assert.True(result.Failed);
        Assert.Contains("Probe", result.FailureMessage);
    }

    [Fact]
    public void Impolite_public_port_fails_validation()
    {
        var o = ValidHome();
        o.Targets[0].Address = "9.9.9.10";
        o.Targets[0].Probe = "tcp:8080";
        var result = Validate(o);
        Assert.True(result.Failed);
        Assert.Contains("public addresses", result.FailureMessage);
    }

    [Fact]
    public void Private_address_any_port_passes()
    {
        var o = ValidHome();
        o.Targets[0].Address = "10.1.10.1";
        o.Targets[0].Probe = "tcp:8443";
        Assert.False(Validate(o).Failed);
    }
}
