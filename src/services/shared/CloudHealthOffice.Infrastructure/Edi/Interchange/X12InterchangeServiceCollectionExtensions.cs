using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

public static class X12InterchangeServiceCollectionExtensions
{
    /// <summary>
    /// Registers interchange control (envelope validation, TA1 generation and
    /// storage, outbound TA1 tracking). MongoDB when <c>MongoDb:ConnectionString</c>
    /// is set (the service's own database, so the shared registration's
    /// tenant scoping applies) when an <see cref="IMongoDatabase"/> is
    /// registered, otherwise a process-local store.
    /// </summary>
    public static IServiceCollection AddChoX12Interchange(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<X12InterchangeOptions>(configuration.GetSection(X12InterchangeOptions.SectionName));

        // MongoDB whenever the service has registered an IMongoDatabase (resolved
        // per scope, so tenant-scoped databases work); otherwise one in-memory
        // store for the process.
        services.TryAddSingleton<InMemoryX12InterchangeStore>();
        services.TryAddScoped<IX12InterchangeStore>(sp =>
            sp.GetService<IMongoDatabase>() is { } database
                ? new MongoX12InterchangeStore(database)
                : sp.GetRequiredService<InMemoryX12InterchangeStore>());
        if (!string.IsNullOrEmpty(configuration["MongoDb:ConnectionString"]))
            services.AddHostedService<X12InterchangeIndexInitializer>();

        services.TryAddScoped<IX12InterchangeIntake, X12InterchangeIntake>();
        services.TryAddScoped<IOutboundInterchangeTracker, OutboundInterchangeTracker>();
        return services;
    }

    /// <summary>Largest TA1 file accepted on the inbound endpoint (1 MB: a TA1 interchange is a few hundred bytes).</summary>
    public const int MaxTa1FileBytes = 1024 * 1024;

    /// <summary>
    /// Maps the TA1 endpoints under <paramref name="prefix"/> (e.g.
    /// <c>api/v1/claims/interchange</c>):
    /// <list type="bullet">
    /// <item><c>GET  {prefix}/ta1</c> — stored TA1s (query: direction, ackCode, controlNumber, senderId, limit)</item>
    /// <item><c>GET  {prefix}/ta1/{id}</c> — one stored TA1</item>
    /// <item><c>GET  {prefix}/ta1/{id}/edi</c> — its X12 text</item>
    /// <item><c>POST {prefix}/ta1/inbound</c> — a partner's TA1 file (text body) for interchanges this service sent</item>
    /// <item><c>GET  {prefix}/outbound</c> — interchanges this service sent and their TA1 status (query: status, limit)</item>
    /// </list>
    /// No permission is named: the service's default read/write permissions
    /// apply through its fallback authorization policy.
    /// </summary>
    public static RouteGroupBuilder MapChoX12InterchangeEndpoints(this IEndpointRouteBuilder app, string prefix)
    {
        var group = app.MapGroup(prefix).WithTags("X12 Interchange (TA1)");

        group.MapGet("/ta1", async (ICurrentActor actor, IX12InterchangeStore store,
            string? direction, string? ackCode, string? controlNumber, string? senderId, int? limit, CancellationToken ct) =>
            Results.Ok(await store.ListAcknowledgmentsAsync(actor.TenantId, new InterchangeAcknowledgmentQuery
            {
                Direction = direction,
                AckCode = ackCode,
                AcknowledgedControlNumber = controlNumber,
                SenderId = senderId,
                Limit = limit ?? 100,
            }, ct)));

        group.MapGet("/ta1/{id}", async (string id, ICurrentActor actor, IX12InterchangeStore store, CancellationToken ct) =>
            await store.GetAcknowledgmentAsync(actor.TenantId, id, ct) is { } record
                ? Results.Ok(record)
                : Results.NotFound(new { error = $"TA1 {id} not found" }));

        group.MapGet("/ta1/{id}/edi", async (string id, ICurrentActor actor, IX12InterchangeStore store, HttpResponse response, CancellationToken ct) =>
        {
            var record = await store.GetAcknowledgmentAsync(actor.TenantId, id, ct);
            if (record?.Ta1Content is null) return Results.NotFound(new { error = $"TA1 {id} not found" });
            response.Headers.ContentDisposition = $"attachment; filename=\"TA1_{record.Ta1ControlNumber ?? record.Id}.edi\"";
            return Results.Text(record.Ta1Content, "text/plain");
        });

        group.MapPost("/ta1/inbound", async (HttpRequest request, ICurrentActor actor, IOutboundInterchangeTracker tracker,
            string? fileName, CancellationToken ct) =>
        {
            var content = await ReadBodyAsync(request, ct);
            if (content is null)
                return Results.Json(new { error = $"The file is larger than {MaxTa1FileBytes} bytes" }, statusCode: StatusCodes.Status413PayloadTooLarge);
            if (string.IsNullOrWhiteSpace(content))
                return Results.BadRequest(new { error = "The request body must be a TA1 interchange." });
            try
            {
                return Results.Ok(await tracker.ProcessInboundTa1Async(actor.TenantId, content, fileName, ct));
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = $"The TA1 file could not be read: {ex.Message}" });
            }
        }).Accepts<string>("text/plain", "application/edi-x12", "application/octet-stream");

        group.MapGet("/outbound", async (ICurrentActor actor, IX12InterchangeStore store, string? status, int? limit, CancellationToken ct) =>
            Results.Ok(await store.ListOutboundAsync(actor.TenantId, status, limit ?? 100, ct)));

        return group;
    }

    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxTa1FileBytes) return null;
        using var reader = new StreamReader(request.Body);
        var buffer = new char[MaxTa1FileBytes + 1];
        var total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
        {
            total += read;
            if (total > MaxTa1FileBytes) return null;
        }
        return new string(buffer, 0, total);
    }
}

/// <summary>Creates the interchange-control indexes once at startup.</summary>
public sealed class X12InterchangeIndexInitializer : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<X12InterchangeIndexInitializer> _logger;

    public X12InterchangeIndexInitializer(IServiceProvider services, ILogger<X12InterchangeIndexInitializer> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _services.CreateScope();
            if (scope.ServiceProvider.GetService<IX12InterchangeStore>() is MongoX12InterchangeStore store)
                await store.EnsureIndexesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Indexes only speed reads up; the duplicate check relies on _id, which always exists.
            _logger.LogWarning(ex, "Could not create X12 interchange indexes");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
