using System.Text.Json.Serialization;
using ClaimsService.Models;
using CloudHealthOffice.Infrastructure.Data;
using ClaimsService.Repositories;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ClaimsService.Extracts;

/// <summary>
/// A tenant's claims warehouse extract: whether it runs, the data-use
/// agreement it is delivered under, and how far it has got. The extract
/// carries PHI for the whole population, so it cannot be turned on without
/// naming the agreement (portal-reporting.md §6).
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ClaimExtractSettings
{
    [BsonId]
    public string TenantId { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>The plan's reference for the data-use agreement the files are delivered under.</summary>
    public string? DataUseAgreement { get; set; }

    /// <summary>
    /// Every claim version last updated at or before this instant has been
    /// delivered. The next run delivers what changed after it. Null until the
    /// first run, which delivers everything.
    /// </summary>
    public DateTime? Watermark { get; set; }

    public DateTime? LastSucceededAt { get; set; }

    /// <summary>A run in progress holds the tenant until this time, so two replicas never run it at once.</summary>
    public DateTime? LeaseUntil { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClaimExtractRunStatus
{
    Succeeded,
    Failed,
}

public sealed record ClaimExtractFile(string Name, long Rows, long Bytes, string Sha256, string Location);

/// <summary>
/// One delivery, kept as the record of what PHI left CHO for the plan's
/// warehouse, when, under which agreement, and who asked for it.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ClaimExtractRun
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary><c>schedule</c>, or the user who started it.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>Everything from the beginning, rather than what changed since the last run.</summary>
    public bool Full { get; set; }

    /// <summary>Claim versions last updated after this instant (null: from the beginning)...</summary>
    public DateTime? WindowFrom { get; set; }

    /// <summary>...and at or before this one.</summary>
    public DateTime WindowTo { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    [BsonRepresentation(MongoDB.Bson.BsonType.String)]
    public ClaimExtractRunStatus Status { get; set; }

    public string? DataUseAgreement { get; set; }
    public long ClaimVersions { get; set; }
    public long Lines { get; set; }
    public List<ClaimExtractFile> Files { get; set; } = [];
    public string? Error { get; set; }
}

public interface IClaimExtractStore
{
    Task<ClaimExtractSettings?> GetSettingsAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Saves whether the extract runs and its agreement, keeping its progress.</summary>
    Task<ClaimExtractSettings> SaveSettingsAsync(string tenantId, bool enabled, string? dataUseAgreement, string updatedBy, DateTime now, CancellationToken ct = default);

    Task<IReadOnlyList<ClaimExtractSettings>> ListEnabledAsync(CancellationToken ct = default);

    /// <summary>Takes the tenant for a run until <paramref name="until"/>; null when another run holds it.</summary>
    Task<ClaimExtractSettings?> TryLeaseAsync(string tenantId, string owner, DateTime now, DateTime until, CancellationToken ct = default);

    /// <summary>Releases the lease, and on success moves the watermark.</summary>
    Task ReleaseAsync(string tenantId, string owner, DateTime? newWatermark, DateTime? succeededAt, CancellationToken ct = default);

    Task SaveRunAsync(ClaimExtractRun run, CancellationToken ct = default);

    Task<IReadOnlyList<ClaimExtractRun>> ListRunsAsync(string tenantId, int limit, CancellationToken ct = default);
}

public sealed class ClaimExtractStoreMongo : IClaimExtractStore
{
    public const string SettingsCollection = "ClaimExtractSettings";
    public const string RunsCollection = "ClaimExtractRuns";

    private readonly IMongoCollection<ClaimExtractSettings> _settings;
    private readonly IMongoCollection<ClaimExtractRun> _runs;

    public ClaimExtractStoreMongo(IMongoDatabase database)
    {
        _settings = database.GetCollection<ClaimExtractSettings>(SettingsCollection);
        _runs = database.GetCollection<ClaimExtractRun>(RunsCollection);
    }

    public async Task<ClaimExtractSettings?> GetSettingsAsync(string tenantId, CancellationToken ct = default)
        => await _settings.Find(s => s.TenantId == tenantId).FirstOrDefaultAsync(ct);

    public async Task<ClaimExtractSettings> SaveSettingsAsync(
        string tenantId, bool enabled, string? dataUseAgreement, string updatedBy, DateTime now, CancellationToken ct = default)
    {
        var update = Builders<ClaimExtractSettings>.Update
            .Set(s => s.Enabled, enabled)
            .Set(s => s.DataUseAgreement, dataUseAgreement)
            .Set(s => s.UpdatedAt, now)
            .Set(s => s.UpdatedBy, updatedBy);
        return await _settings.FindOneAndUpdateAsync(
            s => s.TenantId == tenantId, update,
            new FindOneAndUpdateOptions<ClaimExtractSettings> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
    }

    [CrossTenantQuery("ClaimExtractWorker (background): lists the tenants that turned the extract on; each tenant's run is scoped to its TenantId.")]
    public async Task<IReadOnlyList<ClaimExtractSettings>> ListEnabledAsync(CancellationToken ct = default)
        => await _settings.Find(s => s.Enabled).ToListAsync(ct);

    public async Task<ClaimExtractSettings?> TryLeaseAsync(
        string tenantId, string owner, DateTime now, DateTime until, CancellationToken ct = default)
    {
        var b = Builders<ClaimExtractSettings>.Filter;
        var free = b.And(
            b.Eq(s => s.TenantId, tenantId),
            b.Or(b.Eq(s => s.LeaseUntil, null), b.Lt(s => s.LeaseUntil, now)));
        var update = Builders<ClaimExtractSettings>.Update.Set(s => s.LeaseUntil, until).Set(s => s.LeaseOwner, owner);
        return await _settings.FindOneAndUpdateAsync(free, update,
            new FindOneAndUpdateOptions<ClaimExtractSettings> { ReturnDocument = ReturnDocument.After }, ct);
    }

    public async Task ReleaseAsync(string tenantId, string owner, DateTime? newWatermark, DateTime? succeededAt, CancellationToken ct = default)
    {
        var update = Builders<ClaimExtractSettings>.Update.Set(s => s.LeaseUntil, null).Set(s => s.LeaseOwner, null);
        if (newWatermark is not null) update = update.Set(s => s.Watermark, newWatermark);
        if (succeededAt is not null) update = update.Set(s => s.LastSucceededAt, succeededAt);
        // Only the run that holds the lease moves the watermark.
        await _settings.UpdateOneAsync(s => s.TenantId == tenantId && s.LeaseOwner == owner, update, cancellationToken: ct);
    }

    public Task SaveRunAsync(ClaimExtractRun run, CancellationToken ct = default)
        => _runs.ReplaceOneAsync(r => r.TenantId == run.TenantId && r.Id == run.Id, run, new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<IReadOnlyList<ClaimExtractRun>> ListRunsAsync(string tenantId, int limit, CancellationToken ct = default)
        => await _runs.Find(r => r.TenantId == tenantId).SortByDescending(r => r.StartedAt).Limit(limit).ToListAsync(ct);
}

public sealed class InMemoryClaimExtractStore : IClaimExtractStore
{
    private readonly Dictionary<string, ClaimExtractSettings> _settings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClaimExtractRun> _runs = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public Task<ClaimExtractSettings?> GetSettingsAsync(string tenantId, CancellationToken ct = default)
    {
        lock (_sync) return Task.FromResult(_settings.TryGetValue(tenantId, out var s) ? Copy(s) : null);
    }

    public Task<ClaimExtractSettings> SaveSettingsAsync(
        string tenantId, bool enabled, string? dataUseAgreement, string updatedBy, DateTime now, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_settings.TryGetValue(tenantId, out var s))
                _settings[tenantId] = s = new ClaimExtractSettings { TenantId = tenantId };
            s.Enabled = enabled;
            s.DataUseAgreement = dataUseAgreement;
            s.UpdatedAt = now;
            s.UpdatedBy = updatedBy;
            return Task.FromResult(Copy(s));
        }
    }

    public Task<IReadOnlyList<ClaimExtractSettings>> ListEnabledAsync(CancellationToken ct = default)
    {
        lock (_sync) return Task.FromResult<IReadOnlyList<ClaimExtractSettings>>(_settings.Values.Where(s => s.Enabled).Select(Copy).ToList());
    }

    public Task<ClaimExtractSettings?> TryLeaseAsync(string tenantId, string owner, DateTime now, DateTime until, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_settings.TryGetValue(tenantId, out var s) || (s.LeaseUntil is { } held && held >= now))
                return Task.FromResult<ClaimExtractSettings?>(null);
            s.LeaseUntil = until;
            s.LeaseOwner = owner;
            return Task.FromResult<ClaimExtractSettings?>(Copy(s));
        }
    }

    public Task ReleaseAsync(string tenantId, string owner, DateTime? newWatermark, DateTime? succeededAt, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_settings.TryGetValue(tenantId, out var s) && s.LeaseOwner == owner)
            {
                s.LeaseUntil = null;
                s.LeaseOwner = null;
                if (newWatermark is not null) s.Watermark = newWatermark;
                if (succeededAt is not null) s.LastSucceededAt = succeededAt;
            }
        }
        return Task.CompletedTask;
    }

    public Task SaveRunAsync(ClaimExtractRun run, CancellationToken ct = default)
    {
        lock (_sync) _runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClaimExtractRun>> ListRunsAsync(string tenantId, int limit, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<ClaimExtractRun>>(
                _runs.Values.Where(r => r.TenantId == tenantId).OrderByDescending(r => r.StartedAt).Take(limit).ToList());
    }

    private static ClaimExtractSettings Copy(ClaimExtractSettings s) => new()
    {
        TenantId = s.TenantId, Enabled = s.Enabled, DataUseAgreement = s.DataUseAgreement, Watermark = s.Watermark,
        LastSucceededAt = s.LastSucceededAt, LeaseUntil = s.LeaseUntil, LeaseOwner = s.LeaseOwner,
        UpdatedAt = s.UpdatedAt, UpdatedBy = s.UpdatedBy,
    };
}

/// <summary>
/// The claim versions an extract delivers: every version that is not a
/// draft, last updated in the window, oldest change first, read a page at a
/// time with an explicit tenant (the extract runs outside any request).
/// </summary>
public interface IClaimExtractSource
{
    /// <summary>
    /// Up to <paramref name="limit"/> versions with LastUpdatedDate in
    /// (<paramref name="from"/>, <paramref name="to"/>], ordered by
    /// (LastUpdatedDate, Id), after <paramref name="after"/> in that order.
    /// </summary>
    Task<IReadOnlyList<Claim>> ReadAsync(
        string tenantId, DateTime? from, DateTime to, (DateTime LastUpdated, string Id)? after, int limit, CancellationToken ct);
}

public sealed class ClaimExtractSourceMongo : IClaimExtractSource
{
    private readonly IMongoCollection<Claim> _claims;

    public ClaimExtractSourceMongo(IMongoDatabase database) => _claims = database.GetCollection<Claim>("Claims");

    public async Task<IReadOnlyList<Claim>> ReadAsync(
        string tenantId, DateTime? from, DateTime to, (DateTime LastUpdated, string Id)? after, int limit, CancellationToken ct)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.And(
            b.Eq(c => c.TenantId, tenantId),
            b.Ne(c => c.VersionState, ClaimVersionState.Draft),
            b.Lte(c => c.LastUpdatedDate, to));
        if (from is { } f)
            filter &= b.Gt(c => c.LastUpdatedDate, f);
        if (after is { } a)
            filter &= b.Or(
                b.Gt(c => c.LastUpdatedDate, a.LastUpdated),
                b.And(b.Eq(c => c.LastUpdatedDate, a.LastUpdated), b.Gt(c => c.Id, a.Id)));

        var page = await _claims.Find(filter)
            .Sort(Builders<Claim>.Sort.Ascending(c => c.LastUpdatedDate).Ascending(c => c.Id))
            .Limit(limit)
            .ToListAsync(ct);
        return page.Select(ClaimExtractVersions.Normalize).Where(c => c.VersionState != ClaimVersionState.Draft).ToList();
    }
}

public static class ClaimExtractVersions
{
    /// <summary>Fills the version fields of a claim stored before claims had versions, as the claim repository does.</summary>
    public static Claim Normalize(Claim claim)
    {
        if (string.IsNullOrEmpty(claim.ClaimVersionId)) claim.ClaimVersionId = claim.Id;
        if (claim.VersionNumber == 0) claim.VersionNumber = 1;
        if (claim.VersionState == ClaimVersionState.Unknown)
            claim.VersionState = ClaimRepository.MapStatusToVersionState(claim.Status);
        return claim;
    }
}

/// <summary>An <see cref="IClaimExtractSource"/> over a list, answering as <see cref="ClaimExtractSourceMongo"/> does (tests, local runs).</summary>
public sealed class InMemoryClaimExtractSource : IClaimExtractSource
{
    public List<Claim> Claims { get; } = [];

    public Task<IReadOnlyList<Claim>> ReadAsync(
        string tenantId, DateTime? from, DateTime to, (DateTime LastUpdated, string Id)? after, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Claim>>(Claims
            .Where(c => c.TenantId == tenantId && c.VersionState != ClaimVersionState.Draft)
            .Where(c => c.LastUpdatedDate <= to && (from is null || c.LastUpdatedDate > from))
            .Where(c => after is null || c.LastUpdatedDate > after.Value.LastUpdated
                        || (c.LastUpdatedDate == after.Value.LastUpdated && string.CompareOrdinal(c.Id, after.Value.Id) > 0))
            .OrderBy(c => c.LastUpdatedDate).ThenBy(c => c.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList());
}
