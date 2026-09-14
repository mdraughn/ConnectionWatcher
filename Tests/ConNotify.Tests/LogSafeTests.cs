using ConNotify;

namespace ConNotify.Tests;

public class LogSafeTests
{
    [Theory]
    [InlineData("https://hc-ping.com/8f3a0c2e-secret-uuid", "https://hc-ping.com/…")]
    [InlineData("https://hooks.example.com:8443/T000/B000/secret", "https://hooks.example.com:8443/…")]
    [InlineData("http://192.168.1.10/hook", "http://192.168.1.10/…")]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("not a url", "(invalid url)")]
    [InlineData(null, "(invalid url)")]
    public void Elides_the_secret_path_keeps_the_host(string? url, string expected)
        => Assert.Equal(expected, LogSafe.Url(url));
}
