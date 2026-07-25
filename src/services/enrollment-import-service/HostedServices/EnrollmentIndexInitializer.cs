using EnrollmentImportService.Models;
using MongoDB.Driver;

namespace EnrollmentImportService.HostedServices;

/// <summary>
/// Creates the Mongo indexes used by enrollment-import-service's collections
/// once at startup. Running index creation from a hosted service (instead
/// of the repository constructors) keeps repository construction side-effect
/// free and lets those repositories be registered as singletons — same
/// pattern as member-service's MemberIndexInitializer/MemberEventIndexInitializer.
///
/// Idempotent: Mongo silently no-ops an index that already exists with the
/// same spec.
/// </summary>
public sealed class EnrollmentIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly ILogger<EnrollmentIndexInitializer> _logger;

    public EnrollmentIndexInitializer(IMongoDatabase db, ILogger<EnrollmentIndexInitializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // No explicit Name here: coverage-service's CoverageRepositoryMongo
        // already creates an unnamed (auto-named) index on this exact key
        // pattern against the same shared "Coverage" collection. Naming
        // ours differently would conflict (Mongo rejects two indexes with
        // an identical key pattern under different names) — letting Mongo
        // auto-name it matches the existing one and no-ops correctly.
        var coverage = _db.GetCollection<Coverage>("Coverage");
        coverage.Indexes.CreateOne(new CreateIndexModel<Coverage>(
            Builders<Coverage>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.MemberId)),
            cancellationToken: cancellationToken);

        var transactions = _db.GetCollection<EnrollmentTransaction>("enrollment-transactions");
        transactions.Indexes.CreateOne(new CreateIndexModel<EnrollmentTransaction>(
            Builders<EnrollmentTransaction>.IndexKeys
                .Ascending(x => x.TenantId)
                .Ascending(x => x.MemberId)
                .Descending(x => x.ReceivedAt),
            new CreateIndexOptions { Name = "ix_tenant_member_received" }),
            cancellationToken: cancellationToken);

        var events = _db.GetCollection<EnrollmentEvent>("enrollment-events");
        events.Indexes.CreateOne(new CreateIndexModel<EnrollmentEvent>(
            Builders<EnrollmentEvent>.IndexKeys
                .Ascending(x => x.TenantId)
                .Ascending(x => x.MemberId)
                .Ascending(x => x.EventId),
            new CreateIndexOptions { Unique = true, Name = "ux_tenant_member_event" }),
            cancellationToken: cancellationToken);
        events.Indexes.CreateOne(new CreateIndexModel<EnrollmentEvent>(
            Builders<EnrollmentEvent>.IndexKeys
                .Ascending(x => x.TenantId)
                .Ascending(x => x.MemberId)
                .Ascending(x => x.Version),
            new CreateIndexOptions { Unique = true, Name = "ux_tenant_member_version" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation("Enrollment-import-service Mongo indexes ensured.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
