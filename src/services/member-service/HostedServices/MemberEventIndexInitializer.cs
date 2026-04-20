using MemberService.Models;
using MemberService.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace MemberService.HostedServices;

/// <summary>
/// Creates the Mongo indexes used by the member-events stream once at startup.
/// Running index creation from a hosted service (instead of the repository
/// constructor) keeps repository construction side-effect free and lets us
/// register the repository as a singleton.
///
/// Idempotent: Mongo silently no-ops an index that already exists with the
/// same spec.
/// </summary>
public sealed class MemberEventIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly string _collectionName;
    private readonly ILogger<MemberEventIndexInitializer> _logger;

    public MemberEventIndexInitializer(
        IMongoDatabase db,
        string collectionName,
        ILogger<MemberEventIndexInitializer> logger)
    {
        _db = db;
        _collectionName = collectionName;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _db.GetCollection<MemberEvent>(_collectionName);

        var idemKeys = Builders<MemberEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.MemberId)
            .Ascending(x => x.EventId);
        collection.Indexes.CreateOne(new CreateIndexModel<MemberEvent>(
            idemKeys,
            new CreateIndexOptions { Unique = true, Name = "ux_tenant_member_event" }),
            cancellationToken: cancellationToken);

        var orderKeys = Builders<MemberEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.MemberId)
            .Ascending(x => x.Version);
        collection.Indexes.CreateOne(new CreateIndexModel<MemberEvent>(
            orderKeys,
            new CreateIndexOptions { Unique = true, Name = "ux_tenant_member_version" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation("Member event indexes ensured on collection '{Collection}'.", _collectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Creates the Mongo indexes used by the <c>Members</c> collection at startup.
/// </summary>
public sealed class MemberIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly ILogger<MemberIndexInitializer> _logger;

    public MemberIndexInitializer(IMongoDatabase db, ILogger<MemberIndexInitializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _db.GetCollection<Member>("Members");
        var keys = Builders<Member>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.MemberId);
        collection.Indexes.CreateOne(
            new CreateIndexModel<Member>(keys, new CreateIndexOptions { Name = "ix_tenant_member" }),
            cancellationToken: cancellationToken);

        var alerts = _db.GetCollection<MemberAlert>("MemberAlerts");
        alerts.Indexes.CreateOne(
            new CreateIndexModel<MemberAlert>(
                Builders<MemberAlert>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.MemberId)
                    .Descending(x => x.StartDate),
                new CreateIndexOptions { Name = "ix_tenant_member_start" }),
            cancellationToken: cancellationToken);

        var notes = _db.GetCollection<MemberNote>("MemberNotes");
        notes.Indexes.CreateOne(
            new CreateIndexModel<MemberNote>(
                Builders<MemberNote>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.MemberId)
                    .Descending(x => x.CreatedDate),
                new CreateIndexOptions { Name = "ix_tenant_member_created" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation("Member, MemberAlert, MemberNote indexes ensured.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
