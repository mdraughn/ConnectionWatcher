using ConEngine.Tests.Fakes;
using ConPersistence.Entities;
using ConPersistence.Retention;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ConPersistence.Tests;

public class RetentionServiceTests
{
    [Fact]
    public async Task Nightly_pass_uses_the_settings_table_over_appsettings()
    {
        using var factory = new FileDbFactory();
        await factory.PrepareAsync();
        var settings = new SettingsStore(factory);
        await settings.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.MinuteRetentionDays] = "1" });
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        await using (var db = factory.CreateDbContext())
        {
            db.Rollups.Add(new RollupRow
            {
                TargetId = Guid.NewGuid(), PeriodStartUtc = now.UtcDateTime.AddDays(-2).Date,
                Resolution = "Minute", PingCount = 6, SuccessCount = 6, RttAvg = 10, RttMin = 9, RttMax = 11,
            });
            await db.SaveChangesAsync();
        }

        // Appsettings say 90 days; the table says 1. Only the table's value moves this row.
        var service = new RetentionService(factory, Options.Create(new PersistenceOptions()),
            new FakeTimeProvider(now), NullLogger<RetentionService>.Instance, settings);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (await settings.GetAsync(SettingKeys.RetentionLastRunUtc) is null && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            await using var check = factory.CreateDbContext();
            Assert.Equal("Hour", (await check.Rollups.SingleAsync()).Resolution);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static readonly DateTimeOffset Noon = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static async Task<DateTimeOffset?> LastRunAsync(SettingsStore settings) =>
        await settings.GetAsync(SettingKeys.RetentionLastRunUtc) is { } text ? DateTimeOffset.Parse(text) : null;

    private static async Task ArmedAsync(TimerCountingTime time, int before) => await time.NewTimerArmedSince(before);

    [Fact]
    public async Task The_nightly_pass_runs_at_the_configured_hour()
    {
        using var factory = new FileDbFactory();
        await factory.PrepareAsync();
        var settings = new SettingsStore(factory);
        var time = new TimerCountingTime();
        time.SetUtcNow(Noon);
        var service = new RetentionService(factory,
            Options.Create(new PersistenceOptions { RetentionRunHourUtc = 3 }),
            time, NullLogger<RetentionService>.Instance, settings);
        await service.StartAsync(CancellationToken.None);
        try
        {
            // The startup pass runs at once, whatever the hour.
            await TestWait.Eventually(async () => await LastRunAsync(settings) is not null, what: "no startup pass");
            Assert.Equal(Noon, await LastRunAsync(settings));
            await time.WaitForTimersAsync(1);

            // Next run: 03:00 tomorrow, 15 h away.
            var before = time.TimersCreated;
            time.Advance(TimeSpan.FromHours(15));
            await ArmedAsync(time, before);
            Assert.Equal(new DateTimeOffset(2026, 9, 26, 3, 0, 0, TimeSpan.Zero), await LastRunAsync(settings));

            // And then a day later, not sooner: 23 h on, nothing; 1 h more, a pass.
            before = time.TimersCreated;
            time.Advance(TimeSpan.FromHours(23));
            await Task.Delay(300); // absence check, after the positive condition above (the day-two timer is armed)
            Assert.Equal(new DateTimeOffset(2026, 9, 26, 3, 0, 0, TimeSpan.Zero), await LastRunAsync(settings));
            Assert.Equal(before, time.TimersCreated);
            time.Advance(TimeSpan.FromHours(1));
            await ArmedAsync(time, before);
            Assert.Equal(new DateTimeOffset(2026, 9, 27, 3, 0, 0, TimeSpan.Zero), await LastRunAsync(settings));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_first_pass_that_throws_still_lets_the_second_run()
    {
        using var factory = new FileDbFactory();
        await factory.PrepareAsync();
        var settings = new SettingsStore(factory);
        var time = new TimerCountingTime();
        time.SetUtcNow(Noon);
        // The service's first database request (the startup pass) fails; the settings store, on
        // the real factory, is unaffected.
        var service = new RetentionService(new FailingOnceDbFactory(factory),
            Options.Create(new PersistenceOptions { RetentionRunHourUtc = 3 }),
            time, NullLogger<RetentionService>.Instance, settings);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await time.WaitForTimersAsync(1); // the failed pass is over; the next run is scheduled
            Assert.Null(await LastRunAsync(settings));

            var before = time.TimersCreated;
            time.Advance(TimeSpan.FromHours(15));
            await ArmedAsync(time, before);

            Assert.Equal(new DateTimeOffset(2026, 9, 26, 3, 0, 0, TimeSpan.Zero), await LastRunAsync(settings));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
