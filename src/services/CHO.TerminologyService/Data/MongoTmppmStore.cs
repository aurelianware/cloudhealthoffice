using System.Text.RegularExpressions;
using CHO.TerminologyService.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CHO.TerminologyService.Data;

/// <summary>TMPPM rules, editions, diffs and the overrides published from them.</summary>
public interface ITmppmStore
{
    Task<List<TmppmPaRule>> SearchByCodeAsync(string code, string? state, CancellationToken ct = default);
    Task<List<TmppmPaRule>> GetRulesByCategoryAsync(string category, string? state, CancellationToken ct = default);
    Task<List<TmppmCategoryGroup>> GetCategoriesAsync(string state, CancellationToken ct = default);
    Task<List<string>> AutocompleteCodeAsync(string prefix, int maxResults, CancellationToken ct = default);
    Task<List<TmppmEdition>> GetEditionsAsync(int limit, CancellationToken ct = default);
    Task<TmppmDiffReport?> GetDiffAsync(string fromEdition, string toEdition, CancellationToken ct = default);

    Task<int> UpsertRulesAsync(IReadOnlyCollection<TmppmPaRule> rules, CancellationToken ct = default);
    Task SaveEditionAsync(TmppmEdition edition, CancellationToken ct = default);
    Task SaveDiffAsync(TmppmDiffReport diff, CancellationToken ct = default);

    /// <summary>
    /// Publishes the rules as the tenant's ConceptMap overrides (CPT/HCPCS →
    /// urn:cho:pa-determination), so $translate and CRD answer "auth required"
    /// for that tenant only.
    /// </summary>
    Task<TmppmPublishOverridesResult> PublishOverridesAsync(
        string tenantId, string editionId, IReadOnlyCollection<TmppmPaRule> rules, string? actor, CancellationToken ct = default);
}

public sealed class MongoTmppmStore : ITmppmStore
{
    public const string RulesCollection = "tmppm_pa_rules";
    public const string EditionsCollection = "tmppm_editions";
    public const string DiffsCollection = "tmppm_diff_reports";
    public const string PaDeterminationSystem = "urn:cho:pa-determination";
    public const string HcpcsSystem = "https://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";
    public const string CptSystem = "http://www.ama-assn.org/go/cpt";

    private readonly IMongoCollection<TmppmPaRule> _rules;
    private readonly IMongoCollection<TmppmEdition> _editions;
    private readonly IMongoCollection<TmppmDiffReport> _diffs;
    private readonly IMongoCollection<ConceptMapEntry> _entries;
    private readonly IMongoCollection<MapVersion> _versions;
    private readonly ILogger<MongoTmppmStore> _logger;
    private readonly Lazy<Task> _indexes;

    public MongoTmppmStore(IMongoDatabase database, ILogger<MongoTmppmStore> logger)
    {
        _rules = database.GetCollection<TmppmPaRule>(RulesCollection);
        _editions = database.GetCollection<TmppmEdition>(EditionsCollection);
        _diffs = database.GetCollection<TmppmDiffReport>(DiffsCollection);
        _entries = database.GetCollection<ConceptMapEntry>("concept_map_entries");
        _versions = database.GetCollection<MapVersion>("map_versions");
        _logger = logger;
        _indexes = new Lazy<Task>(CreateIndexesAsync);
    }

    // The indexes the portal used to create on its own copy of these collections.
    private async Task CreateIndexesAsync()
    {
        try
        {
            await _rules.Indexes.CreateManyAsync(new[]
            {
                new CreateIndexModel<TmppmPaRule>(Builders<TmppmPaRule>.IndexKeys.Ascending(r => r.RuleId),
                    new CreateIndexOptions { Name = "idx_rule_id" }),
                new CreateIndexModel<TmppmPaRule>(Builders<TmppmPaRule>.IndexKeys.Ascending(r => r.ProcedureCodes),
                    new CreateIndexOptions { Name = "idx_procedure_codes" }),
                new CreateIndexModel<TmppmPaRule>(Builders<TmppmPaRule>.IndexKeys.Ascending(r => r.Category).Ascending(r => r.State),
                    new CreateIndexOptions { Name = "idx_category_state" }),
            });
            await _editions.Indexes.CreateOneAsync(new CreateIndexModel<TmppmEdition>(
                Builders<TmppmEdition>.IndexKeys.Descending(e => e.IngestedAt), new CreateIndexOptions { Name = "idx_ingested_at" }));
        }
        catch (MongoException ex)
        {
            _logger.LogWarning(ex, "TMPPM indexes could not be created; queries still work");
        }
    }

    private Task EnsureIndexesAsync() => _indexes.Value;

    private static FilterDefinition<TmppmPaRule> StateFilter(FilterDefinition<TmppmPaRule> filter, string? state)
        => string.IsNullOrEmpty(state) ? filter : filter & Builders<TmppmPaRule>.Filter.Eq(r => r.State, state);

    public async Task<List<TmppmPaRule>> SearchByCodeAsync(string code, string? state, CancellationToken ct = default)
    {
        await EnsureIndexesAsync();
        var filter = StateFilter(Builders<TmppmPaRule>.Filter.AnyEq(r => r.ProcedureCodes, code.Trim().ToUpperInvariant()), state);
        return await _rules.Find(filter).SortBy(r => r.Category).ToListAsync(ct);
    }

    public async Task<List<TmppmPaRule>> GetRulesByCategoryAsync(string category, string? state, CancellationToken ct = default)
    {
        await EnsureIndexesAsync();
        var filter = StateFilter(Builders<TmppmPaRule>.Filter.Eq(r => r.Category, category), state);
        return await _rules.Find(filter).SortBy(r => r.TmppmRef).ToListAsync(ct);
    }

    public async Task<List<TmppmCategoryGroup>> GetCategoriesAsync(string state, CancellationToken ct = default)
    {
        await EnsureIndexesAsync();
        var rules = await _rules.Find(Builders<TmppmPaRule>.Filter.Eq(r => r.State, state)).ToListAsync(ct);

        var categories = rules
            .GroupBy(r => (r.Category, r.TmppmRef))
            .Select(g => new
            {
                Summary = new TmppmCategorySummary
                {
                    Category = g.Key.Category,
                    TmppmRef = g.Key.TmppmRef,
                    RuleCount = g.Count(),
                    CodeCount = g.SelectMany(r => r.ProcedureCodes).Distinct().Count(),
                },
                RuleType = g.First().RuleType,
            })
            .ToList();

        TmppmCategoryGroup Group(string priority, Func<string, bool> ofType) => new()
        {
            Priority = priority,
            Categories = categories.Where(c => ofType(c.RuleType)).Select(c => c.Summary).OrderBy(c => c.Category).ToList(),
        };

        return new[]
        {
            Group("P1", t => t is "AuthRequired" or "DiagnosisRestriction"),
            Group("P2", t => t is "AgeLimit" or "UnitLimit"),
            Group("P3", t => t is not ("AuthRequired" or "DiagnosisRestriction" or "AgeLimit" or "UnitLimit")),
        }.Where(g => g.Categories.Count > 0).ToList();
    }

    public async Task<List<string>> AutocompleteCodeAsync(string prefix, int maxResults, CancellationToken ct = default)
    {
        var normalized = prefix.Trim().ToUpperInvariant();
        if (normalized.Length == 0)
            return [];

        await EnsureIndexesAsync();
        var filter = Builders<TmppmPaRule>.Filter.Regex(r => r.ProcedureCodes,
            new BsonRegularExpression("^" + Regex.Escape(normalized), "i"));
        var rules = await _rules.Find(filter).Limit(50).ToListAsync(ct);
        return rules.SelectMany(r => r.ProcedureCodes)
            .Where(c => c.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(c => c)
            .Take(maxResults)
            .ToList();
    }

    public async Task<List<TmppmEdition>> GetEditionsAsync(int limit, CancellationToken ct = default)
    {
        await EnsureIndexesAsync();
        return await _editions.Find(FilterDefinition<TmppmEdition>.Empty)
            .SortByDescending(e => e.IngestedAt).Limit(limit).ToListAsync(ct);
    }

    public async Task<TmppmDiffReport?> GetDiffAsync(string fromEdition, string toEdition, CancellationToken ct = default)
        => await _diffs.Find(d => d.FromEdition == fromEdition && d.ToEdition == toEdition)
            .SortByDescending(d => d.GeneratedAt).FirstOrDefaultAsync(ct);

    public async Task<int> UpsertRulesAsync(IReadOnlyCollection<TmppmPaRule> rules, CancellationToken ct = default)
    {
        if (rules.Count == 0)
            return 0;

        await EnsureIndexesAsync();
        var writes = rules.Select(rule => new ReplaceOneModel<TmppmPaRule>(
                Builders<TmppmPaRule>.Filter.Eq(r => r.RuleId, rule.RuleId), rule) { IsUpsert = true })
            .ToList<WriteModel<TmppmPaRule>>();
        await _rules.BulkWriteAsync(writes, cancellationToken: ct);
        return rules.Count;
    }

    public async Task SaveEditionAsync(TmppmEdition edition, CancellationToken ct = default)
    {
        await EnsureIndexesAsync();
        await _editions.ReplaceOneAsync(e => e.EditionId == edition.EditionId, edition,
            new ReplaceOptions { IsUpsert = true }, ct);
    }

    public async Task SaveDiffAsync(TmppmDiffReport diff, CancellationToken ct = default)
        => await _diffs.InsertOneAsync(diff, cancellationToken: ct);

    /// <summary>
    /// Override map version id for a tenant's TMPPM edition (tenant length-prefixed, as CSV/RF2 overrides).
    /// </summary>
    public static string OverrideVersionId(string tenantId, string editionId)
        => $"override:{tenantId.Length}:{tenantId}:tmppm-{editionId}";

    /// <summary>
    /// Entry id: names the tenant, so two tenants publishing the same edition
    /// never address one another's entries (the old id
    /// <c>tmppm-{state}-{code}-{ruleType}</c> was the same for every tenant).
    /// </summary>
    public static string OverrideEntryId(string tenantId, string state, string code, string ruleType)
        => $"tmppm:{tenantId.Length}:{tenantId}:{state}-{code}-{ruleType}".ToLowerInvariant();

    public async Task<TmppmPublishOverridesResult> PublishOverridesAsync(
        string tenantId, string editionId, IReadOnlyCollection<TmppmPaRule> rules, string? actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("A tenant is required; overrides are never published without one.");

        var versionId = OverrideVersionId(tenantId, editionId);
        var entries = new List<ConceptMapEntry>();
        foreach (var rule in rules.Where(r => r.ProcedureCodes.Count > 0))
        {
            var system = rule.CodeSystem == "HCPCS" ? HcpcsSystem : CptSystem;
            foreach (var code in rule.ProcedureCodes)
            {
                entries.Add(new ConceptMapEntry
                {
                    Id = OverrideEntryId(tenantId, rule.State, code, rule.RuleType),
                    SourceSystem = system,
                    SourceCode = code,
                    SourceDisplay = rule.Category,
                    TargetSystem = PaDeterminationSystem,
                    TargetCode = rule.AuthRequired ? "auth-required" : "no-auth",
                    TargetDisplay = rule.AuthRequired
                        ? $"Prior authorization required — {rule.Category}"
                        : $"No prior authorization — {rule.Category}",
                    Equivalence = "equivalent",
                    MapGroupId = $"tmppm-{rule.State}-{rule.TmppmRef}",
                    Priority = 1,
                    Rule = new MapRule
                    {
                        RuleType = "StateSpecific",
                        StateCode = rule.State,
                        AgeMin = rule.AgeLimit?.MinAge,
                        AgeMax = rule.AgeLimit?.MaxAge,
                    },
                    MapVersionId = versionId,
                    IsOverride = true,
                    TenantId = tenantId,
                });
            }
        }

        if (entries.Count > 0)
        {
            // Replace by id within the tenant: a code whose determination
            // flips (auth-required <-> no-auth) is updated in place.
            var writes = entries.Select(e => new ReplaceOneModel<ConceptMapEntry>(
                    Builders<ConceptMapEntry>.Filter.Eq(x => x.Id, e.Id) & Builders<ConceptMapEntry>.Filter.Eq(x => x.TenantId, tenantId), e)
                { IsUpsert = true })
                .ToList<WriteModel<ConceptMapEntry>>();
            await _entries.BulkWriteAsync(writes, cancellationToken: ct);

            var first = entries[0];
            await _versions.ReplaceOneAsync(v => v.Id == versionId, new MapVersion
            {
                Id = versionId,
                MapName = $"TMPPM-{rules.First().State}",
                Version = editionId,
                SourceSystem = first.SourceSystem,
                TargetSystem = PaDeterminationSystem,
                ImportedAt = DateTime.UtcNow,
                IsActive = true,
                EntryCount = entries.Count,
                TenantId = tenantId,
                ImportedBy = actor,
            }, new ReplaceOptions { IsUpsert = true }, ct);
        }

        return new TmppmPublishOverridesResult { MapVersionId = versionId, OverridesPublished = entries.Count };
    }
}
