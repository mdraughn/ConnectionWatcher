using ConCore.Targets;

namespace ConPersistence.Config;

/// <summary>
/// The engine's built-in presets paired with the descriptions the UI shows.
/// The engine never displays a description, so the text lives here, beside
/// the seeding code, rather than on <see cref="PingProfile"/>.
/// </summary>
public static class BuiltInProfiles
{
    public sealed record Entry(PingProfile Profile, string Description)
    {
        public ProfileDto ToDto() => Profile.ToDto() with { Description = Description };
    }

    public static readonly IReadOnlyList<Entry> All =
    [
        new(PingProfile.Internal, "Devices on your own LAN. Probe as hard as you like."),
        new(PingProfile.Path, "Hops on your route to the internet that you don't own but may " +
                              "probe steadily, such as the ISP gateway."),
        new(PingProfile.Public, "Public internet sites. Be polite."),
    ];
}
