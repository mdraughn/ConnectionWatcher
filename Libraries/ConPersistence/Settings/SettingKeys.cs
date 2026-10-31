namespace ConPersistence.Settings;

/// <summary>Well-known setting keys.</summary>
public static class SettingKeys
{
    public const string AnchorPool = "Verdict.AnchorPool";
    public const string WeatherThreshold = "Verdict.WeatherThreshold";
    public const string MinuteRetentionDays = "Retention.MinuteRetentionDays";
    public const string HourlyRetentionMonths = "Retention.HourlyRetentionMonths";
    public const string TransitionRetentionMonths = "Retention.TransitionMonths";
    public const string RetentionRunHourUtc = "Retention.RetentionRunHourUtc";
    public const string RetentionLastRunUtc = "Retention.LastRunUtc";

    public const string MqttEnabled = "Mqtt.Enabled";
    public const string MqttHost = "Mqtt.Host";
    public const string MqttPort = "Mqtt.Port";
    public const string MqttUsername = "Mqtt.Username";
    public const string MqttPassword = "Mqtt.Password";
    public const string MqttBaseTopic = "Mqtt.BaseTopic";
    public const string MqttDiscoveryPrefix = "Mqtt.DiscoveryPrefix";

    public const string WebhookEnabled = "Webhook.Enabled";
    public const string WebhookUrls = "Webhook.Urls";
    public const string WebhookVerdictOnly = "Webhook.VerdictOnly";

    /// <summary>Anything but "false" (including absent): skip target transitions
    /// explained by an upstream root cause, and their recovery.</summary>
    public const string WebhookSkipExplained = "Webhook.SkipExplained";

    public const string HeartbeatUrl = "Heartbeat.Url";
    public const string HeartbeatIntervalMinutes = "Heartbeat.IntervalMinutes";

    /// <summary>"false" turns the name-resolution watch off (spec 2026-10-10). Default "true".</summary>
    public const string DnsEnabled = "Dns.Enabled";
    /// <summary>JSON array of {name, expected[]}: the names the resolvers are asked for.</summary>
    public const string DnsLookupNames = "Dns.LookupNames";
    /// <summary>Newline-separated resolver addresses the system-resolver service
    /// itself disabled (gone from the OS list, or the watch was off), so it may
    /// re-enable them and nothing else. Maintained by the service, not edited.</summary>
    public const string DnsRetiredResolvers = "Dns.RetiredResolvers";

    /// <summary>"true" lets the traceroute page send each public hop's address
    /// to Team Cymru's IP-to-ASN DNS service to name its operator. Off by default.</summary>
    public const string TracerouteAsnLookup = "Traceroute.AsnLookup";

    /// <summary>Warm restart: seed presumed states from the DB when the gap
    /// since the watcher was last alive is at most this many minutes; 0 or
    /// less disables all seeding (process start AND config rebuilds).</summary>
    public const string WarmRestartMaxGapMinutes = "Engine.WarmRestartMaxGapMinutes";

    /// <summary>Master enable: "false" parks the engine (no pings, rollups,
    /// verdicts, or notifications) while the web UI and APIs keep serving as
    /// a read-only archive. Anything else (including absent) means enabled.</summary>
    public const string EngineEnabled = "Engine.Enabled";

    /// <summary>Set by the clone wizard: the node this one was cloned from.
    /// Drives the take-over card and the both-active health warning.</summary>
    public const string CloneSourceUrl = "Clone.SourceUrl";

    /// <summary>Set by the clone wizard: "false" when the clone left out Local devices, so a
    /// later take-over leaves them out too. Absent or anything else means keep them.</summary>
    public const string CloneIncludeLocalDevices = "Clone.IncludeLocalDevices";

    /// <summary>Never leaves the machine via the snapshot export: credentials
    /// and capability URLs (a webhook or heartbeat URL grants whoever holds it
    /// the power to impersonate or silence this instance).</summary>
    public static readonly string[] SecretKeys = [MqttPassword, WebhookUrls, HeartbeatUrl];

    /// <summary>Node identity, not configuration: excluded from snapshot
    /// export AND preserved locally across snapshot import — a take-over's
    /// final sync from a just-disabled source must not import Enabled=false.</summary>
    public static readonly string[] PerNodeKeys = [EngineEnabled, CloneSourceUrl, CloneIncludeLocalDevices, DnsRetiredResolvers];
}
