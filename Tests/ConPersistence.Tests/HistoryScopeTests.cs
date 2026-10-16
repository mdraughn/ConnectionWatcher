using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Topology;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Queries;

namespace ConPersistence.Tests;

public class HistoryScopeTests : IAsyncLifetime, IDisposable
{
    private readonly TestDbFactory _factory = new();
    private Guid _routerId;
    private Guid _nasId;

    public async Task InitializeAsync()
    {
        var store = new ConfigStore(_factory);
        await store.SeedAsync(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "203.0.113.1", Profile = "Internal" },
                new TargetConfig { Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = TargetScope.Local },
            ],
        });
        var options = await store.LoadWatcherOptionsAsync();
        _routerId = options.Targets.Single(t => t.Name == "Router").Id!.Value;
        _nasId = options.Targets.Single(t => t.Name == "NAS").Id!.Value;

        var at = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        await using var db = _factory.CreateDbContext();
        db.Transitions.AddRange(
            Row(_routerId, "Router", "Target", at),
            Row(_nasId, "NAS", "Target", at.AddMinutes(1)),
            Row(VerdictCoordinator.SubjectId, "Internet", "Verdict", at.AddMinutes(2)));
        db.Glitches.AddRange(
            new GlitchRow { TargetId = _routerId, TargetName = "Router", Class = 0, StartUtc = at, EndUtc = at },
            new GlitchRow { TargetId = _nasId, TargetName = "NAS", Class = 0, StartUtc = at, EndUtc = at },
            new GlitchRow { TargetId = VerdictCoordinator.SubjectId, TargetName = "Internet", Class = 1, StartUtc = at, EndUtc = at });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _factory.Dispose();

    private static TransitionRow Row(Guid id, string name, string kind, DateTime at) => new()
    {
        TimestampUtc = at, SubjectId = id, SubjectName = name, SubjectKind = kind,
        FromState = "Responding", ToState = "Down", Trigger = "ThresholdCrossed",
    };

    private async Task<List<string>> Names(ScopeFilter scope, string? subjectName = null)
    {
        await using var db = _factory.CreateDbContext();
        var rows = await HistoryQueries.TransitionsAsync(db, null, null, null, 100, null, subjectName, scope);
        return rows.Select(r => r.SubjectName).OrderBy(n => n).ToList();
    }

    [Fact]
    public async Task All_returns_every_subject()
        => Assert.Equal(["Internet", "NAS", "Router"], await Names(ScopeFilter.All));

    [Fact]
    public async Task Internet_excludes_Local_targets_but_keeps_pools_and_the_verdict()
        => Assert.Equal(["Internet", "Router"], await Names(ScopeFilter.Internet));

    [Fact]
    public async Task Local_returns_only_Local_targets()
        => Assert.Equal(["NAS"], await Names(ScopeFilter.Local));

    [Fact]
    public async Task Scope_combines_with_a_subject_name_filter()
    {
        Assert.Equal(["NAS"], await Names(ScopeFilter.Local, "NAS"));
        Assert.Empty(await Names(ScopeFilter.Internet, "NAS"));
    }

    [Fact]
    public async Task Glitches_filter_by_scope_and_keep_internet_class_glitches_in_Internet()
    {
        await using var db = _factory.CreateDbContext();
        var internet = await HistoryQueries.GlitchesAsync(db, null, null, null, 100, ScopeFilter.Internet);
        var local = await HistoryQueries.GlitchesAsync(db, null, null, null, 100, ScopeFilter.Local);

        Assert.Equal(["Internet", "Router"], internet.Select(g => g.TargetName).OrderBy(n => n));
        Assert.Equal(["NAS"], local.Select(g => g.TargetName));
    }

    [Fact]
    public async Task LocalTargetIds_lists_only_current_Local_targets()
    {
        await using var db = _factory.CreateDbContext();
        var ids = await HistoryQueries.LocalTargetIdsAsync(db);
        Assert.Equal([_nasId], ids);
    }

    [Theory]
    [InlineData(null, ScopeFilter.All)]
    [InlineData("", ScopeFilter.All)]
    [InlineData("all", ScopeFilter.All)]
    [InlineData("Internet", ScopeFilter.Internet)]
    [InlineData("LOCAL", ScopeFilter.Local)]
    public void TryParse_accepts_the_three_values_case_insensitively(string? text, ScopeFilter expected)
    {
        Assert.True(ScopeFilters.TryParse(text, out var filter));
        Assert.Equal(expected, filter);
    }

    [Fact]
    public void TryParse_rejects_anything_else()
        => Assert.False(ScopeFilters.TryParse("bogus", out _));

    [Fact]
    public async Task Resolver_target_transitions_stay_on_the_internet_side_and_are_tagged_Resolver()
    {
        var store = new ConfigStore(_factory);
        await store.SeedAsync(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "203.0.113.1", Profile = "Internal" },
                new TargetConfig { Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = TargetScope.Local },
                new TargetConfig { Name = "Resolver", Address = "203.0.113.53", Profile = "Internal", Scope = TargetScope.Resolver },
            ],
        });
        var resolverId = (await store.LoadWatcherOptionsAsync()).Targets.Single(t => t.Name == "Resolver").Id!.Value;
        await using (var seed = _factory.CreateDbContext())
        {
            seed.Transitions.Add(Row(resolverId, "Resolver", "Target", new DateTime(2026, 10, 1, 13, 0, 0, DateTimeKind.Utc)));
            await seed.SaveChangesAsync();
        }

        await using var db = _factory.CreateDbContext();
        var internet = await HistoryQueries.TransitionsAsync(db, null, null, null, 100, null, null, ScopeFilter.Internet);
        var local = await HistoryQueries.TransitionsAsync(db, null, null, null, 100, null, null, ScopeFilter.Local);
        var localIds = await HistoryQueries.LocalTargetIdsAsync(db);
        var resolverIds = await HistoryQueries.ResolverTargetIdsAsync(db);
        var knownIds = await HistoryQueries.KnownTargetIdsAsync(db);

        var tagged = internet.Select(r => TransitionDto.From(r, localIds, resolverIds, knownIds))
            .Single(d => d.SubjectName == "Resolver");
        Assert.Equal("Resolver", tagged.Scope);
        Assert.DoesNotContain(local, r => r.SubjectName == "Resolver");
    }
}
