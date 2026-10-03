using MongoDB.Driver;
using SmartAuthService.Models;

namespace SmartAuthService.Services;

/// <summary>Outcome of a write that may collide with an existing binding.</summary>
public enum BindingWriteResult
{
    Created,

    /// <summary>The identity (or client id) is already bound. Nothing was written.</summary>
    AlreadyBound,
}

/// <summary>
/// Server-side store of which tenant (and member / provider) each SMART
/// identity and client belongs to. The only source of a SMART token's
/// <c>tenant_id</c> and <c>patient</c>.
///
/// Every read that a tenant administrator can trigger takes the tenant from
/// their token and filters on it, so one tenant can neither see nor change
/// another's bindings: a foreign id behaves exactly like a missing one.
/// </summary>
public interface ISmartIdentityStore
{
    Task<MemberLink?> FindActiveMemberLinkAsync(SmartIdentity identity, CancellationToken ct = default);
    Task<ProviderUserLink?> FindActiveProviderLinkAsync(SmartIdentity identity, CancellationToken ct = default);
    Task<IReadOnlyList<MemberLink>> ListMemberLinksAsync(string tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderUserLink>> ListProviderLinksAsync(string tenantId, CancellationToken ct = default);
    Task<bool> RevokeMemberLinkAsync(string tenantId, string id, string actor, CancellationToken ct = default);
    Task<bool> RevokeProviderLinkAsync(string tenantId, string id, string actor, CancellationToken ct = default);

    Task CreateEnrolmentAsync(Enrolment enrolment, CancellationToken ct = default);
    Task<IReadOnlyList<Enrolment>> ListEnrolmentsAsync(string tenantId, string kind, CancellationToken ct = default);
    Task<bool> CancelEnrolmentAsync(string tenantId, string kind, string id, string actor, CancellationToken ct = default);

    /// <summary>
    /// Atomically turns a pending, unexpired enrolment into an active link for
    /// <paramref name="identity"/>. Null when the code is unknown, used,
    /// cancelled or expired; <see cref="BindingWriteResult.AlreadyBound"/> when
    /// the identity already has an active link of that kind (the enrolment is
    /// left pending so the person can sort it out with the tenant).
    /// </summary>
    Task<(BindingWriteResult Result, Enrolment Enrolment)?> RedeemEnrolmentAsync(
        string codeHash, SmartIdentity identity, CancellationToken ct = default);

    Task<ClientTenantRegistration?> FindClientAsync(string clientId, CancellationToken ct = default);
    Task<BindingWriteResult> CreateClientAsync(ClientTenantRegistration registration, CancellationToken ct = default);
    Task<IReadOnlyList<ClientTenantRegistration>> ListClientsAsync(string tenantId, CancellationToken ct = default);
    Task<bool> DeleteClientAsync(string tenantId, string clientId, CancellationToken ct = default);

    /// <summary>Development seeding only: binds an identity directly, skipping enrolment.</summary>
    Task SeedDevelopmentBindingsAsync(
        IEnumerable<MemberLink> members, IEnumerable<ProviderUserLink> providers,
        IEnumerable<ClientTenantRegistration> clients, CancellationToken ct = default);
}

public sealed class MongoSmartIdentityStore : ISmartIdentityStore
{
    public const string MemberLinksCollection = "smart_member_links";
    public const string ProviderLinksCollection = "smart_provider_users";
    public const string EnrolmentsCollection = "smart_enrolments";
    public const string ClientsCollection = "smart_client_tenants";

    private readonly IMongoCollection<MemberLink> _members;
    private readonly IMongoCollection<ProviderUserLink> _providers;
    private readonly IMongoCollection<Enrolment> _enrolments;
    private readonly IMongoCollection<ClientTenantRegistration> _clients;
    private readonly Lazy<Task> _indexes;

    public MongoSmartIdentityStore(IMongoDatabase database)
    {
        _members = database.GetCollection<MemberLink>(MemberLinksCollection);
        _providers = database.GetCollection<ProviderUserLink>(ProviderLinksCollection);
        _enrolments = database.GetCollection<Enrolment>(EnrolmentsCollection);
        _clients = database.GetCollection<ClientTenantRegistration>(ClientsCollection);
        _indexes = new Lazy<Task>(EnsureIndexesAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// An identity has at most one ACTIVE member link and one ACTIVE provider
    /// link. The partial unique index makes that a database guarantee rather
    /// than a check-then-insert race; revoked links stay as history.
    /// </summary>
    private async Task EnsureIndexesAsync()
    {
        var activeOnly = new CreateIndexOptions<MemberLink>
        {
            Unique = true,
            Name = "ux_active_identity",
            PartialFilterExpression = Builders<MemberLink>.Filter.Eq(l => l.Status, BindingStatus.Active),
        };
        await _members.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<MemberLink>(
                Builders<MemberLink>.IndexKeys.Ascending(l => l.Issuer).Ascending(l => l.Subject), activeOnly),
            new CreateIndexModel<MemberLink>(Builders<MemberLink>.IndexKeys.Ascending(l => l.TenantId)),
        ]);

        await _providers.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<ProviderUserLink>(
                Builders<ProviderUserLink>.IndexKeys.Ascending(l => l.Issuer).Ascending(l => l.Subject),
                new CreateIndexOptions<ProviderUserLink>
                {
                    Unique = true,
                    Name = "ux_active_identity",
                    PartialFilterExpression = Builders<ProviderUserLink>.Filter.Eq(l => l.Status, BindingStatus.Active),
                }),
            new CreateIndexModel<ProviderUserLink>(Builders<ProviderUserLink>.IndexKeys.Ascending(l => l.TenantId)),
        ]);

        await _enrolments.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Enrolment>(
                Builders<Enrolment>.IndexKeys.Ascending(e => e.CodeHash),
                new CreateIndexOptions { Unique = true, Name = "ux_code_hash" }),
            new CreateIndexModel<Enrolment>(
                Builders<Enrolment>.IndexKeys.Ascending(e => e.TenantId).Ascending(e => e.Kind)),
        ]);

        await _clients.Indexes.CreateOneAsync(new CreateIndexModel<ClientTenantRegistration>(
            Builders<ClientTenantRegistration>.IndexKeys.Ascending(c => c.TenantId)));
    }

    private Task ReadyAsync() => _indexes.Value;

    public async Task<MemberLink?> FindActiveMemberLinkAsync(SmartIdentity identity, CancellationToken ct = default)
    {
        await ReadyAsync();
        return await _members.Find(l => l.Issuer == identity.Issuer && l.Subject == identity.Subject
                                        && l.Status == BindingStatus.Active)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ProviderUserLink?> FindActiveProviderLinkAsync(SmartIdentity identity, CancellationToken ct = default)
    {
        await ReadyAsync();
        return await _providers.Find(l => l.Issuer == identity.Issuer && l.Subject == identity.Subject
                                          && l.Status == BindingStatus.Active)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<MemberLink>> ListMemberLinksAsync(string tenantId, CancellationToken ct = default)
    {
        await ReadyAsync();
        return await _members.Find(l => l.TenantId == tenantId).SortBy(l => l.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ProviderUserLink>> ListProviderLinksAsync(string tenantId, CancellationToken ct = default)
    {
        await ReadyAsync();
        return await _providers.Find(l => l.TenantId == tenantId).SortBy(l => l.CreatedAt).ToListAsync(ct);
    }

    public async Task<bool> RevokeMemberLinkAsync(string tenantId, string id, string actor, CancellationToken ct = default)
    {
        await ReadyAsync();
        var result = await _members.UpdateOneAsync(
            l => l.Id == id && l.TenantId == tenantId && l.Status == BindingStatus.Active,
            Builders<MemberLink>.Update
                .Set(l => l.Status, BindingStatus.Revoked)
                .Set(l => l.RevokedBy, actor)
                .Set(l => l.RevokedAt, DateTimeOffset.UtcNow),
            cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<bool> RevokeProviderLinkAsync(string tenantId, string id, string actor, CancellationToken ct = default)
    {
        await ReadyAsync();
        var result = await _providers.UpdateOneAsync(
            l => l.Id == id && l.TenantId == tenantId && l.Status == BindingStatus.Active,
            Builders<ProviderUserLink>.Update
                .Set(l => l.Status, BindingStatus.Revoked)
                .Set(l => l.RevokedBy, actor)
                .Set(l => l.RevokedAt, DateTimeOffset.UtcNow),
            cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task CreateEnrolmentAsync(Enrolment enrolment, CancellationToken ct = default)
    {
        await ReadyAsync();
        await _enrolments.InsertOneAsync(enrolment, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<Enrolment>> ListEnrolmentsAsync(string tenantId, string kind, CancellationToken ct = default)
    {
        await ReadyAsync();
        return await _enrolments.Find(e => e.TenantId == tenantId && e.Kind == kind)
            .SortBy(e => e.CreatedAt).ToListAsync(ct);
    }

    public async Task<bool> CancelEnrolmentAsync(string tenantId, string kind, string id, string actor, CancellationToken ct = default)
    {
        await ReadyAsync();
        var result = await _enrolments.UpdateOneAsync(
            e => e.Id == id && e.TenantId == tenantId && e.Kind == kind && e.Status == EnrolmentStatus.Pending,
            Builders<Enrolment>.Update
                .Set(e => e.Status, EnrolmentStatus.Cancelled)
                .Set(e => e.CancelledBy, actor),
            cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<(BindingWriteResult Result, Enrolment Enrolment)?> RedeemEnrolmentAsync(
        string codeHash, SmartIdentity identity, CancellationToken ct = default)
    {
        await ReadyAsync();
        var now = DateTimeOffset.UtcNow;

        var enrolment = await _enrolments.Find(e => e.CodeHash == codeHash && e.Status == EnrolmentStatus.Pending)
            .FirstOrDefaultAsync(ct);
        if (enrolment == null || enrolment.ExpiresAt <= now)
            return null;

        // Refuse before consuming the code, so a person who already has a link
        // does not burn a code they may need once the old link is revoked.
        var existing = enrolment.Kind == EnrolmentKind.Member
            ? await FindActiveMemberLinkAsync(identity, ct) is not null
            : await FindActiveProviderLinkAsync(identity, ct) is not null;
        if (existing)
            return (BindingWriteResult.AlreadyBound, enrolment);

        // Claim the code. Only one concurrent redemption can win this update.
        var claimed = await _enrolments.FindOneAndUpdateAsync(
            e => e.Id == enrolment.Id && e.Status == EnrolmentStatus.Pending,
            Builders<Enrolment>.Update
                .Set(e => e.Status, EnrolmentStatus.Redeemed)
                .Set(e => e.RedeemedBy, identity.ToString())
                .Set(e => e.RedeemedAt, now),
            new FindOneAndUpdateOptions<Enrolment> { ReturnDocument = ReturnDocument.After },
            ct);
        if (claimed == null)
            return null;

        try
        {
            if (claimed.Kind == EnrolmentKind.Member)
            {
                await _members.InsertOneAsync(new MemberLink
                {
                    Id = NewId(),
                    TenantId = claimed.TenantId,
                    Issuer = identity.Issuer,
                    Subject = identity.Subject,
                    MemberId = claimed.MemberId!,
                    EnrolmentId = claimed.Id,
                    CreatedBy = claimed.CreatedBy,
                    CreatedAt = now,
                }, cancellationToken: ct);
            }
            else
            {
                await _providers.InsertOneAsync(new ProviderUserLink
                {
                    Id = NewId(),
                    TenantId = claimed.TenantId,
                    Issuer = identity.Issuer,
                    Subject = identity.Subject,
                    ProviderId = claimed.ProviderId!,
                    Npi = claimed.Npi!,
                    EnrolmentId = claimed.Id,
                    CreatedBy = claimed.CreatedBy,
                    CreatedAt = now,
                }, cancellationToken: ct);
            }
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Lost a race with another redemption for the same identity: give
            // the code back rather than leave it consumed with nothing bound.
            await _enrolments.UpdateOneAsync(
                e => e.Id == claimed.Id,
                Builders<Enrolment>.Update
                    .Set(e => e.Status, EnrolmentStatus.Pending)
                    .Set(e => e.RedeemedBy, null)
                    .Set(e => e.RedeemedAt, null),
                cancellationToken: ct);
            return (BindingWriteResult.AlreadyBound, enrolment);
        }

        return (BindingWriteResult.Created, claimed);
    }

    public async Task<ClientTenantRegistration?> FindClientAsync(string clientId, CancellationToken ct = default)
    {
        await ReadyAsync();
        return await _clients.Find(c => c.ClientId == clientId).FirstOrDefaultAsync(ct);
    }

    public async Task<BindingWriteResult> CreateClientAsync(ClientTenantRegistration registration, CancellationToken ct = default)
    {
        await ReadyAsync();
        try
        {
            await _clients.InsertOneAsync(registration, cancellationToken: ct);
            return BindingWriteResult.Created;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return BindingWriteResult.AlreadyBound;
        }
    }

    public async Task<IReadOnlyList<ClientTenantRegistration>> ListClientsAsync(string tenantId, CancellationToken ct = default)
    {
        await ReadyAsync();
        return await _clients.Find(c => c.TenantId == tenantId).SortBy(c => c.CreatedAt).ToListAsync(ct);
    }

    public async Task<bool> DeleteClientAsync(string tenantId, string clientId, CancellationToken ct = default)
    {
        await ReadyAsync();
        var result = await _clients.DeleteOneAsync(c => c.ClientId == clientId && c.TenantId == tenantId, ct);
        return result.DeletedCount == 1;
    }

    public async Task SeedDevelopmentBindingsAsync(
        IEnumerable<MemberLink> members, IEnumerable<ProviderUserLink> providers,
        IEnumerable<ClientTenantRegistration> clients, CancellationToken ct = default)
    {
        await ReadyAsync();
        foreach (var member in members)
        {
            if (await FindActiveMemberLinkAsync(new SmartIdentity(member.Issuer, member.Subject), ct) is null)
                await _members.InsertOneAsync(member, cancellationToken: ct);
        }

        foreach (var provider in providers)
        {
            if (await FindActiveProviderLinkAsync(new SmartIdentity(provider.Issuer, provider.Subject), ct) is null)
                await _providers.InsertOneAsync(provider, cancellationToken: ct);
        }

        foreach (var client in clients)
            await CreateClientAsync(client, ct);
    }

    internal static string NewId() => Guid.NewGuid().ToString("N");
}
