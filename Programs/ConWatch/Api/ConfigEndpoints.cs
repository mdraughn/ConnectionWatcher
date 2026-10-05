using ConPersistence;
using ConPersistence.Config;

namespace ConWatch.Api;

/// <summary>CRUD for targets, profiles, pools, and whole-config export/import.</summary>
public static class ConfigEndpoints
{
    public record InsertParentRequest(TargetDto NewTarget, List<Guid> ChildIds);

    public static void MapConfig(this RouteGroupBuilder api)
    {
        var targets = api.MapGroup("/targets");
        targets.MapGet("/", async (ConfigStore store) => (await store.ExportAsync()).Targets);
        targets.MapGet("/{id:guid}", async (Guid id, ConfigStore store) =>
            (await store.ExportAsync()).Targets.SingleOrDefault(t => t.Id == id) is { } dto
                ? Results.Ok(dto) : Results.NotFound());
        targets.MapPost("/", async (TargetDto dto, ConfigService service, Guid? discoveryId) =>
            (await service.UpsertTargetAsync(dto with { Id = null }, adoptDiscoveryId: discoveryId))
                .ToHttp(r => Results.Created($"/api/v1/targets/{r.Id}", new { id = r.Id })));
        targets.MapPut("/{id:guid}", async (Guid id, TargetDto dto, ConfigService service,
                string? addressChange) =>
            (await service.UpsertTargetAsync(dto with { Id = id }, addressChange)).ToHttp());
        targets.MapDelete("/{id:guid}", async (Guid id, ConfigService service) =>
            (await service.DeleteTargetAsync(id)).ToHttp());
        targets.MapPost("/insert-parent", async (InsertParentRequest request, ConfigService service) =>
            (await service.InsertParentAsync(request.NewTarget, request.ChildIds))
                .ToHttp(r => Results.Created($"/api/v1/targets/{r.Id}", new { id = r.Id })));

        var profiles = api.MapGroup("/profiles");
        profiles.MapGet("/", async (ConfigStore store) => (await store.ExportAsync()).Profiles);
        profiles.MapGet("/{id:guid}", async (Guid id, ConfigStore store) =>
            (await store.ExportAsync()).Profiles.SingleOrDefault(p => p.Id == id) is { } dto
                ? Results.Ok(dto) : Results.NotFound());
        profiles.MapPost("/", async (ProfileDto dto, ConfigService service) =>
            (await service.UpsertProfileAsync(dto with { Id = null }))
                .ToHttp(r => Results.Created($"/api/v1/profiles/{r.Id}", new { id = r.Id })));
        profiles.MapPut("/{id:guid}", async (Guid id, ProfileDto dto, ConfigService service) =>
            (await service.UpsertProfileAsync(dto with { Id = id })).ToHttp());
        profiles.MapDelete("/{id:guid}", async (Guid id, ConfigService service) =>
            (await service.DeleteProfileAsync(id)).ToHttp());

        var pools = api.MapGroup("/pools");
        pools.MapGet("/", async (ConfigStore store) => (await store.ExportAsync()).Pools);
        pools.MapGet("/{id:guid}", async (Guid id, ConfigStore store) =>
            (await store.ExportAsync()).Pools.SingleOrDefault(p => p.Id == id) is { } dto
                ? Results.Ok(dto) : Results.NotFound());
        pools.MapPost("/", async (PoolDto dto, ConfigService service) =>
            (await service.UpsertPoolAsync(dto with { Id = null }))
                .ToHttp(r => Results.Created($"/api/v1/pools/{r.Id}", new { id = r.Id })));
        pools.MapPut("/{id:guid}", async (Guid id, PoolDto dto, ConfigService service) =>
            (await service.UpsertPoolAsync(dto with { Id = id })).ToHttp());
        pools.MapDelete("/{id:guid}", async (Guid id, ConfigService service) =>
            (await service.DeletePoolAsync(id)).ToHttp());

        api.MapGet("/config/export", async (ConfigStore store) => await store.ExportAsync());
        api.MapPost("/config/import", async (ConfigDocument doc, ConfigService service) =>
            (await service.ImportAsync(doc)).ToHttp());
    }
}
