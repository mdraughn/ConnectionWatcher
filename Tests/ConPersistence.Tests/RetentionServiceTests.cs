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
}
