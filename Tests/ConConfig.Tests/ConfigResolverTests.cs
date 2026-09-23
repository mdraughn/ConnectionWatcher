using ConConfig;

namespace ConConfig.Tests;

public sealed class ConfigResolverTests
{
    private static readonly ConfigPaths Paths = ConfigPaths.UnderTemp(@"C:\t");

    [Fact]
    public void Listen_url_precedence_is_flag_env_machine_default()
    {
        var machine = new ConWatchConfig(ListenUrl: "http://0.0.0.0:3");
        Assert.Equal(("http://0.0.0.0:1", ConfigSource.Flag),
            Pair(ConfigResolver.ListenUrl(Paths, machine, flag: "http://0.0.0.0:1", env: "http://0.0.0.0:2")));
        Assert.Equal(("http://0.0.0.0:2", ConfigSource.Environment),
            Pair(ConfigResolver.ListenUrl(Paths, machine, flag: null, env: "http://0.0.0.0:2")));
        var fromMachine = ConfigResolver.ListenUrl(Paths, machine, null, null);
        Assert.Equal(("http://0.0.0.0:3", ConfigSource.Machine), Pair(fromMachine));
        Assert.Equal(Paths.MachineFile, fromMachine.Path);
        Assert.Equal((ConfigResolver.DefaultListenUrl, ConfigSource.Default),
            Pair(ConfigResolver.ListenUrl(Paths, null, null, null)));
    }

    [Fact]
    public void Data_dir_defaults_to_the_paths_default()
    {
        var r = ConfigResolver.DataDir(Paths, null, null, null);
        Assert.Equal(Paths.DefaultDataDir, r.Value);
        Assert.Equal(ConfigSource.Default, r.Source);
        Assert.Equal(@"D:\x", ConfigResolver.DataDir(Paths, new ConWatchConfig(DataDir: @"D:\x"), null, null).Value);
    }

    [Fact]
    public void Server_url_precedence_is_user_machine_server_machine_listen_fallback()
    {
        var user = new ConWatchConfig(ServerUrl: "http://user:1/");
        var machine = new ConWatchConfig(ListenUrl: "http://0.0.0.0:3", ServerUrl: "http://nas:2");
        Assert.Equal(("http://user:1", ConfigSource.User), Pair(ConfigResolver.ServerUrl(Paths, user, machine)));
        Assert.Equal(("http://nas:2", ConfigSource.Machine), Pair(ConfigResolver.ServerUrl(Paths, null, machine)));
        Assert.Equal(("http://localhost:3", ConfigSource.Machine),
            Pair(ConfigResolver.ServerUrl(Paths, null, new ConWatchConfig(ListenUrl: "http://0.0.0.0:3"))));
        Assert.Equal((ConfigResolver.FallbackServerUrl, ConfigSource.Default), Pair(ConfigResolver.ServerUrl(Paths, null, null)));
    }

    [Fact]
    public void Notifications_default_true()
    {
        Assert.True(ConfigResolver.Notifications(null));
        Assert.True(ConfigResolver.Notifications(new ConWatchConfig()));
        Assert.False(ConfigResolver.Notifications(new ConWatchConfig(Notifications: false)));
    }

    [Theory]
    [InlineData("http://0.0.0.0:8080", "http://localhost:8080")]
    [InlineData("http://+:8080/", "http://localhost:8080")]
    [InlineData("http://*:8080", "http://localhost:8080")]
    [InlineData("http://[::]:8080", "http://localhost:8080")]
    [InlineData("https://nas.local:8443/", "https://nas.local:8443")]
    [InlineData("http://192.168.1.5:8080", "http://192.168.1.5:8080")]
    public void Listen_to_server_url_rewrites_wildcards_and_strips_the_slash(string listen, string expected) =>
        Assert.Equal(expected, ConfigResolver.ListenToServerUrl(listen));

    [Theory]
    [InlineData("http://0.0.0.0:8080", 8080)]
    [InlineData("http://+:9090/", 9090)]
    [InlineData("http://[::]:7000", 7000)]
    [InlineData("http://localhost", 80)]
    public void Port_of_reads_the_port(string listen, int port) =>
        Assert.Equal(port, ConfigResolver.PortOf(listen));

    [Fact]
    public void With_port_of_replaces_the_port_but_keeps_the_host()
    {
        Assert.Equal("http://nas:9090", ConfigResolver.WithPortOf("http://nas:8080", "http://0.0.0.0:9090"));
        Assert.Equal("http://localhost:9090", ConfigResolver.WithPortOf("http://localhost:8080", "http://0.0.0.0:9090"));
        Assert.Equal("http://nas:8080", ConfigResolver.WithPortOf("http://nas:8080", "http://0.0.0.0:8080"));
    }

    [Theory]
    [InlineData("http://nas:8080", true)]
    [InlineData("https://nas:8443/", true)]
    [InlineData("nas:8080", false)]
    [InlineData("ftp://nas", false)]
    [InlineData("", false)]
    public void Validate_url_accepts_only_absolute_http_or_https(string url, bool ok)
    {
        Assert.Equal(ok, ConfigResolver.ValidateUrl(url, out var error));
        Assert.Equal(ok, error is null);
    }

    [Fact]
    public void Validate_data_dir_requires_an_absolute_creatable_path()
    {
        var root = Directory.CreateTempSubdirectory("conconfig").FullName;
        try
        {
            Assert.True(ConfigResolver.ValidateDataDir(Path.Combine(root, "new", "deeper"), out var e1));
            Assert.Null(e1);
            Assert.True(Directory.Exists(Path.Combine(root, "new", "deeper")));
            Assert.False(ConfigResolver.ValidateDataDir("relative/dir", out var e2));
            Assert.Contains("absolute", e2);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Validate_data_dir_rejects_rooted_path_with_invalid_characters()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("conconfig").FullName;
        try
        {
            var badPath = Path.Combine(root, "bad<dir");
            Assert.False(ConfigResolver.ValidateDataDir(badPath, out var error));
            Assert.NotNull(error);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static (string, ConfigSource) Pair(Resolved<string> r) => (r.Value, r.Source);
}
