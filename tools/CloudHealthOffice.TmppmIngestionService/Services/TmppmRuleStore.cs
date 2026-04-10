using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using CHO.TmppmIngestionService.Models;

namespace CHO.TmppmIngestionService.Services;

/// <summary>
/// Persists extracted TMPPM rules to MongoDB as ConceptMapEntry overrides.
/// Targets the same collection used by CHO.TerminologyService for runtime lookups.
/// </summary>
public class TmppmRuleStore(IMongoDatabase database, ILogger<TmppmRuleStore> logger)
{
    private IMongoCollection<TmppmPaRule> Rules =>
        database.GetCollection<TmppmPaRule>("tmppm_pa_rules");

    private IMongoCollection<TmppmEdition> Editions =>
        database.GetCollection<TmppmEdition>("tmppm_editions");

    private IMongoCollection<TmppmDiffReport> Diffs =>
        database.GetCollection<TmppmDiffReport>("tmppm_diff_reports");

    private IMongoCollection<ConceptMapEntryOverride> ConceptMapOverrides =>
        database.GetCollection<ConceptMapEntryOverride>("concept_map_entries");

    /// <summary>
    /// Upsert extracted PA rules. Uses RuleId as the unique key.
    /// </summary>
    public async Task<int> UpsertRulesAsync(IEnumerable<TmppmPaRule> rules)
    {
        var count = 0;
        var bulkOps = new List<WriteModel<TmppmPaRule>>();

        foreach (var rule in rules)
        {
            var filter = Builders<TmppmPaRule>.Filter.Eq(r => r.RuleId, rule.RuleId);
            bulkOps.Add(new ReplaceOneModel<TmppmPaRule>(filter, rule) { IsUpsert = true });
            count++;
        }

        if (bulkOps.Count > 0)
        {
            var result = await Rules.BulkWriteAsync(bulkOps);
            logger.LogInformation("Upserted {Count} PA rules ({Inserted} new, {Modified} updated)",
                count, result.InsertedCount, result.ModifiedCount);
        }

        return count;
    }

    /// <summary>
    /// Convert TmppmPaRules to ConceptMapEntry overrides and upsert into the
    /// terminology service collection. This is how TMPPM rules become queryable
    /// via the FHIR $translate endpoint and CRD server.
    /// </summary>
    public async Task<int> PublishAsConceptMapOverridesAsync(
        IEnumerable<TmppmPaRule> rules, string mapVersionId, string? tenantId = null)
    {
        var overrides = new List<ConceptMapEntryOverride>();

        foreach (var rule in rules.Where(r => r.ProcedureCodes.Count > 0))
        {
            foreach (var code in rule.ProcedureCodes)
            {
                var system = rule.CodeSystem == "HCPCS"
                    ? "https://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets"
                    : "http://www.ama-assn.org/go/cpt";

                var overrideEntry = new ConceptMapEntryOverride
                {
                    Id = $"tmppm-{rule.State}-{code}-{rule.RuleType}".ToLowerInvariant(),
                    SourceSystem = system,
                    SourceCode = code,
                    SourceDisplay = rule.Category,
                    TargetSystem = "urn:cho:pa-determination",
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
                        State = rule.State,
                        AgeMin = rule.AgeLimit?.MinAge,
                        AgeMax = rule.AgeLimit?.MaxAge,
                    },
                    MapVersionId = mapVersionId,
                    IsOverride = true,
                    TenantId = tenantId
                };

                overrides.Add(overrideEntry);
            }
        }

        if (overrides.Count > 0)
        {
            var bulkOps = overrides.Select(o =>
                new ReplaceOneModel<ConceptMapEntryOverride>(
                    Builders<ConceptMapEntryOverride>.Filter.Eq(e => e.Id, o.Id), o)
                { IsUpsert = true })
                .ToList<WriteModel<ConceptMapEntryOverride>>();

            var result = await ConceptMapOverrides.BulkWriteAsync(bulkOps);
            logger.LogInformation("Published {Count} ConceptMapEntry overrides for state {State}",
                overrides.Count, rules.FirstOrDefault()?.State ?? "?");
        }

        return overrides.Count;
    }

    /// <summary>
    /// Save edition metadata for version tracking.
    /// </summary>
    public async Task SaveEditionAsync(TmppmEdition edition)
    {
        var filter = Builders<TmppmEdition>.Filter.Eq(e => e.EditionId, edition.EditionId);
        await Editions.ReplaceOneAsync(filter, edition, new ReplaceOptions { IsUpsert = true });
        logger.LogInformation("Saved edition {Id} with {Count} chapters", edition.EditionId, edition.Chapters.Count);
    }

    /// <summary>
    /// Get the most recent edition for change detection.
    /// </summary>
    public async Task<TmppmEdition?> GetLatestEditionAsync()
    {
        return await Editions
            .Find(Builders<TmppmEdition>.Filter.Empty)
            .SortByDescending(e => e.EditionId)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Save a diff report.
    /// </summary>
    public async Task SaveDiffReportAsync(TmppmDiffReport diff)
    {
        await Diffs.InsertOneAsync(diff);
        logger.LogInformation("Saved diff report: {From} → {To} ({Added} added, {Modified} modified, {Removed} removed)",
            diff.FromEdition, diff.ToEdition, diff.AddedCount, diff.ModifiedCount, diff.RemovedCount);
    }
}
