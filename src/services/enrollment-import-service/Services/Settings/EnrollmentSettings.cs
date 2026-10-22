using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace EnrollmentImportService.Services.Settings;

/// <summary>
/// The levels an enrollment setting resolves through, least to most specific
/// (enrollment-operations.md §6): a line-of-business default, overridden per
/// benefit plan, then per sponsor. Sponsor policy and class slot in after
/// <see cref="Sponsor"/> when the sponsor policy record exists (step 4).
/// </summary>
public enum EnrollmentSettingLevel
{
    LineOfBusiness = 0,
    BenefitPlan = 1,
    Sponsor = 2,
}

/// <summary>One level of a lookup: the level and its key (a line of business, plan id or group number).</summary>
public sealed record EnrollmentSettingScope(EnrollmentSettingLevel Level, string Key);

/// <summary>Setting names. Each is an integer for now; retro limits and waiting periods join them in step 4.</summary>
public static class EnrollmentSettingKeys
{
    /// <summary>Days a dependent whose subscriber is not on file is held before it reaches the Matching queue.</summary>
    public const string OrphanGraceDays = "orphanGraceDays";

    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> Ranges = new Dictionary<string, (int, int)>
    {
        [OrphanGraceDays] = (0, 90),
    };

    public static bool IsKnown(string key) => Ranges.ContainsKey(key);
}

/// <summary>Line-of-business names, as sponsor-service's <c>LineOfBusiness</c> enum spells them.</summary>
public static class LinesOfBusiness
{
    public const string Commercial = "Commercial";
    public const string Medicare = "Medicare";
    public const string Medicaid = "Medicaid";
    public const string Exchange = "Exchange";
    public const string Tricare = "TRICARE";
    public const string Va = "VA";

    private static readonly string[] ByNumber = ["", Commercial, Medicare, Medicaid, Exchange, Tricare, Va];

    /// <summary>The canonical name for a sponsor-service value, numeric (its default JSON) or by name.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value, out var n)) return n > 0 && n < ByNumber.Length ? ByNumber[n] : null;
        return ByNumber.Skip(1).FirstOrDefault(name => string.Equals(name, value.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The platform's built-in defaults per line of business, used when the
/// tenant has set nothing at any level. Orphan grace: one file cycle for
/// commercial groups (weekly files are the common cadence); none where the
/// sponsor sends complete enrollment groups (Marketplace) or is the source of
/// truth (Medicaid) — enrollment-operations.md §10, decision 5.
/// </summary>
public static class EnrollmentSettingDefaults
{
    private static readonly Dictionary<string, Dictionary<string, int>> Defaults = new()
    {
        [EnrollmentSettingKeys.OrphanGraceDays] = new(StringComparer.OrdinalIgnoreCase)
        {
            [LinesOfBusiness.Commercial] = 7,
            [LinesOfBusiness.Exchange] = 0,
            [LinesOfBusiness.Medicaid] = 0,
        },
    };

    /// <summary>The default for a setting and line of business; 0 when the line is unknown or has none.</summary>
    public static int For(string settingKey, string? lineOfBusiness) =>
        lineOfBusiness is not null && Defaults.TryGetValue(settingKey, out var byLine) && byLine.TryGetValue(lineOfBusiness, out var value)
            ? value
            : 0;

    public static IReadOnlyDictionary<string, int> ForSetting(string settingKey) =>
        Defaults.TryGetValue(settingKey, out var byLine) ? byLine : new Dictionary<string, int>();
}

/// <summary>A tenant's override at one level: the values it sets there (unset values fall through).</summary>
public sealed class EnrollmentSettingOverride
{
    /// <summary><c>{tenant}|{level}|{key}</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public EnrollmentSettingLevel Level { get; set; }
    public string Key { get; set; } = string.Empty;
    public Dictionary<string, int> Values { get; set; } = new();
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    public static string IdFor(string tenantId, EnrollmentSettingLevel level, string key) => $"{tenantId}|{level}|{key}";
}

/// <summary>A resolved value and the level it came from (Default when nothing overrode it).</summary>
public sealed record ResolvedEnrollmentSetting(string Setting, int Value, string Source, string? SourceKey);

public interface IEnrollmentSettingsStore
{
    Task<IReadOnlyList<EnrollmentSettingOverride>> GetAsync(string tenantId, IReadOnlyCollection<EnrollmentSettingScope> scopes, CancellationToken ct = default);
    Task<IReadOnlyList<EnrollmentSettingOverride>> ListAsync(string tenantId, CancellationToken ct = default);
    Task UpsertAsync(EnrollmentSettingOverride value, CancellationToken ct = default);
    Task<bool> DeleteAsync(string tenantId, EnrollmentSettingLevel level, string key, CancellationToken ct = default);
}

/// <summary>Overrides, one document per tenant, level and key (collection <c>enrollment-settings</c>).</summary>
public sealed class MongoEnrollmentSettingsStore(IMongoDatabase database) : IEnrollmentSettingsStore
{
    public const string CollectionName = "enrollment-settings";

    private readonly IMongoCollection<EnrollmentSettingOverride> _collection =
        database.GetCollection<EnrollmentSettingOverride>(CollectionName);

    public async Task<IReadOnlyList<EnrollmentSettingOverride>> GetAsync(string tenantId, IReadOnlyCollection<EnrollmentSettingScope> scopes, CancellationToken ct = default)
    {
        if (scopes.Count == 0) return [];
        var ids = scopes.Select(s => EnrollmentSettingOverride.IdFor(tenantId, s.Level, s.Key)).ToList();
        return await _collection.Find(x => x.TenantId == tenantId && ids.Contains(x.Id)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EnrollmentSettingOverride>> ListAsync(string tenantId, CancellationToken ct = default) =>
        await _collection.Find(x => x.TenantId == tenantId).SortBy(x => x.Level).ThenBy(x => x.Key).ToListAsync(ct);

    public Task UpsertAsync(EnrollmentSettingOverride value, CancellationToken ct = default) =>
        _collection.ReplaceOneAsync(x => x.TenantId == value.TenantId && x.Id == value.Id, value,
            new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<bool> DeleteAsync(string tenantId, EnrollmentSettingLevel level, string key, CancellationToken ct = default)
    {
        var id = EnrollmentSettingOverride.IdFor(tenantId, level, key);
        var result = await _collection.DeleteOneAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        return result.DeletedCount > 0;
    }
}

/// <summary>For hosts without MongoDB (tests, local runs without a database).</summary>
public sealed class InMemoryEnrollmentSettingsStore : IEnrollmentSettingsStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EnrollmentSettingOverride> _items = new();

    public Task<IReadOnlyList<EnrollmentSettingOverride>> GetAsync(string tenantId, IReadOnlyCollection<EnrollmentSettingScope> scopes, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<EnrollmentSettingOverride>>(scopes
                .Select(s => EnrollmentSettingOverride.IdFor(tenantId, s.Level, s.Key))
                .Where(_items.ContainsKey).Select(id => _items[id]).ToList());
    }

    public Task<IReadOnlyList<EnrollmentSettingOverride>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<EnrollmentSettingOverride>>(_items.Values.Where(x => x.TenantId == tenantId)
                .OrderBy(x => x.Level).ThenBy(x => x.Key).ToList());
    }

    public Task UpsertAsync(EnrollmentSettingOverride value, CancellationToken ct = default)
    {
        lock (_gate) _items[value.Id] = value;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string tenantId, EnrollmentSettingLevel level, string key, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_items.Remove(EnrollmentSettingOverride.IdFor(tenantId, level, key)));
    }
}

/// <summary>
/// Resolves a setting through the chain of levels: the most specific level
/// that sets it wins, then the built-in line-of-business default. Callers
/// pass whatever levels they know; a level whose key is unknown is skipped.
/// </summary>
public sealed class EnrollmentSettingsResolver(IEnrollmentSettingsStore store)
{
    public async Task<ResolvedEnrollmentSetting> ResolveAsync(string tenantId, string settingKey,
        string? lineOfBusiness, string? benefitPlanId, string? sponsorGroupNumber, CancellationToken ct = default)
    {
        // Most specific first.
        var chain = new List<EnrollmentSettingScope>();
        if (!string.IsNullOrWhiteSpace(sponsorGroupNumber)) chain.Add(new(EnrollmentSettingLevel.Sponsor, sponsorGroupNumber.Trim()));
        if (!string.IsNullOrWhiteSpace(benefitPlanId)) chain.Add(new(EnrollmentSettingLevel.BenefitPlan, benefitPlanId.Trim()));
        if (!string.IsNullOrWhiteSpace(lineOfBusiness)) chain.Add(new(EnrollmentSettingLevel.LineOfBusiness, lineOfBusiness.Trim()));

        var overrides = (await store.GetAsync(tenantId, chain, ct))
            .ToDictionary(o => (o.Level, o.Key));
        foreach (var scope in chain)
        {
            if (overrides.TryGetValue((scope.Level, scope.Key), out var o) && o.Values.TryGetValue(settingKey, out var value))
                return new ResolvedEnrollmentSetting(settingKey, value, scope.Level.ToString(), scope.Key);
        }
        return new ResolvedEnrollmentSetting(settingKey, EnrollmentSettingDefaults.For(settingKey, lineOfBusiness), "Default", lineOfBusiness);
    }
}
