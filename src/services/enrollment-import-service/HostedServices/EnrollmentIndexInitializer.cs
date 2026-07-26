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
        // Coverage indexing used to live here too, back when this service
        // wrote Coverage documents directly into a Mongo collection shared
        // with coverage-service's own repository. Coverage creation is now
        // delegated to coverage-service via ICoverageServiceClient, which
        // owns that collection (and its indexes) exclusively.
        var transactions = _db.GetCollection<EnrollmentTransaction>("enrollment-transactions");
        transactions.Indexes.CreateOne(new CreateIndexModel<EnrollmentTransaction>(
            Builders<EnrollmentTransaction>.IndexKeys
                .Ascending(x => x.TenantId)
                .Ascending(x => x.MemberId)
                .Descending(x => x.ReceivedAt),
            new CreateIndexOptions { Name = "ix_tenant_member_received" }),
            cancellationToken: cancellationToken);

        var importRuns = _db.GetCollection<EnrollmentImportRun>("enrollment-import-runs");
        importRuns.Indexes.CreateOne(new CreateIndexModel<EnrollmentImportRun>(
            Builders<EnrollmentImportRun>.IndexKeys
                .Ascending(x => x.TenantId)
                .Descending(x => x.StartedAt),
            new CreateIndexOptions { Name = "ix_tenant_started" }),
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
