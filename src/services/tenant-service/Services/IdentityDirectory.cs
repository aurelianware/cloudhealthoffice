using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;
using TenantService.Models;

namespace TenantService.Services;

/// <summary>
/// Read model for token-service: who an Entra identity is in CHO, and whether a
/// tenant is open for sign-in. Purpose-built queries only; nothing here lists a
/// tenant's users.
/// </summary>
public interface IIdentityDirectory
{
    /// <summary>
    /// TenantUsers linked to Entra object <paramref name="oid"/> whose recorded
    /// directory is <paramref name="tid"/> or not yet recorded. The caller
    /// decides whether an unrecorded directory is acceptable.
    /// </summary>
    Task<IReadOnlyList<IdentityMembership>> GetMembershipsAsync(string tid, string oid, CancellationToken ct);

    Task<IdentityTenant?> GetTenantAsync(string tenantId, CancellationToken ct);

    /// <summary>All tenants, or those registered to one Entra directory.</summary>
    Task<IReadOnlyList<IdentityTenant>> GetTenantsAsync(string? azureTenantId, CancellationToken ct);

    Task<IdentityUser?> FindUserByEmailAsync(string tenantId, string email, CancellationToken ct);

    /// <summary>
    /// Records <paramref name="oid"/>/<paramref name="tid"/> on a user that has no
    /// link yet (or has this oid without a recorded directory). Never overwrites a
    /// different link: returns <see cref="LinkOutcome.Conflict"/> instead.
    /// </summary>
    Task<(LinkOutcome Outcome, IdentityUser? User)> LinkEntraIdentityAsync(
        string tenantId, string userId, string oid, string tid, CancellationToken ct);
}

public enum LinkOutcome { Linked, NotFound, Conflict }

public sealed class IdentityMembership
{
    [JsonPropertyName("user")] public IdentityUser User { get; set; } = new();
    [JsonPropertyName("tenant")] public IdentityTenant? Tenant { get; set; }
}

public sealed class IdentityUser
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("firstName")] public string FirstName { get; set; } = string.Empty;
    [JsonPropertyName("lastName")] public string LastName { get; set; } = string.Empty;
    [JsonPropertyName("department")] public string Department { get; set; } = string.Empty;
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = new();
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("azureAdObjectId")] public string AzureAdObjectId { get; set; } = string.Empty;
    [JsonPropertyName("azureAdTenantId")] public string AzureAdTenantId { get; set; } = string.Empty;

    public static IdentityUser From(TenantUser u) => new()
    {
        Id = u.Id,
        TenantId = u.TenantId,
        Email = u.Email,
        DisplayName = u.DisplayName,
        FirstName = u.FirstName,
        LastName = u.LastName,
        Department = u.Department,
        Roles = u.Roles ?? new List<string>(),
        Status = u.Status,
        AzureAdObjectId = u.AzureAdObjectId ?? string.Empty,
        AzureAdTenantId = u.AzureAdTenantId ?? string.Empty,
    };
}

public sealed class IdentityTenant
{
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("tenantName")] public string TenantName { get; set; } = string.Empty;

    /// <summary>The Entra directory registered to this tenant, if any.</summary>
    [JsonPropertyName("azureTenantId")] public string AzureTenantId { get; set; } = string.Empty;

    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;

    /// <summary>Whether users may sign in to this tenant. See <see cref="IdentityDirectory.IsActive"/>.</summary>
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
}

public sealed class IdentityDirectory : IIdentityDirectory
{
    private readonly IMongoCollection<TenantUser> _users;

    // Read untyped: the Tenants collection holds both tenant-service's Tenant
    // documents and the portal's TenantSubscription documents (which carry
    // azureTenantId and subscriptionStatus), and the typed model can read neither
    // the other's shape nor fields it does not declare.
    private readonly IMongoCollection<BsonDocument> _tenants;

    public IdentityDirectory(IMongoDatabase database)
    {
        _users = database.GetCollection<TenantUser>("TenantUsers");
        _tenants = database.GetCollection<BsonDocument>("Tenants");
    }

    public async Task<IReadOnlyList<IdentityMembership>> GetMembershipsAsync(string tid, string oid, CancellationToken ct)
    {
        var f = Builders<TenantUser>.Filter;
        var filter = f.And(
            f.Eq(u => u.AzureAdObjectId, oid),
            f.In(u => u.AzureAdTenantId, new[] { tid, string.Empty, null }));
        var users = await _users.Find(filter).Limit(200).ToListAsync(ct);

        var tenantIds = users.Select(u => u.TenantId).Distinct().ToList();
        var tenants = (await LoadTenantsAsync(tenantIds, ct)).ToDictionary(t => t.TenantId);

        return users.Select(u => new IdentityMembership
        {
            User = IdentityUser.From(u),
            Tenant = tenants.GetValueOrDefault(u.TenantId),
        }).ToList();
    }

    public async Task<IdentityTenant?> GetTenantAsync(string tenantId, CancellationToken ct)
    {
        return (await LoadTenantsAsync(new[] { tenantId }, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<IdentityTenant>> GetTenantsAsync(string? azureTenantId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(azureTenantId))
        {
            var all = await _tenants.Find(Builders<BsonDocument>.Filter.Empty).Limit(5000).ToListAsync(ct);
            return CombineByTenant(all);
        }

        // Match on any record of the tenant, then judge it on all of its records.
        var matching = await _tenants.Find(Builders<BsonDocument>.Filter.Eq("azureTenantId", azureTenantId))
            .Limit(5000).ToListAsync(ct);
        var ids = matching.Select(d => Str(d, "tenantId")).OfType<string>().Distinct().ToList();
        return (await LoadTenantsAsync(ids, ct))
            .Where(t => string.Equals(t.AzureTenantId, azureTenantId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<IReadOnlyList<IdentityTenant>> LoadTenantsAsync(IReadOnlyCollection<string> tenantIds, CancellationToken ct)
    {
        if (tenantIds.Count == 0)
            return Array.Empty<IdentityTenant>();
        var docs = await _tenants.Find(Builders<BsonDocument>.Filter.In("tenantId", tenantIds)).ToListAsync(ct);
        return CombineByTenant(docs);
    }

    private static IReadOnlyList<IdentityTenant> CombineByTenant(IEnumerable<BsonDocument> docs)
        => docs.GroupBy(d => Str(d, "tenantId"))
            .Where(g => g.Key != null)
            .Select(g => Combine(g.ToList()))
            .ToList();

    /// <summary>
    /// One tenant can have several records in the Tenants collection
    /// (tenant-service's Tenant and the portal's TenantSubscription). The
    /// tenant is active only if every record says so, and it has a directory
    /// only if its records agree on one. Taking the first record would let a
    /// suspended Tenant hide behind an Active subscription.
    /// </summary>
    public static IdentityTenant Combine(IReadOnlyList<BsonDocument> records)
    {
        var parts = records.Select(ToTenant).ToList();
        var directories = parts.Select(p => p.AzureTenantId)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var conflict = directories.Count > 1;

        return new IdentityTenant
        {
            TenantId = parts[0].TenantId,
            TenantName = parts.Select(p => p.TenantName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? string.Empty,
            AzureTenantId = conflict ? string.Empty : directories.SingleOrDefault() ?? string.Empty,
            Status = conflict ? "conflict" : string.Join(",", parts.Select(p => p.Status).Where(x => x.Length > 0).Distinct()),
            IsActive = !conflict && parts.All(p => p.IsActive),
        };
    }

    public async Task<IdentityUser?> FindUserByEmailAsync(string tenantId, string email, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        // Two records with one address: refuse rather than pick one to link.
        var users = await _users.Find(u => u.TenantId == tenantId && u.EmailNormalized == normalized)
            .Limit(2).ToListAsync(ct);
        return users.Count == 1 ? IdentityUser.From(users[0]) : null;
    }

    public async Task<(LinkOutcome Outcome, IdentityUser? User)> LinkEntraIdentityAsync(
        string tenantId, string userId, string oid, string tid, CancellationToken ct)
    {
        var f = Builders<TenantUser>.Filter;
        // Conditional on the current state, so two concurrent first logins (or an
        // admin edit racing a login) can never replace one link with another.
        var filter = f.And(
            f.Eq(u => u.Id, userId),
            f.Eq(u => u.TenantId, tenantId),
            f.Or(
                f.In(u => u.AzureAdObjectId, new[] { string.Empty, null }),
                f.And(
                    f.Eq(u => u.AzureAdObjectId, oid),
                    f.In(u => u.AzureAdTenantId, new[] { string.Empty, null, tid }))));
        var update = Builders<TenantUser>.Update
            .Set(u => u.AzureAdObjectId, oid)
            .Set(u => u.AzureAdTenantId, tid)
            .Set(u => u.UpdatedAt, DateTime.UtcNow);

        var updated = await _users.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<TenantUser> { ReturnDocument = ReturnDocument.After }, ct);
        if (updated != null)
            return (LinkOutcome.Linked, IdentityUser.From(updated));

        var exists = await _users.Find(u => u.Id == userId && u.TenantId == tenantId).AnyAsync(ct);
        return (exists ? LinkOutcome.Conflict : LinkOutcome.NotFound, null);
    }

    /// <summary>
    /// A tenant is open for sign-in when every status field it carries says so:
    /// tenant-service's <c>status</c> must be <c>active</c>, and the portal's
    /// <c>subscriptionStatus</c> must be <c>Active</c> or <c>Trial</c>. A document
    /// with neither field is not active. Pending, suspended, terminated, expired
    /// and cancelled tenants are closed.
    /// </summary>
    public static bool IsActive(string? status, string? subscriptionStatus)
    {
        if (status == null && subscriptionStatus == null)
            return false;
        if (status != null && !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            return false;
        if (subscriptionStatus != null
            && !string.Equals(subscriptionStatus, "Active", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(subscriptionStatus, "Trial", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static IdentityTenant ToTenant(BsonDocument d)
    {
        var status = Str(d, "status");
        var subscriptionStatus = Str(d, "subscriptionStatus");
        return new IdentityTenant
        {
            TenantId = Str(d, "tenantId") ?? string.Empty,
            TenantName = Str(d, "tenantName") ?? Str(d, "organizationName") ?? string.Empty,
            AzureTenantId = Str(d, "azureTenantId") ?? string.Empty,
            Status = status ?? subscriptionStatus ?? string.Empty,
            IsActive = IsActive(status, subscriptionStatus),
        };
    }

    private static string? Str(BsonDocument d, string name)
        => d.TryGetValue(name, out var v) && v.IsString && !string.IsNullOrEmpty(v.AsString) ? v.AsString : null;
}
