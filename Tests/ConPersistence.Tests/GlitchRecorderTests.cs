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
    public async Task A_coalesced_glitch_in_a_later_flush_extends_the_row()
    {
        // The production path: the extending event arrives in a LATER batch, so the
        // row to extend is in the database, not in the batch being written.
        var id = Guid.NewGuid();
        var start = DateTimeOffset.Parse("2026-09-06T03:34:00Z");
        _source.OnNext(Sample(id, start));
        await _recorder.FlushAsync();
        _source.OnNext(Sample(id, start.AddSeconds(30), coalesced: true));
        await _recorder.FlushAsync();

        await using var db = _factory.CreateDbContext();
        var row = Assert.Single(db.Glitches);
        Assert.Equal(start, new DateTimeOffset(row.StartUtc));
        Assert.Equal(start.AddSeconds(38).UtcDateTime, row.EndUtc);
        Assert.Equal(4, row.LostCount);
        Assert.Equal(16, row.SampleCount);
    }

    [Fact]
    public async Task A_coalesced_glitch_extends_only_the_same_targets_row()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var start = DateTimeOffset.Parse("2026-09-06T03:34:00Z");
        _source.OnNext(Sample(a, start));
        _source.OnNext(Sample(b, start.AddSeconds(10))); // B's row is the newest in the table
        await _recorder.FlushAsync();
        _source.OnNext(Sample(a, start.AddSeconds(30), coalesced: true));
        await _recorder.FlushAsync();

        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, db.Glitches.Count());
        var rowA = db.Glitches.Single(r => r.TargetId == a);
        var rowB = db.Glitches.Single(r => r.TargetId == b);
        Assert.Equal(4, rowA.LostCount);
        Assert.Equal(start.AddSeconds(38).UtcDateTime, rowA.EndUtc);
        Assert.Equal(2, rowB.LostCount);
        Assert.Equal(start.AddSeconds(18).UtcDateTime, rowB.EndUtc);
    }

    [Fact]
    public async Task A_coalesced_glitch_with_no_prior_row_inserts()
    {
        // Its predecessor may have been pruned or lost; the event still becomes a row.
        var id = Guid.NewGuid();
        var start = DateTimeOffset.Parse("2026-09-06T03:34:00Z");
        _source.OnNext(Sample(id, start, coalesced: true));
        await _recorder.FlushAsync();

        await using var db = _factory.CreateDbContext();
        var row = Assert.Single(db.Glitches);
        Assert.Equal(id, row.TargetId);
        Assert.Equal(2, row.LostCount);
        Assert.Equal(8, row.SampleCount);
        Assert.Equal(start.UtcDateTime, row.StartUtc);
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
                RetentionWindows.From(new PersistenceOptions { MinuteRetentionDays = 90 }), CancellationToken.None);
        }

        await using (var check = _factory.CreateDbContext())
        {
            var remaining = Assert.Single(check.Glitches);
            Assert.Equal("recent", remaining.TargetName);
        }
    }
}
