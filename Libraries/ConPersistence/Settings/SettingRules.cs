using System.Globalization;

namespace ConPersistence.Settings;

/// <summary>An integer setting's allowed range and the value used when its row is missing.</summary>
public sealed record IntRule(string Key, int Min, int Max, int Default);

/// <summary>
/// What each setting means, kept in one place. For boolean flags that is the
/// default: a flag is on or off when the row is absent, and only one exact,
/// case-sensitive spelling flips it.
/// </summary>
public static class SettingRules
{
    public const string MqttBaseTopicDefault = "conwatch";
    public const string MqttDiscoveryPrefixDefault = "homeassistant";

    private static readonly PersistenceOptions Persistence = new();

    private static readonly IReadOnlyDictionary<string, IntRule> IntRules = new[]
    {
        new IntRule(SettingKeys.WeatherThreshold, 1, int.MaxValue, SettingsDefaults.WeatherThreshold),
        new IntRule(SettingKeys.MinuteRetentionDays, 1, 3650, Persistence.MinuteRetentionDays),
        new IntRule(SettingKeys.HourlyRetentionMonths, 1, 120, Persistence.HourlyRetentionMonths),
        new IntRule(SettingKeys.TransitionRetentionMonths, 1, 120, Persistence.TransitionRetentionMonths),
        new IntRule(SettingKeys.RetentionRunHourUtc, 0, 23, Persistence.RetentionRunHourUtc),
        new IntRule(SettingKeys.MqttPort, 1, 65535, 1883),
        new IntRule(SettingKeys.HeartbeatIntervalMinutes, 1, 1440, 5),
    }.ToDictionary(r => r.Key, StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, IntRule> Ints => IntRules;

    /// <summary>The rule for an integer setting; throws <see cref="KeyNotFoundException"/> for any other key.</summary>
    public static IntRule Int(string key) => IntRules[key];

    /// <summary>Null when <paramref name="value"/> is a valid integer for the key; otherwise the message.</summary>
    public static string? IntError(string key, string? value)
    {
        var rule = Int(key);
        if (int.TryParse(value, out var n) && n >= rule.Min && n <= rule.Max)
            return null;
        return rule.Max == int.MaxValue
            ? string.Create(CultureInfo.InvariantCulture, $"{key} must be an integer ≥ {rule.Min}.")
            : string.Create(CultureInfo.InvariantCulture, $"{key} must be {rule.Min}–{rule.Max}.");
    }

    /// <summary>The stored integer for the key, or <paramref name="fallback"/> when absent or unparseable.</summary>
    public static int IntOr(IReadOnlyDictionary<string, string> all, string key, int fallback) =>
        all.TryGetValue(key, out var text) && int.TryParse(text, out var value) ? value : fallback;

    /// <summary>An absolute URL with an http or https scheme.</summary>
    public static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>The non-blank, trimmed lines of a newline-separated URL list.</summary>
    public static IReadOnlyList<string> SplitUrlList(string? value) =>
        (value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>On unless the value is exactly "false": turning these off is the deliberate act
    /// (park the engine, stop DNS watching, keep explained transitions, drop Local devices).</summary>
    private static readonly HashSet<string> DefaultOn = new(StringComparer.Ordinal)
    {
        SettingKeys.EngineEnabled,
        SettingKeys.DnsEnabled,
        SettingKeys.WebhookSkipExplained,
        SettingKeys.CloneIncludeLocalDevices,
    };

    /// <summary>Off unless the value is exactly "true": each reaches outside the machine or
    /// changes what is sent, so it needs an explicit opt-in.</summary>
    private static readonly HashSet<string> DefaultOff = new(StringComparer.Ordinal)
    {
        SettingKeys.MqttEnabled,
        SettingKeys.WebhookEnabled,
        SettingKeys.WebhookVerdictOnly,
        SettingKeys.TracerouteAsnLookup,
    };

    public static bool IsFlag(string key) => DefaultOn.Contains(key) || DefaultOff.Contains(key);

    /// <summary>Reads a flag's stored value (null = absent) using the key's own default.</summary>
    public static bool ReadFlag(string key, string? raw)
    {
        if (DefaultOn.Contains(key))
            return !string.Equals(raw, "false", StringComparison.Ordinal);
        if (DefaultOff.Contains(key))
            return string.Equals(raw, "true", StringComparison.Ordinal);
        throw new ArgumentException($"'{key}' is not a boolean setting flag.", nameof(key));
    }
}
