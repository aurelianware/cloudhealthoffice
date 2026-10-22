using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Edi.Files;

public static class EdiFileServiceCollectionExtensions
{
    /// <summary>
    /// Registers the EDI file record (docs/architecture/edi-operations.md §3):
    /// MongoDB (collection <c>edi-files</c>) when the service registered an
    /// <see cref="IMongoDatabase"/>, otherwise a process-local store.
    /// Called by <c>AddChoX12Interchange</c>.
    /// </summary>
    public static IServiceCollection AddChoEdiFiles(this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryEdiFileStore>();
        services.TryAddScoped<IEdiFileStore>(sp =>
            sp.GetService<IMongoDatabase>() is { } database
                ? new MongoEdiFileStore(database)
                : sp.GetRequiredService<InMemoryEdiFileStore>());
        services.TryAddScoped<IEdiFileRecorder, EdiFileRecorder>();
        return services;
    }

    /// <summary>
    /// Maps the file endpoints under <paramref name="prefix"/> (e.g.
    /// <c>api/v1/claims/edi</c>):
    /// <list type="bullet">
    /// <item><c>GET {prefix}/files</c>: the tenant's files, newest first (query: direction, type, status, from, to, controlNumber, limit)</item>
    /// <item><c>GET {prefix}/files/{id}</c>: one file</item>
    /// </list>
    /// File records carry envelope identifiers and counts only, so the
    /// service's default read permission applies (edi:read comes with the
    /// EDI Operations page).
    /// </summary>
    public static RouteGroupBuilder MapChoEdiFileEndpoints(this IEndpointRouteBuilder app, string prefix)
    {
        var group = app.MapGroup(prefix).WithTags("EDI files");

        group.MapGet("/files", async (ICurrentActor actor, IEdiFileStore store,
            string? direction, string? type, string? status, DateTime? from, DateTime? to, string? controlNumber, int? limit,
            CancellationToken ct) =>
            Results.Ok(await store.ListAsync(actor.TenantId, new EdiFileQuery
            {
                Direction = direction,
                TransactionType = type,
                Status = status,
                From = from?.ToUniversalTime(),
                To = to?.ToUniversalTime(),
                ControlNumber = controlNumber,
                Limit = limit ?? 100,
            }, ct)));

        group.MapGet("/files/{id}", async (string id, ICurrentActor actor, IEdiFileStore store, CancellationToken ct) =>
            await store.GetAsync(actor.TenantId, id, ct) is { } file
                ? Results.Ok(file)
                : Results.NotFound(new { error = $"EDI file {id} not found" }));

        return group;
    }
}
