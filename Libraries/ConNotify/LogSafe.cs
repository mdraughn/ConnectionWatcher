namespace ConNotify;

/// <summary>
/// Log-safe rendering of capability URLs (webhook and heartbeat endpoints):
/// the PATH is the secret — whoever holds it can impersonate or silence this
/// node — and logs are exactly what people paste into forum posts and bug
/// reports. Keep scheme + host for diagnosability, elide the rest.
/// </summary>
public static class LogSafe
{
    public static string Url(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return "(invalid url)";
        var port = parsed.IsDefaultPort ? "" : $":{parsed.Port}";
        var path = parsed.AbsolutePath is { Length: > 1 } ? "/…" : "";
        return $"{parsed.Scheme}://{parsed.Host}{port}{path}";
    }
}
