using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// A tenant's own choice to run shadow adjudication, saved from the portal.
/// A saved choice replaces what <see cref="ShadowAdjudicationOptions.Tenants"/>
/// says for that tenant, in either direction, so a plan can try shadow
/// adjudication without a redeploy. <see cref="ShadowAdjudicationOptions.Enabled"/>
/// stays the deployment's switch: while it is off, nothing runs.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ShadowAdjudicationTenantSettings
{
    [BsonId]
    public string TenantId { get; set; } = string.Empty;

    public bool Enabled { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

public interface IShadowAdjudicationTenantSettingsRepository
{
    Task<ShadowAdjudicationTenantSettings?> GetAsync(string tenantId, CancellationToken ct = default);
    Task SaveAsync(ShadowAdjudicationTenantSettings settings, CancellationToken ct = default);
}

public sealed class ShadowAdjudicationTenantSettingsRepositoryMongo : IShadowAdjudicationTenantSettingsRepository
{
    public const string CollectionName = "ShadowAdjudicationTenantSettings";
    private readonly IMongoCollection<ShadowAdjudicationTenantSettings> _settings;

    public ShadowAdjudicationTenantSettingsRepositoryMongo(IMongoDatabase database)
        => _settings = database.GetCollection<ShadowAdjudicationTenantSettings>(CollectionName);

    public async Task<ShadowAdjudicationTenantSettings?> GetAsync(string tenantId, CancellationToken ct = default)
        => await _settings.Find(s => s.TenantId == tenantId).FirstOrDefaultAsync(ct);

    public Task SaveAsync(ShadowAdjudicationTenantSettings settings, CancellationToken ct = default)
        => _settings.ReplaceOneAsync(s => s.TenantId == settings.TenantId, settings,
            new ReplaceOptions { IsUpsert = true }, ct);
}

public sealed class InMemoryShadowAdjudicationTenantSettingsRepository : IShadowAdjudicationTenantSettingsRepository
{
    private readonly Dictionary<string, ShadowAdjudicationTenantSettings> _settings = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public Task<ShadowAdjudicationTenantSettings?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        lock (_sync) return Task.FromResult(_settings.GetValueOrDefault(tenantId));
    }

    public Task SaveAsync(ShadowAdjudicationTenantSettings settings, CancellationToken ct = default)
    {
        lock (_sync) _settings[settings.TenantId] = settings;
        return Task.CompletedTask;
    }
}

/// <summary>Where a tenant's shadow on/off answer came from.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum ShadowAdjudicationSettingsSource
{
    /// <summary><see cref="ShadowAdjudicationOptions.Tenants"/> in deployment configuration.</summary>
    Configuration,

    /// <summary>Saved by the tenant (<see cref="ShadowAdjudicationTenantSettings"/>).</summary>
    Stored,
}

/// <summary>
/// Whether shadow adjudication runs for a tenant: the deployment's switch
/// (<see cref="ShadowAdjudicationOptions.Enabled"/>) and then the tenant's
/// saved choice, or, when it has saved none, deployment configuration.
/// </summary>
public sealed class ShadowAdjudicationTenants
{
    private readonly IShadowAdjudicationTenantSettingsRepository _stored;

    public ShadowAdjudicationTenants(
        IOptions<ShadowAdjudicationOptions> options, IShadowAdjudicationTenantSettingsRepository stored)
    {
        Options = options.Value;
        _stored = stored;
    }

    public ShadowAdjudicationOptions Options { get; }

    public async Task<bool> IsEnabledForAsync(string tenantId, CancellationToken ct = default)
        => (await DescribeAsync(tenantId, ct)).Running;

    public async Task<ShadowAdjudicationSettingsView> DescribeAsync(string tenantId, CancellationToken ct = default)
    {
        var stored = await _stored.GetAsync(tenantId, ct);
        var tenantOn = stored?.Enabled ?? ConfiguredFor(tenantId);
        return new ShadowAdjudicationSettingsView
        {
            TenantId = tenantId,
            EnabledForDeployment = Options.Enabled,
            Enabled = tenantOn,
            Running = Options.Enabled && tenantOn,
            Source = stored is null ? ShadowAdjudicationSettingsSource.Configuration : ShadowAdjudicationSettingsSource.Stored,
            MaxClaimsPerRun = Math.Max(1, Options.MaxClaimsPerRun),
            Tolerance = Options.Tolerance,
            UpdatedAt = stored?.UpdatedAt,
            UpdatedBy = stored?.UpdatedBy,
        };
    }

    public Task SaveAsync(string tenantId, bool enabled, string? actorId, DateTime nowUtc, CancellationToken ct = default)
        => _stored.SaveAsync(new ShadowAdjudicationTenantSettings
        {
            TenantId = tenantId,
            Enabled = enabled,
            UpdatedAt = nowUtc,
            UpdatedBy = actorId ?? string.Empty,
        }, ct);

    /// <summary>Configuration alone, as if the deployment switch were on: the tenant is listed, or nobody is.</summary>
    private bool ConfiguredFor(string tenantId) =>
        Options.Tenants.Count == 0 || Options.Tenants.Contains(tenantId, StringComparer.OrdinalIgnoreCase);
}

/// <summary>The tenant's shadow switch as <c>GET .../shadow-adjudication/settings</c> describes it.</summary>
public sealed class ShadowAdjudicationSettingsView
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Whether this deployment runs shadow adjudication at all (<c>Adjudication:Shadow:Enabled</c>).</summary>
    public bool EnabledForDeployment { get; set; }

    /// <summary>Whether the tenant has it on (its saved choice, or configuration).</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether runs are accepted now: the deployment and the tenant both on.</summary>
    public bool Running { get; set; }

    public ShadowAdjudicationSettingsSource Source { get; set; }
    public int MaxClaimsPerRun { get; set; }
    public decimal Tolerance { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Body of <c>PUT .../shadow-adjudication/settings</c>.</summary>
public sealed class ShadowAdjudicationSettingsRequest
{
    public bool Enabled { get; set; }
}
