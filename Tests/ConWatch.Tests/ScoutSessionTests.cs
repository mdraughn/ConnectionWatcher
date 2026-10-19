using System.Net.Http.Json;
using System.Text.Json;
using ConCore.Discovery;
using ConInfrastructure.Discovery;
using ConInfrastructure.Tray;

namespace ConWatch.Tests;

/// <summary>
/// The tray's scout send loop (prompt budget, save-after-accept, plain-HTTP consent, saved-password
/// binding) against a locked in-memory server, as ScoutClientTests does for the client itself.
/// </summary>
public class ScoutSessionTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly ScoutItem[] OneDevice =
    [
        new("192.0.2.20", null, DiscoveryKind.LocalDevice, DiscoveryMethod.ArpTable, "tray:TESTPC", null),
    ];

    private static readonly Uri Local = new("http://localhost/");

    /// <summary>Stands in for the tray's DPAPI store: one saved password, bound to its server by the shared rule.</summary>
    private sealed class MemoryStore : IScoutPasswordStore
    {
        public (string Server, string Password)? Saved { get; private set; }
        public int Saves { get; private set; }

        public string? Load(Uri server) =>
            Saved is { } s && ScoutServerKey.Matches(s.Server, server) ? s.Password : null;

        public void Save(Uri server, string password)
        {
            Saved = (ScoutServerKey.Of(server), password);
            Saves++;
        }

        public void Forget() => Saved = null;
    }

    /// <summary>A prompt that gives the scripted answers in turn, then cancels; it records who was asked.</summary>
    private sealed class Prompts(params ScoutPasswordAnswer?[] answers)
    {
        public List<(Uri Server, bool Rejected)> Asked { get; } = [];

        public ScoutPasswordAnswer? Ask(Uri server, bool rejected)
        {
            Asked.Add((server, rejected));
            return Asked.Count <= answers.Length ? answers[Asked.Count - 1] : null;
        }
    }

    private async Task<HttpClient> LockAsync()
    {
        var admin = _factory.CreateClient();
        (await admin.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();
        return admin;
    }

    private async Task<int> StoredAsync(HttpClient admin) =>
        JsonSerializer.Deserialize<JsonElement>(await admin.GetStringAsync("/api/v1/discoveries?status=all")).GetArrayLength();

    // A fresh cookie jar each time, so every send has to sign in again.
    private ScoutClient Client(Uri server) => new(_factory.CreateClient(), server);

    [Fact]
    public async Task A_wrong_password_three_times_stops_and_saves_nothing()
    {
        var admin = await LockAsync();
        var store = new MemoryStore();
        var wrong = new ScoutPasswordAnswer("wrong", Remember: true);
        // Far more wrong answers than the budget: the cap, not the script, has to end the loop.
        var prompts = new Prompts(Enumerable.Repeat<ScoutPasswordAnswer?>(wrong, 10).ToArray());
        var session = new ScoutSession(store, prompts.Ask, _ => true);

        var outcome = await session.SendAsync(Client(Local), OneDevice, CancellationToken.None);

        Assert.Equal(ScoutOutcomeKind.PasswordNotAccepted, outcome.Kind);
        Assert.Equal(ScoutSession.MaxPrompts, prompts.Asked.Count);
        Assert.Equal(3, prompts.Asked.Count);
        Assert.False(prompts.Asked[0].Rejected);                     // the first ask: the server needs a password
        Assert.All(prompts.Asked.Skip(1), a => Assert.True(a.Rejected));
        Assert.Equal(0, store.Saves);
        Assert.Equal(0, await StoredAsync(admin));
    }

    [Fact]
    public async Task A_password_is_saved_only_after_the_server_accepts_it()
    {
        var admin = await LockAsync();
        var store = new MemoryStore();

        // Remember is ticked, but the server says no and the user then cancels: nothing is saved.
        var rejected = new ScoutSession(store, new Prompts(new ScoutPasswordAnswer("wrong", Remember: true)).Ask, _ => true);
        var cancelled = await rejected.SendAsync(Client(Local), OneDevice, CancellationToken.None);
        Assert.Equal(ScoutOutcomeKind.Cancelled, cancelled.Kind);
        Assert.Equal(0, store.Saves);

        // Accepted but Remember not ticked: nothing is saved.
        var unticked = new ScoutSession(store, new Prompts(new ScoutPasswordAnswer("hunter2", Remember: false)).Ask, _ => true);
        Assert.Equal(ScoutOutcomeKind.Sent, (await unticked.SendAsync(Client(Local), OneDevice, CancellationToken.None)).Kind);
        Assert.Equal(0, store.Saves);

        // A wrong try, then the right one, both with Remember: only the accepted one is saved, once.
        var prompts = new Prompts(new ScoutPasswordAnswer("wrong", Remember: true), new ScoutPasswordAnswer("hunter2", Remember: true));
        var session = new ScoutSession(store, prompts.Ask, _ => true);
        var sent = await session.SendAsync(Client(Local), OneDevice, CancellationToken.None);
        Assert.Equal(ScoutOutcomeKind.Sent, sent.Kind);
        Assert.Equal(1, store.Saves);
        Assert.Equal(("http://localhost", "hunter2"), store.Saved!.Value);

        // The saved password is used next time without asking.
        var again = await session.SendAsync(Client(Local), OneDevice, CancellationToken.None);
        Assert.Equal(ScoutOutcomeKind.Sent, again.Kind);
        Assert.Equal(2, prompts.Asked.Count);
        Assert.Equal(1, await StoredAsync(admin));
    }

    [Fact]
    public async Task Insecure_consent_is_asked_once_per_server_and_forgotten_on_Forget()
    {
        await LockAsync();
        var first = new Uri("http://scout-test.example.com/");      // plain http, not loopback
        var second = new Uri("http://scout-other.example.com/");
        var asked = new List<Uri>();
        var sayYes = false;
        var session = new ScoutSession(new MemoryStore(),
            (_, _) => new ScoutPasswordAnswer("hunter2", Remember: false),
            server => { asked.Add(server); return sayYes; });

        // A "no" is not remembered: it ends this send and is asked again next time.
        var declined = await session.SendAsync(Client(first), OneDevice, CancellationToken.None);
        Assert.Equal(ScoutOutcomeKind.Cancelled, declined.Kind);
        sayYes = true;
        Assert.Equal(ScoutOutcomeKind.Sent, (await session.SendAsync(Client(first), OneDevice, CancellationToken.None)).Kind);
        Assert.Equal(new[] { first, first }, asked);

        // Yes is remembered for that server for the session.
        Assert.Equal(ScoutOutcomeKind.Sent, (await session.SendAsync(Client(first), OneDevice, CancellationToken.None)).Kind);
        Assert.Equal(2, asked.Count);

        // Another server is asked for itself.
        Assert.Equal(ScoutOutcomeKind.Sent, (await session.SendAsync(Client(second), OneDevice, CancellationToken.None)).Kind);
        Assert.Equal(new[] { first, first, second }, asked);

        // "Forget saved password" forgets the consent too.
        session.ForgetConsent();
        Assert.Equal(ScoutOutcomeKind.Sent, (await session.SendAsync(Client(second), OneDevice, CancellationToken.None)).Kind);
        Assert.Equal(new[] { first, first, second, second }, asked);
    }

    [Fact]
    public async Task A_saved_password_is_offered_only_for_the_same_server()
    {
        var admin = await LockAsync();
        var store = new MemoryStore();
        store.Save(new Uri("HTTPS://Scout-Test.example.com"), "hunter2");   // saved as typed: other case, no slash
        var prompts = new Prompts();                                        // cancels whenever it is asked
        var session = new ScoutSession(store, prompts.Ask, _ => true);

        var same = await session.SendAsync(Client(new Uri("https://scout-test.example.com/")), OneDevice, CancellationToken.None);
        Assert.Equal(ScoutOutcomeKind.Sent, same.Kind);
        Assert.Empty(prompts.Asked);

        var other = new Uri("https://scout-other.example.com/");
        var elsewhere = await session.SendAsync(Client(other), OneDevice, CancellationToken.None);
        Assert.Equal(ScoutOutcomeKind.Cancelled, elsewhere.Kind);
        Assert.Equal((other, false), Assert.Single(prompts.Asked));
        Assert.Equal(1, await StoredAsync(admin));
    }
}
