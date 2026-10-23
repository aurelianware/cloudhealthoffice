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
        // The worker's claim: queued runs, and running ones whose lease ran out.
        importRuns.Indexes.CreateOne(new CreateIndexModel<EnrollmentImportRun>(
            Builders<EnrollmentImportRun>.IndexKeys
                .Ascending(x => x.Status)
                .Ascending(x => x.LeaseExpiresAt)
                .Ascending(x => x.QueuedAt),
            new CreateIndexOptions { Name = "ix_status_lease_queued" }),
            cancellationToken: cancellationToken);

        var units = _db.GetCollection<EnrollmentImportUnitRecord>(Services.EnrollmentImportUnitStore.CollectionName);
        units.Indexes.CreateOne(new CreateIndexModel<EnrollmentImportUnitRecord>(
            Builders<EnrollmentImportUnitRecord>.IndexKeys
                .Ascending(x => x.TenantId)
                .Ascending(x => x.RunId)
                .Ascending(x => x.Status),
            new CreateIndexOptions { Name = "ix_tenant_run_status" }),
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

        var exceptions = _db.GetCollection<EnrollmentException>(Repositories.EnrollmentExceptionRepository.CollectionName);
        exceptions.Indexes.CreateOne(new CreateIndexModel<EnrollmentException>(
            Builders<EnrollmentException>.IndexKeys
                .Ascending(x => x.TenantId)
                .Ascending(x => x.Status)
                .Ascending(x => x.CreatedAt),
            new CreateIndexOptions { Name = "ix_tenant_status_created" }),
            cancellationToken: cancellationToken);
        exceptions.Indexes.CreateOne(new CreateIndexModel<EnrollmentException>(
            Builders<EnrollmentException>.IndexKeys
                .Ascending(x => x.TenantId)
                .Ascending(x => x.MemberId),
            new CreateIndexOptions { Name = "ix_tenant_member" }),
            cancellationToken: cancellationToken);
        // Orphan retry: a tenant's unresolved dependents waiting on one subscriber.
        exceptions.Indexes.CreateOne(new CreateIndexModel<EnrollmentException>(
            Builders<EnrollmentException>.IndexKeys
                .Ascending(x => x.TenantId)
                .Ascending(x => x.ReasonCode)
                .Ascending("Dependent.SubscriberId"),
            new CreateIndexOptions { Name = "ix_tenant_reason_dependent_subscriber" }),
            cancellationToken: cancellationToken);
        // Orphan sweep: automatic holds whose grace period has ended.
        exceptions.Indexes.CreateOne(new CreateIndexModel<EnrollmentException>(
            Builders<EnrollmentException>.IndexKeys
                .Ascending(x => x.AutoHeld)
                .Ascending(x => x.Status)
                .Ascending(x => x.HeldUntil)
                .Ascending(x => x.TenantId),
            new CreateIndexOptions { Name = "ix_autoheld_status_helduntil_tenant" }),
            cancellationToken: cancellationToken);
        // The interim enrollment-settings collection (#1500) is no longer read:
        // the orphan grace period is a plan setting in tenant-service.

        _logger.LogInformation("Enrollment-import-service Mongo indexes ensured.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
