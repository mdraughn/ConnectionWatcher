namespace ConPersistence.Settings;

/// <summary>
/// What each setting means, kept in one place. For boolean flags that is the
/// default: a flag is on or off when the row is absent, and only one exact,
/// case-sensitive spelling flips it.
/// </summary>
public static class SettingRules
{
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
