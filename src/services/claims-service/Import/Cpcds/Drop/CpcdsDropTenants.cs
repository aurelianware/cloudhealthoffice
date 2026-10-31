using System.Text.RegularExpressions;
using ClaimsService.Models;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ClaimsService.Import.Cpcds.Drop;

/// <summary>
/// A tenant's CPCDS drop settings as the tenant saved them (the portal's
/// setup page, or onboarding), rather than as deployment configuration.
/// Stored settings let a plan that signs up turn its drop on without a
/// redeploy. A stored record for a tenant replaces that tenant's entry in
/// <see cref="CpcdsDropOptions.Tenants"/>, including to turn it off.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class CpcdsDropTenantSettings
{
    [BsonId]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>False keeps the record (and its settings) but stops loading the tenant's drop.</summary>
    public bool Enabled { get; set; }

    public CpcdsDropTenantOptions Settings { get; set; } = new();

    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

public interface ICpcdsDropTenantSettingsRepository
{
    Task<CpcdsDropTenantSettings?> GetAsync(string tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<CpcdsDropTenantSettings>> ListAsync(CancellationToken ct = default);
    Task SaveAsync(CpcdsDropTenantSettings settings, CancellationToken ct = default);
}

public sealed class CpcdsDropTenantSettingsRepositoryMongo : ICpcdsDropTenantSettingsRepository
{
    public const string CollectionName = "CpcdsDropTenantSettings";
    private readonly IMongoCollection<CpcdsDropTenantSettings> _settings;

    public CpcdsDropTenantSettingsRepositoryMongo(IMongoDatabase database)
        => _settings = database.GetCollection<CpcdsDropTenantSettings>(CollectionName);

    public async Task<CpcdsDropTenantSettings?> GetAsync(string tenantId, CancellationToken ct = default)
        => await _settings.Find(s => s.TenantId == tenantId).FirstOrDefaultAsync(ct);

    /// <summary>One small record per tenant that ever saved drop settings.</summary>
    public async Task<IReadOnlyList<CpcdsDropTenantSettings>> ListAsync(CancellationToken ct = default)
        => await _settings.Find(FilterDefinition<CpcdsDropTenantSettings>.Empty).ToListAsync(ct);

    public Task SaveAsync(CpcdsDropTenantSettings settings, CancellationToken ct = default)
        => _settings.ReplaceOneAsync(s => s.TenantId == settings.TenantId, settings,
            new ReplaceOptions { IsUpsert = true }, ct);
}

public sealed class InMemoryCpcdsDropTenantSettingsRepository : ICpcdsDropTenantSettingsRepository
{
    private readonly Dictionary<string, CpcdsDropTenantSettings> _settings = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public Task<CpcdsDropTenantSettings?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        lock (_sync) return Task.FromResult(_settings.GetValueOrDefault(tenantId));
    }

    public Task<IReadOnlyList<CpcdsDropTenantSettings>> ListAsync(CancellationToken ct = default)
    {
        lock (_sync) return Task.FromResult<IReadOnlyList<CpcdsDropTenantSettings>>(_settings.Values.ToList());
    }

    public Task SaveAsync(CpcdsDropTenantSettings settings, CancellationToken ct = default)
    {
        lock (_sync) _settings[settings.TenantId] = settings;
        return Task.CompletedTask;
    }
}

/// <summary>Where a tenant's effective drop settings came from.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum CpcdsDropSettingsSource
{
    /// <summary>Neither configured nor saved: the tenant has no drop.</summary>
    None,

    /// <summary><see cref="CpcdsDropOptions.Tenants"/> in deployment configuration.</summary>
    Configuration,

    /// <summary>Saved by the tenant (<see cref="CpcdsDropTenantSettings"/>).</summary>
    Stored,
}

/// <summary>
/// The tenants whose drops load, and each one's settings: stored settings
/// where a tenant saved them, deployment configuration otherwise. Nothing
/// loads while <see cref="CpcdsDropOptions.Enabled"/> is off, whatever a
/// tenant saved: that switch is the deployment's.
/// </summary>
public sealed class CpcdsDropTenants
{
    /// <summary>A tenant id that is also a safe folder name (see <see cref="FolderCpcdsDropStore"/>).</summary>
    internal static readonly Regex TenantIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);

    private readonly CpcdsDropOptions _options;
    private readonly ICpcdsDropTenantSettingsRepository _stored;

    public CpcdsDropTenants(IOptions<CpcdsDropOptions> options, ICpcdsDropTenantSettingsRepository stored)
    {
        _options = options.Value;
        _stored = stored;
    }

    public bool DropEnabled => _options.Enabled;

    /// <summary>The tenant's settings and where they came from, whether or not the drop is on.</summary>
    public async Task<(CpcdsDropTenantOptions? Settings, bool Enabled, CpcdsDropSettingsSource Source)> DescribeAsync(
        string tenantId, CancellationToken ct = default)
    {
        if (await _stored.GetAsync(tenantId, ct) is { } stored)
            return (stored.Settings, stored.Enabled, CpcdsDropSettingsSource.Stored);
        if (_options.Tenants.TryGetValue(tenantId, out var configured))
            return (configured, true, CpcdsDropSettingsSource.Configuration);
        return (null, false, CpcdsDropSettingsSource.None);
    }

    /// <summary>The tenant's settings when its drop loads; null when it does not.</summary>
    public async Task<CpcdsDropTenantOptions?> ResolveAsync(string tenantId, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return null;
        var (settings, enabled, _) = await DescribeAsync(tenantId, ct);
        return enabled ? settings : null;
    }

    /// <summary>Every tenant whose drop loads, ordered by id.</summary>
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return [];

        var stored = await _stored.ListAsync(ct);
        var storedIds = stored.Select(s => s.TenantId).ToHashSet(StringComparer.Ordinal);
        return _options.Tenants.Keys.Where(id => !storedIds.Contains(id))
            .Concat(stored.Where(s => s.Enabled).Select(s => s.TenantId))
            .Where(id => TenantIdPattern.IsMatch(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
