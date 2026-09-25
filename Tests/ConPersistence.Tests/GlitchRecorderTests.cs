using System.Reactive.Subjects;
using ConEngine.Events;
using ConPersistence.Entities;
using ConPersistence.Recording;
using ConPersistence.Retention;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConPersistence.Tests;

public class GlitchRecorderTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly GlitchRecorder _recorder;
    private readonly Subject<GlitchEvent> _source = new();

    public GlitchRecorderTests()
    {
        _recorder = new GlitchRecorder(_factory, NullLogger<GlitchRecorder>.Instance);
        _recorder.Attach(_source);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        _factory.Dispose();
    }

    private static GlitchEvent Sample(Guid id, DateTimeOffset start, bool coalesced = false) => new()
    {
        TargetId = id,
        TargetName = "Google",
        StartUtc = start,
        EndUtc = start.AddSeconds(8),
        LostCount = 2,
        SampleCount = 8,
        Coalesced = coalesced,
    };

    [Fact]
    public async Task Writes_glitch_rows()
    {
        var id = Guid.NewGuid();
        _source.OnNext(Sample(id, DateTimeOffset.Parse("2026-09-06T03:34:00Z")));
        await _recorder.FlushAsync();

        await using var db = _factory.CreateDbContext();
        var row = Assert.Single(db.Glitches);
        Assert.Equal(id, row.TargetId);
        Assert.Equal(2, row.LostCount);
        Assert.Equal(0, row.Class);
    }

    [Fact]
    public async Task Coalesced_event_extends_the_previous_row()
    {
        var id = Guid.NewGuid();
        var start = DateTimeOffset.Parse("2026-09-06T03:34:00Z");
        _source.OnNext(Sample(id, start));
        _source.OnNext(Sample(id, start.AddSeconds(30), coalesced: true));
        await _recorder.FlushAsync();

        await using var db = _factory.CreateDbContext();
        var row = Assert.Single(db.Glitches);
        Assert.Equal(start.AddSeconds(38).UtcDateTime, row.EndUtc);
        Assert.Equal(4, row.LostCount);
        Assert.Equal(16, row.SampleCount);
    }

    [Fact]
    public async Task Retention_prunes_old_glitches_with_minute_data()
    {
        var now = DateTime.Parse("2026-09-06T00:00:00Z").ToUniversalTime();
        await using (var db = _factory.CreateDbContext())
        {
            db.Glitches.Add(new GlitchRow { TargetId = Guid.NewGuid(), TargetName = "old",
                StartUtc = now.AddDays(-91), EndUtc = now.AddDays(-91) });
            db.Glitches.Add(new GlitchRow { TargetId = Guid.NewGuid(), TargetName = "recent",
                StartUtc = now.AddDays(-1), EndUtc = now.AddDays(-1) });
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
        {
            await new RetentionPass(NullLogger.Instance).RunAsync(db, now,
                new PersistenceOptions { MinuteRetentionDays = 90 }, CancellationToken.None);
        }

        await using (var check = _factory.CreateDbContext())
        {
            var remaining = Assert.Single(check.Glitches);
            Assert.Equal("recent", remaining.TargetName);
        }
    }
}
