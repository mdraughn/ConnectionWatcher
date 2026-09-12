using System.Net;
using System.Text.Json;
using ConPersistence.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>
/// Warm restart end-to-end: a second instance on the same database resumes
/// the last known states (no Starting parade, no new transition rows) and
/// corrects honestly when the world changed while it was down.
/// </summary>
public class WarmRestartTests
{
    private static async Task<JsonElement> GetJson(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement?> TargetStatus(HttpClient client, string name)
    {
        var response = await client.GetAsync("/api/v1/status");
        if (!response.IsSuccessStatusCode)
            return null;
        var status = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        foreach (var t in status.GetProperty("targets").EnumerateArray())
            if (t.GetProperty("name").GetString() == name)
                return t;
        return null;
    }

    private static async Task<int> TransitionCount(HttpClient client)
        => (await GetJson(client, "/api/v1/transitions?limit=1000"))
            .GetProperty("items").GetArrayLength();

    [Fact]
    public async Task Restart_resumes_states_without_new_transitions()
    {
        var dir = Directory.CreateTempSubdirectory("conwatch-warm").FullName;
        var dbPath = Path.Combine(dir, "conwatch.db");
        try
        {
            int countBefore;
            using (var first = new ConWatchFactory(dbPath))
            {
                var client = first.CreateClient();
                await ConfigApiTests.SeedHomeAsync(client);
                await ConfigApiTests.Eventually(async () =>
                    (await TargetStatus(client, "Google"))?.GetProperty("state").GetString() == "Responding");
                await first.Services.GetRequiredService<TransitionRecorder>().FlushAsync();
                countBefore = await TransitionCount(client);
            }

            using var second = new ConWatchFactory(dbPath);
            var client2 = second.CreateClient();

            // The state carries over: Google reads Responding from the first
            // poll on (assumed until fresh pings confirm), never Starting.
            var google = await TargetStatus(client2, "Google");
            Assert.NotNull(google);
            Assert.Equal("Responding", google.Value.GetProperty("state").GetString());

            // Confirmation clears the assumed flag without any transition.
            await ConfigApiTests.Eventually(async () =>
            {
                var t = await TargetStatus(client2, "Google");
                return t?.GetProperty("assumed").GetBoolean() == false
                    && t?.GetProperty("state").GetString() == "Responding";
            });

            await second.Services.GetRequiredService<TransitionRecorder>().FlushAsync();
            Assert.Equal(countBefore, await TransitionCount(client2));

            // The verdict resumed too — no re-proving detour through Unknown.
            var status = await GetJson(client2, "/api/v1/status");
            Assert.Equal("Healthy", status.GetProperty("verdict").GetProperty("state").GetString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Contradicted_seed_corrects_from_the_presumed_state()
    {
        var dir = Directory.CreateTempSubdirectory("conwatch-warm2").FullName;
        var dbPath = Path.Combine(dir, "conwatch.db");
        try
        {
            long lastIdBefore;
            using (var first = new ConWatchFactory(dbPath))
            {
                var client = first.CreateClient();
                await ConfigApiTests.SeedHomeAsync(client);
                await ConfigApiTests.Eventually(async () =>
                    (await TargetStatus(client, "Google"))?.GetProperty("state").GetString() == "Responding");
                await first.Services.GetRequiredService<TransitionRecorder>().FlushAsync();
                lastIdBefore = (await GetJson(client, "/api/v1/transitions?limit=1"))
                    .GetProperty("items")[0].GetProperty("id").GetInt64();
            }

            using var second = new ConWatchFactory(dbPath);
            second.Pings.SetUp(IPAddress.Parse("8.8.8.8"), false); // world changed while down
            var client2 = second.CreateClient();

            await ConfigApiTests.Eventually(async () =>
                (await TargetStatus(client2, "Google"))?.GetProperty("state").GetString() == "Down");

            // The correction is honest: one transition FROM the presumption,
            // and no Starting row from the restart.
            await second.Services.GetRequiredService<TransitionRecorder>().FlushAsync();
            var items = (await GetJson(client2, "/api/v1/transitions?subjectId=" +
                    (await GetJson(client2, "/api/v1/targets")).EnumerateArray()
                        .Single(t => t.GetProperty("name").GetString() == "Google")
                        .GetProperty("id").GetString() + "&limit=10"))
                .GetProperty("items").EnumerateArray().ToList();
            var newest = items.First(); // newest-first
            Assert.Equal("Responding", newest.GetProperty("from").GetString());
            Assert.Equal("Down", newest.GetProperty("to").GetString());
            Assert.DoesNotContain(items, i =>
                i.GetProperty("id").GetInt64() > lastIdBefore &&
                i.GetProperty("to").GetString() == "Starting");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
