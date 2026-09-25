using ConPersistence.Config;
using ConPersistence.Snapshots;

namespace ConPersistence.Tests;

public class SnapshotImporterTests
{
    [Fact]
    public async Task Import_raises_ConfigChanged_once_on_the_service_it_is_given()
    {
        using var factory = new TestDbFactory();
        var store = new ConfigStore(factory);
        var service = new ConfigService(factory, store);
        var changes = 0;
        service.ConfigChanged += () => changes++;

        await SnapshotImporter.ImportAsync(factory, service, new Snapshot
        {
            Config = new ConfigDocument
            {
                Profiles = [new ProfileDto { Name = "Internal" }],
                Targets = [new TargetDto { Name = "Router", Address = "192.168.1.1", Profile = "Internal" }],
            },
        });

        Assert.Equal(1, changes);
        Assert.Single((await store.LoadWatcherOptionsAsync()).Targets);
    }
}
