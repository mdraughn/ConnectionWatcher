using System.Text.Json.Serialization;

namespace ConCore.Discovery;

/// <summary>What a discovery is. Serialized as a string everywhere.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscoveryKind>))]
public enum DiscoveryKind
{
    /// <summary>The user's router (default gateway).</summary>
    Gateway,
    /// <summary>The first hop beyond the user's LAN: where the ISP's network begins.</summary>
    NetworkEdge,
    /// <summary>A device on the LAN (camera, NAS, access point, IoT).</summary>
    LocalDevice,
}

/// <summary>How a discovery was found. Extensible; the kind says what, the method says how.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscoveryMethod>))]
public enum DiscoveryMethod
{
    DefaultRoute,
    CommonAddress,
    Traceroute,
    ArpTable,
    PingSweep,
}

[JsonConverter(typeof(JsonStringEnumConverter<DiscoveryStatus>))]
public enum DiscoveryStatus
{
    New,
    Adopted,
    Dismissed,
}
