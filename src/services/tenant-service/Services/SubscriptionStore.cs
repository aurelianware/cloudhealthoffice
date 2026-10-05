using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace TenantService.Services;

/// <summary>
/// The portal's subscription records (TenantSubscription: Entra directory,
/// subscription status, tier, admin emails, Stripe ids). They live in the
/// <c>Tenants</c> collection beside tenant-service's own Tenant documents (see
/// <see cref="IdentityDirectory"/>), with camelCase field names. tenant-service
/// is now the only writer: the portal's PlatformTenants page goes through the
/// platform endpoints (platform:tenants) and self-service signup through
/// token-service and <c>POST /internal/v1/identity/signups</c>.
/// </summary>
public interface ISubscriptionStore
{
    Task<IReadOnlyList<SubscriptionRecord>> ListAsync(CancellationToken ct);
    Task<SubscriptionRecord> CreateAsync(SubscriptionWrite write, CancellationToken ct);
    Task<bool> UpdateAsync(string azureTenantId, SubscriptionWrite write, CancellationToken ct);
    Task<bool> SetStatusAsync(string azureTenantId, string status, CancellationToken ct);
    Task<bool> DeleteAsync(string azureTenantId, CancellationToken ct);

    /// <summary>
    /// Self-service signup for an Entra directory: a Trial subscription whose
    /// directory is the signed-in user's (never a caller-chosen one). Null when
    /// that directory already has a subscription.
    /// </summary>
    Task<SubscriptionRecord?> SignupAsync(SignupWrite signup, CancellationToken ct);
}

/// <summary>A subscription as the portal reads it.</summary>
public sealed class SubscriptionRecord
{
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("azureTenantId")] public string AzureTenantId { get; set; } = string.Empty;
    [JsonPropertyName("organizationName")] public string OrganizationName { get; set; } = string.Empty;
    [JsonPropertyName("subscriptionStatus")] public string SubscriptionStatus { get; set; } = string.Empty;
    [JsonPropertyName("tier")] public string Tier { get; set; } = string.Empty;
    [JsonPropertyName("isDemo")] public bool IsDemo { get; set; }
    [JsonPropertyName("stripeCustomerId")] public string? StripeCustomerId { get; set; }
    [JsonPropertyName("stripeSubscriptionId")] public string? StripeSubscriptionId { get; set; }
    [JsonPropertyName("trialEndsAt")] public DateTime? TrialEndsAt { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
    [JsonPropertyName("adminEmails")] public List<string> AdminEmails { get; set; } = new();
    [JsonPropertyName("notes")] public string? Notes { get; set; }
}

/// <summary>Fields a platform administrator sets (null: unchanged on update).</summary>
public sealed class SubscriptionWrite
{
    [JsonPropertyName("azureTenantId")] public string? AzureTenantId { get; set; }
    [JsonPropertyName("organizationName")] public string? OrganizationName { get; set; }
    [JsonPropertyName("subscriptionStatus")] public string? SubscriptionStatus { get; set; }
    [JsonPropertyName("tier")] public string? Tier { get; set; }
    [JsonPropertyName("isDemo")] public bool? IsDemo { get; set; }
    [JsonPropertyName("adminEmails")] public List<string>? AdminEmails { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("stripeCustomerId")] public string? StripeCustomerId { get; set; }
    [JsonPropertyName("stripeSubscriptionId")] public string? StripeSubscriptionId { get; set; }
}

/// <summary>
/// What a self-service signup may set. The directory and the admin address come
/// from the Entra token token-service validated; status (Trial), demo flag
/// (false), trial end and notes are fixed here.
/// </summary>
public sealed class SignupWrite
{
    public required string AzureTenantId { get; init; }
    public required string AdminEmail { get; init; }
    public required string OrganizationName { get; init; }
    public required string Tier { get; init; }
    public string? StripeCustomerId { get; init; }
    public string? StripeSubscriptionId { get; init; }
}

public static class SubscriptionRules
{
    public static readonly IReadOnlySet<string> Statuses =
        new HashSet<string>(StringComparer.Ordinal) { "Active", "Trial", "Expired", "Cancelled" };

    public static readonly IReadOnlySet<string> Tiers =
        new HashSet<string>(StringComparer.Ordinal) { "starter", "professional", "enterprise" };

    /// <summary>Self-service signup offers these tiers; enterprise is arranged with sales.</summary>
    public static readonly IReadOnlySet<string> SignupTiers =
        new HashSet<string>(StringComparer.Ordinal) { "starter", "professional" };

    private static readonly Regex StripeCustomer = new("^cus_[A-Za-z0-9]{6,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex StripeSubscription = new("^sub_[A-Za-z0-9]{6,64}$", RegexOptions.CultureInvariant);

    public static bool IsStripeCustomerId(string? value) => value is null || StripeCustomer.IsMatch(value);
    public static bool IsStripeSubscriptionId(string? value) => value is null || StripeSubscription.IsMatch(value);
}

public sealed class MongoSubscriptionStore : ISubscriptionStore
{
    private readonly IMongoCollection<BsonDocument> _tenants;

    public MongoSubscriptionStore(IMongoDatabase database)
    {
        _tenants = database.GetCollection<BsonDocument>("Tenants");
    }

    private static FilterDefinitionBuilder<BsonDocument> F => Builders<BsonDocument>.Filter;

    // A subscription record carries the portal's subscriptionStatus; tenant-service's
    // own Tenant documents do not, and are never touched here.
    private static FilterDefinition<BsonDocument> Subscriptions => F.Exists("subscriptionStatus");

    private static FilterDefinition<BsonDocument> ForDirectory(string azureTenantId)
        => Subscriptions & F.Eq("azureTenantId", azureTenantId);

    public async Task<IReadOnlyList<SubscriptionRecord>> ListAsync(CancellationToken ct)
    {
        var docs = await _tenants.Find(Subscriptions).Sort(Builders<BsonDocument>.Sort.Descending("createdAt")).ToListAsync(ct);
        return docs.Select(ToRecord).ToList();
    }

    public async Task<SubscriptionRecord> CreateAsync(SubscriptionWrite write, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var status = write.SubscriptionStatus ?? "Trial";
        var doc = new BsonDocument
        {
            ["tenantId"] = $"tenant-{Guid.NewGuid():N}",
            ["azureTenantId"] = write.AzureTenantId ?? string.Empty,
            ["organizationName"] = write.OrganizationName ?? string.Empty,
            ["subscriptionStatus"] = status,
            ["tier"] = write.Tier ?? "starter",
            ["isDemo"] = write.IsDemo ?? false,
            ["stripeCustomerId"] = (BsonValue?)write.StripeCustomerId ?? BsonNull.Value,
            ["stripeSubscriptionId"] = (BsonValue?)write.StripeSubscriptionId ?? BsonNull.Value,
            ["trialEndsAt"] = status == "Trial" ? (BsonValue)now.AddDays(14) : BsonNull.Value,
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["adminEmails"] = new BsonArray(write.AdminEmails ?? new List<string>()),
            ["notes"] = (BsonValue?)write.Notes ?? BsonNull.Value,
        };
        await _tenants.InsertOneAsync(doc, cancellationToken: ct);
        return ToRecord(doc);
    }

    public async Task<bool> UpdateAsync(string azureTenantId, SubscriptionWrite write, CancellationToken ct)
    {
        var u = Builders<BsonDocument>.Update;
        var updates = new List<UpdateDefinition<BsonDocument>> { u.Set("updatedAt", DateTime.UtcNow) };
        if (write.OrganizationName != null) updates.Add(u.Set("organizationName", write.OrganizationName));
        if (write.Tier != null) updates.Add(u.Set("tier", write.Tier));
        if (write.SubscriptionStatus != null) updates.Add(u.Set("subscriptionStatus", write.SubscriptionStatus));
        if (write.AdminEmails != null) updates.Add(u.Set("adminEmails", new BsonArray(write.AdminEmails)));
        if (write.IsDemo.HasValue) updates.Add(u.Set("isDemo", write.IsDemo.Value));
        updates.Add(u.Set("notes", (BsonValue?)write.Notes ?? BsonNull.Value));

        var result = await _tenants.UpdateOneAsync(ForDirectory(azureTenantId), u.Combine(updates), cancellationToken: ct);
        return result.MatchedCount > 0;
    }

    public async Task<bool> SetStatusAsync(string azureTenantId, string status, CancellationToken ct)
    {
        var result = await _tenants.UpdateOneAsync(ForDirectory(azureTenantId),
            Builders<BsonDocument>.Update.Set("subscriptionStatus", status).Set("updatedAt", DateTime.UtcNow),
            cancellationToken: ct);
        return result.MatchedCount > 0;
    }

    public async Task<bool> DeleteAsync(string azureTenantId, CancellationToken ct)
    {
        var result = await _tenants.DeleteOneAsync(ForDirectory(azureTenantId), ct);
        return result.DeletedCount > 0;
    }

    public async Task<SubscriptionRecord?> SignupAsync(SignupWrite signup, CancellationToken ct)
    {
        if (await _tenants.Find(ForDirectory(signup.AzureTenantId)).AnyAsync(ct))
            return null;

        return await CreateAsync(new SubscriptionWrite
        {
            AzureTenantId = signup.AzureTenantId,
            OrganizationName = signup.OrganizationName,
            SubscriptionStatus = "Trial",
            Tier = signup.Tier,
            IsDemo = false,
            AdminEmails = new List<string> { signup.AdminEmail },
            StripeCustomerId = signup.StripeCustomerId,
            StripeSubscriptionId = signup.StripeSubscriptionId,
        }, ct);
    }

    private static SubscriptionRecord ToRecord(BsonDocument d) => new()
    {
        TenantId = Str(d, "tenantId") ?? string.Empty,
        AzureTenantId = Str(d, "azureTenantId") ?? string.Empty,
        OrganizationName = Str(d, "organizationName") ?? string.Empty,
        SubscriptionStatus = Str(d, "subscriptionStatus") ?? string.Empty,
        Tier = Str(d, "tier") ?? string.Empty,
        IsDemo = d.TryGetValue("isDemo", out var demo) && demo.IsBoolean && demo.AsBoolean,
        StripeCustomerId = Str(d, "stripeCustomerId"),
        StripeSubscriptionId = Str(d, "stripeSubscriptionId"),
        TrialEndsAt = Date(d, "trialEndsAt"),
        CreatedAt = Date(d, "createdAt") ?? DateTime.MinValue,
        UpdatedAt = Date(d, "updatedAt") ?? DateTime.MinValue,
        AdminEmails = d.TryGetValue("adminEmails", out var emails) && emails.IsBsonArray
            ? emails.AsBsonArray.Where(e => e.IsString).Select(e => e.AsString).ToList()
            : new List<string>(),
        Notes = Str(d, "notes"),
    };

    private static string? Str(BsonDocument d, string name)
        => d.TryGetValue(name, out var v) && v.IsString ? v.AsString : null;

    private static DateTime? Date(BsonDocument d, string name)
        => d.TryGetValue(name, out var v) && v.IsValidDateTime ? v.ToUniversalTime() : null;
}

/// <summary>
/// Body of <c>POST /internal/v1/identity/signups</c> (token-service only).
/// <c>tid</c>, <c>oid</c> and <c>email</c> are from the Entra token token-service
/// validated; the rest is what the signup form chose.
/// </summary>
public sealed class SignupRequest
{
    public string Tid { get; set; } = string.Empty;
    public string Oid { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string OrganizationName { get; set; } = string.Empty;
    public string? Tier { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
}
