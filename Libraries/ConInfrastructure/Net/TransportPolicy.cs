using System.Net;

namespace ConInfrastructure.Net;

/// <summary>
/// Transport policy for cross-node calls that carry sensitive material (the
/// source admin password out, secrets back). Enforced by the REQUESTING node
/// because it is the only party that knows the transport end to end — the
/// source may sit behind a TLS-terminating proxy and cannot tell, and by the
/// time it could check anything the password has already crossed the wire.
/// Spec: docs/superpowers/specs/2026-09-12-clone-takeover-design.md.
/// </summary>
public static class TransportPolicy
{
    public enum Verdict
    {
        Allowed,
        /// <summary>Plain HTTP to a non-loopback host with sensitive material:
        /// proceed only on the user's explicit override.</summary>
        RequiresOverride,
    }

    public static Verdict Evaluate(Uri source, bool sensitive, bool allowInsecure)
    {
        if (source.Scheme == Uri.UriSchemeHttps)
            return Verdict.Allowed;
        if (IsLoopback(source))
            return Verdict.Allowed; // nothing crosses a wire
        if (!sensitive)
            return Verdict.Allowed; // the sanitized snapshot is public anyway
        return allowInsecure ? Verdict.Allowed : Verdict.RequiresOverride;
    }

    private static bool IsLoopback(Uri url) =>
        url.IsLoopback ||
        (IPAddress.TryParse(url.Host, out var ip) && IPAddress.IsLoopback(ip));
}
