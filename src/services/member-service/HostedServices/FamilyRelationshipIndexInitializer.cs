using MemberService.Models;
using MemberService.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace MemberService.HostedServices;

/// <summary>
/// Provisions MongoDB indexes on the <c>FamilyRelationships</c> collection at startup.
/// Matches the pattern of <see cref="MemberIndexInitializer"/>: idempotent, runs once,
/// keeps repository construction side-effect free.
/// </summary>
public sealed class FamilyRelationshipIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly ILogger<FamilyRelationshipIndexInitializer> _logger;

    public FamilyRelationshipIndexInitializer(
        IMongoDatabase db,
        ILogger<FamilyRelationshipIndexInitializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _db.GetCollection<FamilyRelationship>(
            FamilyRelationshipRepositoryMongo.CollectionName);

        // Supports ListBySubjectAsync (portal Family tab, shim idempotency check).
        var subjectKeys = Builders<FamilyRelationship>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.SubjectMemberId)
            .Ascending(x => x.EndDate);
        collection.Indexes.CreateOne(new CreateIndexModel<FamilyRelationship>(
            subjectKeys,
            new CreateIndexOptions { Name = "ix_tenant_subject_enddate" }),
            cancellationToken: cancellationToken);

        // Supports GetPairAsync (update/end/delete paths).
        var pairKeys = Builders<FamilyRelationship>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.PairId);
        collection.Indexes.CreateOne(new CreateIndexModel<FamilyRelationship>(
            pairKeys,
            new CreateIndexOptions { Name = "ix_tenant_pair" }),
            cancellationToken: cancellationToken);

        // Supports ListTouchingAsync (graph derivation, portal reads).
        var relatedKeys = Builders<FamilyRelationship>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.RelatedMemberId);
        collection.Indexes.CreateOne(new CreateIndexModel<FamilyRelationship>(
            relatedKeys,
            new CreateIndexOptions { Name = "ix_tenant_related" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation("FamilyRelationship indexes ensured.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
