using CHO.TerminologyService.Models;

namespace CHO.TerminologyService.Services.Loaders;

/// <summary>
/// Loads SNOMED CT crosswalk maps from NLM RF2 (Release Format 2) files.
/// 
/// The NLM SNOMED-to-ICD-10-CM map ships as part of the US Edition of SNOMED CT
/// in RF2 format. Key files:
///   - der2_iisssccRefset_ExtendedMapFull_US*.txt (the actual map entries)
///   - sct2_Description*.txt (concept descriptions for display names)
/// 
/// RF2 is tab-delimited with a header row. Map entries include:
///   id, effectiveTime, active, moduleId, refsetId, referencedComponentId,
///   mapGroup, mapPriority, mapRule, mapAdvice, mapTarget, correlationId, mapCategoryId
/// 
/// referencedComponentId = SNOMED concept ID (source)
/// mapTarget = ICD-10-CM code (target)
/// mapRule = contextual rule expression (e.g., "IFA 248152002 | Female")
/// mapGroup/mapPriority = ordering within multi-target mappings
/// </summary>
public class Rf2MapLoader : IMapLoader
{
    private readonly IConceptMapRepository _repository;
    private readonly ILogger<Rf2MapLoader> _logger;

    public string Format => "RF2";

    // Well-known SNOMED refset IDs
    private const string ICD10CM_MAP_REFSET = "6011000124106"; // US SNOMED-to-ICD-10-CM

    public Rf2MapLoader(IConceptMapRepository repository, ILogger<Rf2MapLoader> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    public async Task<MapLoadResult> LoadAsync(Stream source, MapLoadOptions options, CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new MapLoadResult();
        var entries = new List<ConceptMapEntry>();
        var errors = new List<string>();

        var mapVersionId = $"{options.MapName}-{options.Version}-{DateTime.UtcNow:yyyyMMddHHmmss}";

        try
        {
            using var reader = new StreamReader(source);
            var headerLine = await reader.ReadLineAsync(ct);
            if (headerLine == null)
            {
                result.Errors.Add("Empty file");
                return result;
            }

            var headers = headerLine.Split('\t');
            var headerMap = new Dictionary<string, int>();
            for (int i = 0; i < headers.Length; i++)
            {
                headerMap[headers[i].Trim()] = i;
            }

            // Validate expected columns exist
            var requiredColumns = new[] { "referencedComponentId", "mapTarget", "active", "mapGroup", "mapPriority" };
            foreach (var col in requiredColumns)
            {
                if (!headerMap.ContainsKey(col))
                {
                    result.Errors.Add($"Missing required column: {col}");
                    return result;
                }
            }

            int lineNumber = 1;
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var fields = line.Split('\t');
                    var active = GetField(fields, headerMap, "active");

                    // Skip inactive entries
                    if (active != "1")
                    {
                        result.EntriesSkipped++;
                        continue;
                    }

                    var snomedCode = GetField(fields, headerMap, "referencedComponentId");
                    var targetCode = GetField(fields, headerMap, "mapTarget");

                    // Skip entries with no target (advice-only rows)
                    if (string.IsNullOrEmpty(targetCode))
                    {
                        result.EntriesSkipped++;
                        continue;
                    }

                    var mapGroup = GetField(fields, headerMap, "mapGroup");
                    var mapPriority = GetField(fields, headerMap, "mapPriority");
                    var mapRule = GetFieldOptional(fields, headerMap, "mapRule");
                    var mapAdvice = GetFieldOptional(fields, headerMap, "mapAdvice");

                    var entry = new ConceptMapEntry
                    {
                        Id = $"{mapVersionId}:{snomedCode}:{targetCode}:{mapGroup}",
                        SourceSystem = options.SourceSystem,
                        SourceCode = snomedCode,
                        SourceDisplay = "", // Populated in a second pass from description files
                        TargetSystem = options.TargetSystem,
                        TargetCode = targetCode,
                        TargetDisplay = "", // Could be enriched from ICD-10-CM descriptions
                        Equivalence = DetermineEquivalence(mapAdvice),
                        MapGroupId = mapGroup,
                        Priority = int.TryParse(mapPriority, out var p) ? p : 1,
                        Rule = ParseMapRule(mapRule, mapAdvice),
                        MapVersionId = mapVersionId,
                        IsOverride = options.IsOverride,
                        TenantId = options.TenantId
                    };

                    entries.Add(entry);
                }
                catch (Exception ex)
                {
                    errors.Add($"Line {lineNumber}: {ex.Message}");
                    if (errors.Count > 100)
                    {
                        errors.Add("Too many errors, stopping parse");
                        break;
                    }
                }
            }

            // Bulk insert
            if (entries.Count > 0)
            {
                _logger.LogInformation("Bulk inserting {Count} entries for map {MapName} v{Version}",
                    entries.Count, SanitizeForLog(options.MapName), SanitizeForLog(options.Version));

                await _repository.BulkInsertAsync(entries, ct);

                // Save map version
                var mapVersion = new MapVersion
                {
                    Id = mapVersionId,
                    MapName = options.MapName,
                    Version = options.Version,
                    SourceSystem = options.SourceSystem,
                    TargetSystem = options.TargetSystem,
                    ImportedAt = DateTime.UtcNow,
                    IsActive = true,
                    EntryCount = entries.Count
                };

                // Deactivate previous versions
                await _repository.DeactivatePreviousVersionsAsync(options.MapName, mapVersionId, ct);
                await _repository.SaveMapVersionAsync(mapVersion, ct);
            }

            result.Success = true;
            result.EntriesLoaded = entries.Count;
            result.MapVersionId = mapVersionId;
            result.Errors = errors;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load RF2 map file");
            result.Errors.Add($"Fatal: {ex.Message}");
        }

        stopwatch.Stop();
        result.Duration = stopwatch.Elapsed;

        _logger.LogInformation("RF2 load complete: {Loaded} loaded, {Skipped} skipped, {Errors} errors in {Duration}ms",
            result.EntriesLoaded, result.EntriesSkipped, result.Errors.Count, result.Duration.TotalMilliseconds);

        return result;
    }

    /// <summary>
    /// Parse the NLM map rule expression into our MapRule model.
    /// NLM rules use IFA (IF Applicable) expressions like:
    ///   "IFA 248152002 | Female (finding) |"
    ///   "IFA 445518008 | Age at onset of clinical finding (observable entity) | >= 15 years"
    /// </summary>
    private MapRule? ParseMapRule(string? mapRule, string? mapAdvice)
    {
        if (string.IsNullOrEmpty(mapRule) || mapRule == "TRUE")
            return null;

        var rule = new MapRule();

        // Parse IFA age rules
        if (mapRule.Contains("Age", StringComparison.OrdinalIgnoreCase) &&
            (mapRule.Contains(">=") || mapRule.Contains("<=") || mapRule.Contains("<") || mapRule.Contains(">")))
        {
            rule.RuleType = "Age";
            // Extract age value (simplified parser - production would use regex)
            var parts = mapRule.Split(new[] { ">=", "<=", ">", "<" }, StringSplitOptions.None);
            if (parts.Length >= 2)
            {
                var ageStr = new string(parts.Last().Where(c => char.IsDigit(c)).ToArray());
                if (int.TryParse(ageStr, out var age))
                {
                    if (mapRule.Contains(">=")) rule.AgeMin = age;
                    else if (mapRule.Contains(">")) rule.AgeMin = age + 1;
                    else if (mapRule.Contains("<=")) rule.AgeMax = age;
                    else if (mapRule.Contains("<")) rule.AgeMax = age - 1;
                }
            }
            return rule;
        }

        // Parse IFA gender rules
        if (mapRule.Contains("248152002")) // SNOMED: Female
        {
            rule.RuleType = "Gender";
            rule.Gender = "female";
            return rule;
        }
        if (mapRule.Contains("248153007")) // SNOMED: Male
        {
            rule.RuleType = "Gender";
            rule.Gender = "male";
            return rule;
        }

        // For complex rules, store the raw expression for future FHIRPath evaluation
        rule.RuleType = "Custom";
        rule.Expression = mapRule;
        return rule;
    }

    private string DetermineEquivalence(string? mapAdvice)
    {
        if (string.IsNullOrEmpty(mapAdvice)) return "equivalent";
        if (mapAdvice.Contains("POSSIBLE EQUIVALENCE")) return "inexact";
        if (mapAdvice.Contains("MAP SOURCE CONCEPT IS MORE SPECIFIC")) return "wider";
        if (mapAdvice.Contains("MAP SOURCE CONCEPT IS LESS SPECIFIC")) return "narrower";
        return "equivalent";
    }

    private string GetField(string[] fields, Dictionary<string, int> headerMap, string columnName)
    {
        if (!headerMap.TryGetValue(columnName, out var idx) || idx >= fields.Length)
            throw new InvalidOperationException($"Column '{columnName}' not found or out of range");
        return fields[idx].Trim();
    }

    private string? GetFieldOptional(string[] fields, Dictionary<string, int> headerMap, string columnName)
    {
        if (!headerMap.TryGetValue(columnName, out var idx) || idx >= fields.Length)
            return null;
        var value = fields[idx].Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
