using System.Reactive.Subjects;
using ConEngine.Topology;
using ConPersistence.Warnings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConPersistence.Tests;

public class ModelWarningLedgerTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-09T12:00:00Z"));
    private readonly ModelWarningRecorder _recorder;
    private ModelWarningLedger _ledger;
    private Subject<ModelConditionChange> _source = new();
    private List<ModelCondition> _active = new();

    private static readonly Guid Inside = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly ModelCondition Bypassed = new(
        ModelCondition.KeyFor(ModelWarningKind.HopBypassed, Inside), ModelWarningKind.HopBypassed,
        Inside, "Inside", "Inside isn't answering its probe (tcp/80), but External beyond it is");

    public ModelWarningLedgerTests()
    {
        _recorder = new ModelWarningRecorder(_factory, NullLogger<ModelWarningRecorder>.Instance);
        _ledger = NewLedger();
        _ledger.Attach(_source, () => _active);
    }

    private ModelWarningLedger NewLedger() =>
        new(_recorder, _factory, _time, NullLogger<ModelWarningLedger>.Instance);

    public void Dispose()
    {
        _recorder.Dispose();
        _factory.Dispose();
    }

    private void Raise(ModelCondition c) => _source.OnNext(new(c, true, _time.GetUtcNow()));
    private void Clear(ModelCondition c) => _source.OnNext(new(c, false, _time.GetUtcNow()));

    private async Task<List<Entities.ModelWarningRow>> Rows()
    {
        await _recorder.FlushAsync();
        await using var db = _factory.CreateDbContext();
        return await db.ModelWarnings.OrderBy(r => r.Id).ToListAsync();
    }

    [Fact]
    public async Task A_raise_opens_one_instance_and_one_row()
    {
        Raise(Bypassed);

        var w = Assert.Single(_ledger.Current);
        Assert.Equal(Bypassed.Key, w.Key);
        Assert.Equal(_time.GetUtcNow(), w.SinceUtc);
        Assert.False(w.Acknowledged);
        var row = Assert.Single(await Rows());
        Assert.Equal("HopBypassed", row.Kind);
        Assert.Equal(Inside, row.TargetId);
        Assert.Null(row.EndUtc);
    }

    [Fact]
    public async Task A_clear_closes_the_instance_and_its_row()
    {
        Raise(Bypassed);
        _time.Advance(TimeSpan.FromSeconds(27));
        Clear(Bypassed);

        Assert.Empty(_ledger.Current);
        var row = Assert.Single(await Rows());
        Assert.Equal(_time.GetUtcNow().UtcDateTime, row.EndUtc);
    }

    [Fact]
    public async Task Acknowledge_marks_the_instance_and_row_and_unknown_keys_are_refused()
    {
        Raise(Bypassed);
        Assert.True(_ledger.Acknowledge(Bypassed.Key));
        Assert.False(_ledger.Acknowledge("hop-bypassed:nope"));

        Assert.True(Assert.Single(_ledger.Current).Acknowledged);
        Assert.NotNull(Assert.Single(await Rows()).AcknowledgedUtc);
    }

    [Fact]
    public async Task A_recurrence_after_a_clear_is_a_new_unacknowledged_instance()
    {
        Raise(Bypassed);
        _ledger.Acknowledge(Bypassed.Key);
        Clear(Bypassed);
        _time.Advance(TimeSpan.FromMinutes(1));
        Raise(Bypassed);

        Assert.False(Assert.Single(_ledger.Current).Acknowledged);
        Assert.Equal(2, (await Rows()).Count);
    }

    [Fact]
    public async Task An_engine_rebuild_that_re_raises_continues_the_same_instance()
    {
        Raise(Bypassed);
        var since = _time.GetUtcNow();
        _ledger.Acknowledge(Bypassed.Key);

        _ledger.Detach();
        _source = new Subject<ModelConditionChange>();
        _time.Advance(TimeSpan.FromSeconds(3));
        _ledger.Attach(_source, () => _active);
        Raise(Bypassed); // the new engine's first evaluation

        var w = Assert.Single(_ledger.Current);
        Assert.Equal(since, w.SinceUtc);
        Assert.True(w.Acknowledged);
        var row = Assert.Single(await Rows());
        Assert.Null(row.EndUtc);
    }

    [Fact]
    public async Task Conditions_already_active_at_attach_are_adopted()
    {
        _ledger.Detach();
        _active = [Bypassed];
        _ledger.Attach(new Subject<ModelConditionChange>(), () => _active);

        Assert.Single(_ledger.Current);
        Assert.Single(await Rows());
    }

    [Fact]
    public async Task A_carried_note_the_new_engine_never_re_raises_closes_after_the_grace()
    {
        Raise(Bypassed);
        _ledger.Detach();
        _ledger.Attach(new Subject<ModelConditionChange>(), () => []);

        _time.Advance(ModelWarningLedger.ResyncGrace - TimeSpan.FromSeconds(1));
        Assert.Single(_ledger.Current); // still within the grace

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(_ledger.Current);
        Assert.NotNull(Assert.Single(await Rows()).EndUtc);
    }

    [Fact]
    public async Task A_stale_grace_timer_from_an_earlier_attach_closes_nothing()
    {
        Raise(Bypassed);
        _ledger.Detach();
        _ledger.Attach(new Subject<ModelConditionChange>(), () => []);
        _time.Advance(ModelWarningLedger.ResyncGrace / 2);
        _ledger.Detach();
        var source = new Subject<ModelConditionChange>();
        _ledger.Attach(source, () => []);
        source.OnNext(new(Bypassed, true, _time.GetUtcNow()));

        _time.Advance(ModelWarningLedger.ResyncGrace); // both timers' deadlines pass
        Assert.Single(_ledger.Current);
        Assert.Null(Assert.Single(await Rows()).EndUtc);
    }

    [Fact]
    public async Task A_process_restart_loads_open_rows_with_their_acknowledgement()
    {
        Raise(Bypassed);
        _ledger.Acknowledge(Bypassed.Key);
        var since = _time.GetUtcNow();
        await _recorder.FlushAsync();

        var restarted = NewLedger();
        await restarted.LoadAsync();
        var w = Assert.Single(restarted.Current);
        Assert.Equal(since, w.SinceUtc);
        Assert.True(w.Acknowledged);
        Assert.Equal(ModelWarningKind.HopBypassed, w.Kind);

        var source = new Subject<ModelConditionChange>();
        restarted.Attach(source, () => []);
        source.OnNext(new(Bypassed, true, _time.GetUtcNow()));
        Assert.Single(await Rows()); // continued, not reopened
    }

    [Fact]
    public async Task Rows_deleted_under_an_open_instance_do_not_break_the_close()
    {
        Raise(Bypassed);
        await _recorder.FlushAsync();
        await using (var db = _factory.CreateDbContext())
            await db.ModelWarnings.ExecuteDeleteAsync(); // clear-history ran

        Clear(Bypassed);
        Assert.Empty(await Rows());
        Assert.Empty(_ledger.Current);
    }

    [Fact]
    public async Task A_late_load_does_not_overwrite_a_live_instance_or_let_the_grace_close_it()
    {
        Raise(Bypassed);
        var since = _time.GetUtcNow();
        await _recorder.FlushAsync();
        await using (var db = _factory.CreateDbContext())
        {
            db.ModelWarnings.Add(new Entities.ModelWarningRow
            {
                Key = Bypassed.Key, Kind = "HopBypassed", Message = "old",
                StartUtc = since.UtcDateTime.AddDays(-1), AcknowledgedUtc = since.UtcDateTime.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        await _ledger.LoadAsync();

        var w = Assert.Single(_ledger.Current);
        Assert.Equal(since, w.SinceUtc);
        Assert.False(w.Acknowledged);
        _time.Advance(ModelWarningLedger.ResyncGrace + TimeSpan.FromSeconds(1));
        Assert.Single(_ledger.Current);
    }
}
