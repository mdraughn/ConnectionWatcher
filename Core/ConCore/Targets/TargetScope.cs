using System.Text.Json.Serialization;

namespace ConCore.Targets;

/// <summary>
/// What a target is for. <see cref="Verdict"/> targets (the default) form the
/// reachability tree and feed the internet verdict. <see cref="Local"/>
/// targets are LAN devices: probed with history, shown in the LAN section,
/// and excluded from the verdict, suspicion, notifications and the ISP report.
/// Serialized as a string everywhere ("Verdict" / "Local").
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TargetScope>))]
public enum TargetScope
{
    Verdict = 0,
    Local = 1,
}
